using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Re-arming leftover "relic" wrecks so the normal recovery chain clears them.
    ///
    /// Relics are damaged cars stranded by a failed tow: stripped of InvolvedInAccident, left with
    /// a dead Controller, and - if destroyed - marked cleared, so vanilla stopped requesting
    /// recovery for them. They are invisible to every accident-based query, which is why they sat
    /// on the map indefinitely.
    ///
    /// Rather than deleting them, they are re-armed - drop the stale controller and request, reset
    /// the cleared flag - so vanilla raises a fresh maintenance request and a truck hauls them away
    /// visibly. A watchdog deletes one only if nothing recovers it within the timeout.
    /// </summary>
    internal sealed class TowRelics
    {
        private readonly TowingContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private SimulationSystem m_SimulationSystem => m_Ctx.Simulation;
        private HashSet<Entity> m_TrucksWithLoad => m_Ctx.TrucksWithLoad;
        private HashSet<Entity> m_OurTows => m_Ctx.OurTows;
        private Dictionary<Entity, float3> m_TowTravelDir => m_Ctx.TowTravelDir;
        private Dictionary<Entity, float3> m_LastTruckPos => m_Ctx.LastTruckPos;
        private Dictionary<Entity, uint> m_RelicArmedFrame => m_Ctx.RelicArmedFrame;
        private EntityQuery m_OrphanQuery => m_Ctx.OrphanQuery;
        private EntityQuery m_TrailerOrphanQuery => m_Ctx.TrailerOrphanQuery;
        private EntityQuery m_ArmedRelicQuery => m_Ctx.ArmedRelicQuery;
        private EntityQuery m_RelicDiagQuery => m_Ctx.RelicDiagQuery;
        private EntityQuery m_RelicSweepDryRun => m_Ctx.RelicSweepDryRun;
        private EntityQuery m_AccidentSiteQuery => m_Ctx.AccidentSiteQuery;

        public TowRelics(TowingContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Sebastian's idea: don't hard-delete relics, recover them like any wreck. Relics are
        /// stranded leftovers of a failed tow (InvolvedInAccident stripped, a dead null Controller
        /// left on them, Destroyed ones marked cleared) - so vanilla stopped asking to recover them
        /// and OrphanSweep skips them (null Controller). We RE-ARM each: drop the dead Controller,
        /// drop any stale MaintenanceConsumer, and reset Destroyed.m_Cleared - then
        /// Game.Simulation.DamagedVehicleSystem (query: Damaged+Stopped+Car, no InvolvedInAccident
        /// needed) raises a fresh maintenance request and a recovery vehicle is dispatched. If it is
        /// our Abschleppwagen, TryHookup (relaxed to accept the RelicRecovery tag) hauls it to the
        /// depot; a stock van clears it in place. Either way it is removed through the normal chain.
        /// FALLBACK: if nothing recovers it within kRelicRecoveryTimeout (e.g. fully boxed in, no
        /// route), the watchdog SafeDeletes it so a relic can never linger forever.
        /// </summary>
        public void RelicRecoveryPass(uint frame, Setting setting)
        {
            // Phase 1 - re-arm newly found relics (they still carry the dead tow Controller).
            if (!m_RelicSweepDryRun.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> relics = m_RelicSweepDryRun.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < relics.Length; i++)
                    {
                        Entity relic = relics[i];
                        // Drop the dead null Controller (points at Entity.Null; blocks hookup and
                        // makes OrphanSweep skip it).
                        if (EntityManager.HasComponent<Controller>(relic))
                        {
                            EntityManager.RemoveComponent<Controller>(relic);
                        }
                        // Drop any stale recovery request so DamagedVehicleSystem raises a fresh one.
                        if (EntityManager.HasComponent<Game.Simulation.MaintenanceConsumer>(relic))
                        {
                            Entity req = EntityManager.GetComponentData<Game.Simulation.MaintenanceConsumer>(relic).m_Request;
                            if (req != Entity.Null && EntityManager.Exists(req))
                            {
                                EntityManager.AddComponent<Deleted>(req);
                            }
                            EntityManager.RemoveComponent<Game.Simulation.MaintenanceConsumer>(relic);
                        }
                        // Un-clear a Destroyed wreck so DamagedVehicleSystem requests recovery again
                        // (it removes the consumer while m_Cleared >= 1).
                        if (EntityManager.HasComponent<Destroyed>(relic))
                        {
                            Destroyed d = EntityManager.GetComponentData<Destroyed>(relic);
                            d.m_Cleared = 0f;
                            EntityManager.SetComponentData(relic, d);
                        }
                        EntityManager.AddComponent<RelicRecovery>(relic);
                        m_RelicArmedFrame[relic] = frame;
                        if (setting.VerboseLogging)
                        {
                            Mod.Log.Info($"[relicrecovery] armed car={relic.Index} for recovery");
                        }
                    }
                }
                finally
                {
                    relics.Dispose();
                }
            }

            // Phase 2 - watchdog over armed relics (delete fallback).
            if (m_ArmedRelicQuery.IsEmptyIgnoreFilter)
            {
                return;
            }
            NativeArray<Entity> armed = m_ArmedRelicQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < armed.Length; i++)
                {
                    Entity relic = armed[i];
                    // Actively on our hook now - let the tow finish (it will SafeDelete at the depot).
                    if (EntityManager.HasComponent<TowMarker>(relic))
                    {
                        continue;
                    }
                    // Re-seed the timer after a load (dict is session-only, the tag persisted).
                    if (!m_RelicArmedFrame.TryGetValue(relic, out uint armedFrame))
                    {
                        m_RelicArmedFrame[relic] = frame;
                        continue;
                    }
                    if (frame - armedFrame > kRelicRecoveryTimeout)
                    {
                        m_Ctx.SafeDelete(relic);
                        m_RelicArmedFrame.Remove(relic);
                        if (setting.VerboseLogging)
                        {
                            Mod.Log.Info($"[relicrecovery] fallback delete car={relic.Index} (not recovered in time)");
                        }
                    }
                }
            }
            finally
            {
                armed.Dispose();
            }
        }

        /// <summary>
        /// LOG-ONLY. Reports leftover "relic" wrecks: damaged/destroyed cars that no longer carry
        /// InvolvedInAccident (the game still shows them as "Verkehrsunfall" via a lingering
        /// AccidentSite/icon, but they are invisible to every accident-based query, so none of our
        /// despawn logic can see them). Dumps each one's telltale state so the eventual relic
        /// sweeper can target them precisely, and flags whether it already carries our persistent
        /// TowMarker (relics from before the tag existed will not). Deletes NOTHING.
        /// </summary>
        public void RelicDiagnostic()
        {
            if (m_RelicDiagQuery.IsEmptyIgnoreFilter)
            {
                Mod.Log.Info($"[relic] none (damaged/destroyed cars without InvolvedInAccident); " +
                    $"accidentSites={m_AccidentSiteQuery.CalculateEntityCount()}");
                return;
            }
            NativeArray<Entity> cars = m_RelicDiagQuery.ToEntityArray(Allocator.Temp);
            try
            {
                int tagged = 0;
                int logged = 0;
                for (int i = 0; i < cars.Length; i++)
                {
                    Entity car = cars[i];
                    bool hasCtrl = EntityManager.HasComponent<Controller>(car);
                    bool ctrlIsRecovery = hasCtrl &&
                        EntityManager.GetComponentData<Controller>(car).m_Controller != car &&
                        EntityManager.Exists(EntityManager.GetComponentData<Controller>(car).m_Controller) &&
                        EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(
                            EntityManager.GetComponentData<Controller>(car).m_Controller);
                    bool tow = EntityManager.HasComponent<TowMarker>(car);
                    if (tow)
                    {
                        tagged++;
                    }
                    // A car mid-tow legitimately matches (damaged, no InvolvedInAccident, our
                    // controller); only the ones NOT actively on a live recovery truck are relics.
                    if (logged < 12 && !ctrlIsRecovery)
                    {
                        logged++;
                        // Resolve the carrier so we understand WHY OrphanSweep leaves it: it only
                        // deletes when the carrier is gone or a parked recovery truck.
                        Entity carrier = hasCtrl ? EntityManager.GetComponentData<Controller>(car).m_Controller : Entity.Null;
                        bool carrierExists = carrier != Entity.Null && carrier != car && EntityManager.Exists(carrier) &&
                            !EntityManager.HasComponent<Deleted>(carrier);
                        bool carrierRecov = carrierExists && EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(carrier);
                        bool carrierParked = carrierExists && EntityManager.HasComponent<ParkedCar>(carrier);
                        Mod.Log.Info($"[relic] car={car.Index} " +
                            $"dstr={(EntityManager.HasComponent<Destroyed>(car) ? 1 : 0)} " +
                            $"dmg={(EntityManager.HasComponent<Damaged>(car) ? 1 : 0)} " +
                            $"ctrl={(hasCtrl ? 1 : 0)} tow={(tow ? 1 : 0)} " +
                            $"stop={(EntityManager.HasComponent<Stopped>(car) ? 1 : 0)} " +
                            $"mov={(EntityManager.HasComponent<Moving>(car) ? 1 : 0)} " +
                            $"outctl={(EntityManager.HasComponent<Game.Vehicles.OutOfControl>(car) ? 1 : 0)} " +
                            $"owner={(EntityManager.HasComponent<Owner>(car) ? 1 : 0)} " +
                            $"ctlane={(EntityManager.HasComponent<CarTrailerLane>(car) ? 1 : 0)} " +
                            $"cartrl={(EntityManager.HasComponent<CarTrailer>(car) ? 1 : 0)} " +
                            $"| carrier={carrier.Index} exists={(carrierExists ? 1 : 0)} " +
                            $"recov={(carrierRecov ? 1 : 0)} parked={(carrierParked ? 1 : 0)}");
                    }
                }
                Mod.Log.Info($"[relic] {cars.Length} damaged/destroyed car(s) without InvolvedInAccident " +
                    $"({tagged} carry our TowMarker); accidentSites={m_AccidentSiteQuery.CalculateEntityCount()}; " +
                    $"sweepDryRun={m_RelicSweepDryRun.CalculateEntityCount()}");
            }
            finally
            {
                cars.Dispose();
            }
        }
    }
}

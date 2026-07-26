using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Re-arms stranded relic wrecks the normal relic sweep can never reach: a wreck left carrying
    /// our persistent <see cref="TowMarker"/> after its tow was ABANDONED.
    ///
    /// The field failure this fixes (wreck 206440): a recovery run that gives up leaves the
    /// TowMarker AND a self-pointing Controller on the wreck. Every existing cleanup path then
    /// treats it as "actively on our hook, leave it alone" and skips it:
    ///  - the relic re-arm query excludes TowMarker, so its Controller is never dropped,
    ///  - OrphanSweep only deletes when the carrier is gone or parked (a self-controller "exists"),
    ///  - the relic watchdog skips TowMarker wrecks.
    /// So it sits forever - InvolvedInAccident already stripped, invisible to every accident query,
    /// no recovery ever dispatched (recov=0 in the [relic] dump for minutes on end).
    ///
    /// A marked wreck is stranded rather than genuinely being towed when NO tow truck is actually
    /// working it. We test that the cheap way: no truck that is ACTIVELY carrying a load
    /// (TrucksWithLoad) sits within kRelicRearmTruckRange (500 m). A truck that is not actively
    /// towing - idle, or driving past - does NOT count; only a live haul is a recovery we must not
    /// disturb. When nothing is working it, we re-arm exactly as the relic pass would (drop the
    /// stale TowMarker + Controller + request, tag RelicRecovery) so vanilla DamagedVehicleSystem
    /// raises a fresh maintenance request and a recovery vehicle is dispatched again - and the
    /// watchdog can now clear it as a last resort, because the TowMarker that blocked it is gone.
    /// </summary>
    internal sealed class TowRearmDeadObjects
    {
        private readonly TowingContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private EntityQuery m_RearmDeadQuery => m_Ctx.RearmDeadQuery;
        private HashSet<Entity> m_TrucksWithLoad => m_Ctx.TrucksWithLoad;
        private Dictionary<Entity, uint> m_RelicArmedFrame => m_Ctx.RelicArmedFrame;

        // Positions of the actively-towing trucks, gathered once per pass.
        private readonly List<float3> m_TowingTruckPos = new List<float3>();

        public TowRearmDeadObjects(TowingContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Re-arm every stranded, still-marked relic that no actively-towing truck is working.
        /// Must run AFTER the follow pass has rebuilt TrucksWithLoad for this tick.
        /// </summary>
        public void RearmStrandedRelics(uint frame, Setting setting)
        {
            if (m_RearmDeadQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            // Snapshot the positions of trucks ACTIVELY carrying a load. Only these count as a
            // live tow operation near a wreck; idle / en-route trucks are ignored on purpose.
            m_TowingTruckPos.Clear();
            foreach (Entity truck in m_TrucksWithLoad)
            {
                if (EntityManager.Exists(truck) && EntityManager.HasComponent<Transform>(truck))
                {
                    m_TowingTruckPos.Add(EntityManager.GetComponentData<Transform>(truck).m_Position);
                }
            }

            NativeArray<Entity> relics = m_RearmDeadQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < relics.Length; i++)
                {
                    Entity relic = relics[i];
                    // A wreck whose Controller is a LIVE recovery truck is genuinely on a hook (or
                    // mid-hookup) - never re-arm that, regardless of distance.
                    if (EntityManager.HasComponent<Controller>(relic))
                    {
                        Entity ctrl = EntityManager.GetComponentData<Controller>(relic).m_Controller;
                        if (ctrl != relic && ctrl != Entity.Null && EntityManager.Exists(ctrl) &&
                            EntityManager.HasComponent<MaintenanceVehicle>(ctrl))
                        {
                            continue;
                        }
                    }
                    // Any actively-towing truck within range means a live recovery is working here.
                    float3 pos = EntityManager.GetComponentData<Transform>(relic).m_Position;
                    if (AnyTowingTruckWithinRange(pos))
                    {
                        continue;
                    }
                    ReArm(relic, frame, setting);
                }
            }
            finally
            {
                relics.Dispose();
            }
        }

        private bool AnyTowingTruckWithinRange(float3 pos)
        {
            float rangeSq = kRelicRearmTruckRange * kRelicRearmTruckRange;
            for (int i = 0; i < m_TowingTruckPos.Count; i++)
            {
                if (math.distancesq(m_TowingTruckPos[i].xz, pos.xz) <= rangeSq)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Bring a stranded marked relic back into the recovery pipeline: drop the stale TowMarker
        /// and Controller (the self/dead reference that blocks hookup and every sweep), clear a
        /// stale maintenance request and Destroyed.m_Cleared, then tag RelicRecovery. Vanilla
        /// DamagedVehicleSystem raises a fresh request and a recovery vehicle is dispatched; the
        /// relic watchdog now covers it too, because the blocking TowMarker is gone. Mirrors the
        /// re-arm body in <see cref="TowRelics"/> plus the TowMarker drop that is the whole point.
        /// </summary>
        private void ReArm(Entity relic, uint frame, Setting setting)
        {
            if (EntityManager.HasComponent<TowMarker>(relic))
            {
                EntityManager.RemoveComponent<TowMarker>(relic);
            }
            if (EntityManager.HasComponent<Controller>(relic))
            {
                EntityManager.RemoveComponent<Controller>(relic);
            }
            if (EntityManager.HasComponent<MaintenanceConsumer>(relic))
            {
                Entity req = EntityManager.GetComponentData<MaintenanceConsumer>(relic).m_Request;
                if (req != Entity.Null && EntityManager.Exists(req))
                {
                    EntityManager.AddComponent<Deleted>(req);
                }
                EntityManager.RemoveComponent<MaintenanceConsumer>(relic);
            }
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
                Mod.Log.Info($"[rearmdead] car={relic.Index} re-armed - stale TowMarker dropped, " +
                    $"no towing truck within {(int)kRelicRearmTruckRange}m, recovery re-requested");
            }
        }
    }
}

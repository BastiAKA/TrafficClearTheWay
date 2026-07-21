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
    /// Cleaning up wrecks and trailers whose carrier is gone.
    ///
    /// Both sweeps are deliberately conservative, because an over-eager version of exactly this
    /// caused real damage: a sweep keyed on our trailer PREFAB deleted ordinary civilian trailers
    /// (the game hands that registered prefab to citizens), leaving dangling LayoutElement
    /// references. So a wreck is only removed when its carrier truly no longer exists or has
    /// parked, and any LIVE controller protects a trailer from deletion.
    /// </summary>
    internal sealed class TowOrphans
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

        public TowOrphans(TowingContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Safety net for wrecks the follow pass can no longer see. Once a towed wreck falls
        /// out of m_TowedQuery (a secondary impact re-adds InvolvedInAccident, or it loses
        /// Stopped) OR m_OurTows lost it (in-memory only, no save/load survival), FollowOrFinish
        /// never runs for it again - so a wreck whose tow truck has parked or despawned is left
        /// standing on the road forever (field report: empty wrecks stranded ~300 m from the
        /// depot waiting on the vanilla delete timeout). This sweep uses the BROADER m_OrphanQuery
        /// and deletes any wreck still carrying OUR Controller tow marker whose carrier is gone
        /// or parked - INDEPENDENT of m_OurTows. Foreign rigs stay protected: real trailers are
        /// excluded by the query, and a wreck whose Controller points at a LIVE non-recovery
        /// vehicle is left alone (exactly the ownership test FollowOrFinish uses).
        /// </summary>
        public void OrphanSweep()
        {
            if (m_OrphanQuery.IsEmptyIgnoreFilter)
            {
                return;
            }
            NativeArray<Entity> wrecks = m_OrphanQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < wrecks.Length; i++)
                {
                    Entity wreck = wrecks[i];
                    Entity carrier = EntityManager.GetComponentData<Controller>(wreck).m_Controller;
                    if (carrier == Entity.Null || carrier == wreck)
                    {
                        continue; // no / self controller - not one of our tows
                    }
                    bool carrierExists = EntityManager.Exists(carrier) &&
                        !EntityManager.HasComponent<Deleted>(carrier);
                    if (carrierExists)
                    {
                        if (!EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(carrier))
                        {
                            continue; // Controller points at a live non-recovery vehicle - not ours
                        }
                        if (!EntityManager.HasComponent<ParkedCar>(carrier))
                        {
                            continue; // live recovery truck still hauling it (incl. stuck in a jam) - leave it
                        }
                    }
                    // Carrier gone, or parked at the depot -> orphaned / delivered tow: remove it.
                    m_Ctx.SafeDelete(wreck);
                    m_OurTows.Remove(wreck);
                    if (Mod.Setting.VerboseLogging)
                    {
                        Mod.Log.Info($"[tow] orphan wreck={wreck.Index} removed (carrier {(carrierExists ? "parked" : "gone")})");
                    }
                }
            }
            finally
            {
                wrecks.Dispose();
            }
        }

        /// <summary>
        /// Removes trailers stranded by a tow: their tractor (the wreck) was hauled away and
        /// SafeDeleted at the depot, leaving the trailer standing on the road with a Controller
        /// reference to a dead entity. New hookups despawn the trailer right at coupling, this
        /// sweep retroactively cleans the ones already on the map (they survive save/load).
        /// Only a controller that no longer exists (or is being deleted) qualifies - any live
        /// controller, parked included, means a civilian rig and is left strictly alone.
        /// </summary>
        public void OrphanTrailerSweep()
        {
            if (m_TrailerOrphanQuery.IsEmptyIgnoreFilter)
            {
                return;
            }
            NativeArray<Entity> trailers = m_TrailerOrphanQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < trailers.Length; i++)
                {
                    Entity trailer = trailers[i];
                    Entity controller = EntityManager.GetComponentData<Controller>(trailer).m_Controller;
                    if (controller == Entity.Null || controller == trailer)
                    {
                        continue; // no/self controller - never one of our tows, leave it
                    }
                    if (EntityManager.Exists(controller) && !EntityManager.HasComponent<Deleted>(controller))
                    {
                        continue; // live tractor (driving or parked) - a real rig
                    }
                    m_Ctx.SafeDelete(trailer);
                    if (Mod.Setting.VerboseLogging)
                    {
                        Mod.Log.Info($"[tow] orphan trailer={trailer.Index} removed (its tractor is gone)");
                    }
                }
            }
            finally
            {
                trailers.Dispose();
            }
        }
    }
}

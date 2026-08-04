using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Deciding WHICH recovery vehicle goes to a given wreck.
    ///
    /// The mod never creates a maintenance request - vanilla does that. This only redirects the
    /// ones that exist, either straight to a nearby capable van or, failing that, to the tow
    /// depot.
    ///
    /// Handing a request to a depot needs one non-obvious step: the PathInformation component
    /// must be removed and the path element buffer cleared first. Otherwise its origin still
    /// points at the previous provider and the depot AI silently drops the request instead of
    /// spawning a truck.
    ///
    /// Reassignment is damped by a hysteresis: two nearby wrecks with several equidistant depot
    /// trucks otherwise steal the same trucks from each other on every pass, and none of them
    /// ever arrives.
    /// </summary>
    internal sealed class TowAssignment
    {
        private readonly DispatchContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private SimulationSystem m_SimulationSystem => m_Ctx.Simulation;
        private TowHookupSystem m_HookupSystem => m_Ctx.HookupSystem;
        private EntityQuery m_WreckQuery => m_Ctx.WreckQuery;
        private EntityQuery m_VanQuery => m_Ctx.VanQuery;
        private EntityQuery m_DepotQuery => m_Ctx.DepotQuery;
        private EntityQuery m_AllWreckQuery => m_Ctx.AllWreckQuery;
        private EntityQuery m_VanillaDamagedQuery => m_Ctx.VanillaDamagedQuery;
        private EntityArchetype m_HandleRequestArchetype => m_Ctx.HandleRequestArchetype;
        private Dictionary<Entity, uint> m_LastAction => m_Ctx.LastAction;
        private Dictionary<Entity, uint> m_VanSentHome => m_Ctx.VanSentHome;
        private Dictionary<Entity, uint> m_VanAssigned => m_Ctx.VanAssigned;
        private Dictionary<Entity, Entity> m_VanJob => m_Ctx.VanJob;
        private List<Entity> m_PruneScratch => m_Ctx.PruneScratch;

        public TowAssignment(DispatchContext ctx)
        {
            m_Ctx = ctx;
        }

        public void DispatchWreck(Entity wreck, NativeArray<Entity> vans, uint frame, Setting setting)
        {
            Entity request = EntityManager.GetComponentData<Game.Simulation.MaintenanceConsumer>(wreck).m_Request;
            if (request == Entity.Null || !EntityManager.Exists(request) ||
                !EntityManager.HasComponent<MaintenanceRequest>(request))
            {
                return; // no open request (yet) - vanilla will create one, we act next pass
            }
            float3 wreckPos = EntityManager.GetComponentData<Transform>(wreck).m_Position;

            // Survey the vans. The responder is whichever van pass 1 resolved to THIS wreck -
            // m_VanJob is the single source of truth and holds each van exactly once, so a
            // wreck can neither steal a van that is already serving another one nor cause a
            // second re-target of it this tick. Beelining happened in pass 1; this pass only
            // makes assignment decisions.
            Entity responder = Entity.Null;
            float responderDist = float.MaxValue;
            Entity nearest = Entity.Null;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < vans.Length; i++)
            {
                Entity van = vans[i];
                float dist = math.distance(EntityManager.GetComponentData<Transform>(van).m_Position.xz, wreckPos.xz);
                if (m_VanJob.TryGetValue(van, out Entity job))
                {
                    if (job == wreck)
                    {
                        responder = van;
                        responderDist = dist;
                    }
                    continue; // busy with a wreck (this one or another) - never a candidate
                }
                if (m_VanSentHome.TryGetValue(van, out uint sentHomeFrame) &&
                    frame - sentHomeFrame < kActionCooldown)
                {
                    continue; // just sent home - don't let another wreck instantly re-grab it (anti-ping-pong)
                }
                if (dist < nearestDist && IsSuitable(van, wreck))
                {
                    nearest = van;
                    nearestDist = dist;
                }
            }

            // Reassignments are rate-limited per wreck.
            if (m_LastAction.TryGetValue(wreck, out uint last) && frame - last < kActionCooldown)
            {
                return;
            }

            if (responder == Entity.Null)
            {
                if (nearest != Entity.Null && nearestDist <= kNearVanRange)
                {
                    AssignToVan(nearest, wreck, request, frame);
                }
                else
                {
                    AssignToTowDepot(wreck, request, wreckPos, frame);
                }
            }
            else if (responderDist > kHandsOffRange)
            {
                bool responderIsTowTruck = EntityManager.HasComponent<CarTractorData>(
                    EntityManager.GetComponentData<PrefabRef>(responder).m_Prefab);
                // A van much closer than the current responder takes over - but only if the swap
                // is actually WORTH it. Three guards, all learned from one field case (2026-07-26,
                // van 2270668 cancelled just short of its wreck):
                //  - a relative halving alone fires on trivial gains at short range (90 m -> 40 m)
                //    and these are straight-line distances, so the "gain" may not even be real on
                //    the road. Demand an absolute margin too.
                //  - a van only just put on the job keeps it for kTakeoverGraceFrames. Otherwise a
                //    cluster of wrecks re-decides every pass, and each van sent home is instantly
                //    free to become the "nearest" for the next wreck - five sent-home in three
                //    minutes, nobody ever arriving.
                bool recentlyAssigned = m_VanAssigned.TryGetValue(responder, out uint assignedFrame) &&
                    frame - assignedFrame < kTakeoverGraceFrames;
                if (nearest != Entity.Null && nearestDist <= kNearVanRange &&
                    nearestDist < responderDist * kMuchCloserFactor &&
                    responderDist - nearestDist >= kTakeoverMinGainMeters &&
                    !recentlyAssigned)
                {
                    m_Ctx.Beeline.SendHome(responder, frame);
                    AssignToVan(nearest, wreck, request, frame);
                }
                else if (!responderIsTowTruck && responderDist > kNearVanRange &&
                    (nearest == Entity.Null || nearestDist > kNearVanRange))
                {
                    // No suitable van anywhere near: a distant road-maintenance van is NOT
                    // what should come - send it home and call a proper tow truck instead.
                    if (AssignToTowDepot(wreck, request, wreckPos, frame))
                    {
                        m_Ctx.Beeline.SendHome(responder, frame);
                    }
                }
            }
        }

        /// <summary>Does this van hold the wreck's recovery order - accepted OR still
        /// <summary>Vehicle-capable, on duty, not towing, not full, not already on another wreck.</summary>
        public bool IsSuitable(Entity van, Entity wreck)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(van).m_Prefab;
            if (!EntityManager.HasComponent<MaintenanceVehicleData>(prefab) ||
                (EntityManager.GetComponentData<MaintenanceVehicleData>(prefab).m_MaintenanceType & MaintenanceType.Vehicle) == 0)
            {
                return false;
            }
            Game.Vehicles.MaintenanceVehicle maintenance =
                EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(van);
            if ((maintenance.m_State & (MaintenanceVehicleFlags.Full |
                MaintenanceVehicleFlags.EstimatedFull | MaintenanceVehicleFlags.Disabled)) != 0)
            {
                return false;
            }
            if (m_HookupSystem.TrucksWithLoad.Contains(van))
            {
                return false; // hauling a wreck home already
            }
            // Already dispatched to a different wreck? Leave it on that job.
            if (EntityManager.HasComponent<Target>(van))
            {
                Entity target = EntityManager.GetComponentData<Target>(van).m_Target;
                if (target != Entity.Null && target != wreck && EntityManager.Exists(target) &&
                    EntityManager.HasComponent<Game.Events.InvolvedInAccident>(target))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Hand the wreck order to this van, first in its queue (mirrors what
        /// SelectNextDispatch does when it accepts a request).</summary>
        public void AssignToVan(Entity van, Entity wreck, Entity request, uint frame)
        {
            m_VanSentHome.Remove(van); // a real assignment clears any stale sent-home cooldown
            m_VanAssigned[van] = frame;  // starts its takeover grace - let it drive there before re-deciding
            DynamicBuffer<ServiceDispatch> dispatches = EntityManager.HasBuffer<ServiceDispatch>(van)
                ? EntityManager.GetBuffer<ServiceDispatch>(van)
                : EntityManager.AddBuffer<ServiceDispatch>(van);
            for (int i = 0; i < dispatches.Length; i++)
            {
                if (dispatches[i].m_Request == request)
                {
                    m_VanJob[van] = wreck; // already queued - claim it so no other wreck grabs this van
                    return;                // beeline handles the rest next pass
                }
            }
            // Claim the van for THIS wreck immediately: later wrecks in this same pass must
            // not pick it as their "nearest candidate" too (that would queue it for several
            // wrecks at once and put us straight back into the re-target thrash).
            m_VanJob[van] = wreck;
            dispatches.Insert(0, new ServiceDispatch(request));
            Game.Vehicles.MaintenanceVehicle maintenance =
                EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(van);
            maintenance.m_RequestCount++;
            maintenance.m_State &= ~(MaintenanceVehicleFlags.Returning | MaintenanceVehicleFlags.TryWork |
                MaintenanceVehicleFlags.Working | MaintenanceVehicleFlags.ClearingDebris |
                MaintenanceVehicleFlags.EdgeTarget | MaintenanceVehicleFlags.TransformTarget);
            maintenance.m_State |= MaintenanceVehicleFlags.TransformTarget;
            EntityManager.SetComponentData(van, maintenance);
            Car car = EntityManager.GetComponentData<Car>(van);
            car.m_Flags |= CarFlags.StayOnRoad;
            EntityManager.SetComponentData(van, car);
            EntityManager.SetComponentData(van, new Target(wreck));
            m_Ctx.Beeline.Repath(van);
            m_Ctx.Beeline.CreateHandleRequest(request, van);
            m_LastAction[wreck] = frame;
            if (Mod.Setting.VerboseLogging)
            {
                Mod.Log.Info($"[dispatch] wreck={wreck.Index} -> nearest van={van.Index}");
            }
        }

        /// <summary>Route the request to the dedicated tow depot (MaintenanceType == Vehicle
        /// ONLY). Returns false when no such depot is built. The depot AI consumes the entry
        /// and spawns/unparks an Abschleppwagen itself; the request's stale PathInformation
        /// must be stripped first or the depot's SpawnVehicle silently drops it (its origin
        /// points at the previous provider).</summary>
        public bool AssignToTowDepot(Entity wreck, Entity request, float3 wreckPos, uint frame)
        {
            Entity depot = Entity.Null;
            float depotDist = float.MaxValue;
            NativeArray<Entity> depots = m_DepotQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < depots.Length; i++)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(depots[i]).m_Prefab;
                    if (!EntityManager.HasComponent<MaintenanceDepotData>(prefab) ||
                        EntityManager.GetComponentData<MaintenanceDepotData>(prefab).m_MaintenanceType != MaintenanceType.Vehicle)
                    {
                        continue;
                    }
                    float dist = math.distance(EntityManager.GetComponentData<Transform>(depots[i]).m_Position.xz, wreckPos.xz);
                    if (dist < depotDist)
                    {
                        depot = depots[i];
                        depotDist = dist;
                    }
                }
            }
            finally
            {
                depots.Dispose();
            }
            if (depot == Entity.Null)
            {
                return false; // no tow depot built - leave the vanilla assignment alone
            }
            DynamicBuffer<ServiceDispatch> dispatches = EntityManager.HasBuffer<ServiceDispatch>(depot)
                ? EntityManager.GetBuffer<ServiceDispatch>(depot)
                : EntityManager.AddBuffer<ServiceDispatch>(depot);
            for (int i = 0; i < dispatches.Length; i++)
            {
                if (dispatches[i].m_Request == request)
                {
                    return true; // already queued there
                }
            }
            if (EntityManager.HasComponent<PathInformation>(request))
            {
                EntityManager.RemoveComponent<PathInformation>(request);
            }
            if (EntityManager.HasBuffer<PathElement>(request))
            {
                EntityManager.GetBuffer<PathElement>(request).Clear();
            }
            dispatches.Add(new ServiceDispatch(request));
            m_LastAction[wreck] = frame;
            if (Mod.Setting.VerboseLogging)
            {
                Mod.Log.Info($"[dispatch] wreck={wreck.Index} -> tow depot={depot.Index} (no van within {kNearVanRange:F0} m)");
            }
            return true;
        }
    }
}

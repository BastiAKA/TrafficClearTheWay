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
    /// Keeping a recovery vehicle pointed AT the wreck it was given, and its path alive.
    ///
    /// Two failures live here, both found in logs:
    ///  - THRASH. The pass used to iterate wrecks, so a van holding several wreck requests was
    ///    recognised as the responder for each of them and had its target rewritten multiple
    ///    times per pass - it never kept a path, stood still, and the forced repaths flooded the
    ///    pathfinder hard enough to despawn unrelated vehicles (whole trams). It now resolves ONE
    ///    job per van, so a van already heading to its wreck is written to at all.
    ///  - LOST ORDERS. The maintenance AI drops our assignment whenever the route to the wreck
    ///    fails - and the wreck is lying ON the road it is blocking, so it fails often. The path
    ///    shield clears those flags before the AI reads them and retries on a stagger, rather
    ///    than every tick, because a repath flood was the despawn cause above.
    /// </summary>
    internal sealed class TowBeeline
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
        private Dictionary<Entity, Entity> m_VanJob => m_Ctx.VanJob;
        private List<Entity> m_PruneScratch => m_Ctx.PruneScratch;

        // EntityManager.CreateEntity(EntityArchetype) cannot be called directly here: its method
        // group also has a ReadOnlySpan overload the .NET Framework reference assemblies cannot
        // resolve, so ANY direct call fails to compile with CS0518. Cached reflection instead.
        private static System.Reflection.MethodInfo s_CreateEntityMethod;

        public TowBeeline(DispatchContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// The single wreck-recovery job this van is serving, resolved deterministically so
        /// the van cannot be pulled between several orders (see the note in OnUpdate). Its
        /// current Target wins - that is the job the vanilla AI is already driving to;
        /// otherwise the FIRST wreck order in its dispatch buffer (accepted or still
        /// pending). Returns Entity.Null when the van has no wreck job at all.
        /// </summary>
        public Entity ResolveVanJob(Entity van, out Entity request)
        {
            request = Entity.Null;
            if (EntityManager.HasComponent<Target>(van))
            {
                Entity target = EntityManager.GetComponentData<Target>(van).m_Target;
                if (IsOpenWreck(target, out Entity targetRequest))
                {
                    request = targetRequest;
                    return target;
                }
            }
            if (!EntityManager.HasBuffer<ServiceDispatch>(van))
            {
                return Entity.Null;
            }
            DynamicBuffer<ServiceDispatch> dispatches = EntityManager.GetBuffer<ServiceDispatch>(van, isReadOnly: true);
            for (int i = 0; i < dispatches.Length; i++)
            {
                Entity candidate = dispatches[i].m_Request;
                if (candidate == Entity.Null || !EntityManager.Exists(candidate) ||
                    !EntityManager.HasComponent<MaintenanceRequest>(candidate))
                {
                    continue;
                }
                Entity wreck = EntityManager.GetComponentData<MaintenanceRequest>(candidate).m_Target;
                if (IsOpenWreck(wreck, out Entity wreckRequest) && wreckRequest == candidate)
                {
                    request = candidate;
                    return wreck;
                }
            }
            return Entity.Null;
        }

        /// <summary>
        /// Keeps a recovery vehicle's wreck order alive while it cannot path there (yet).
        /// The wreck sits ON the road it blocks, so the route to it regularly fails - and
        /// MaintenanceVehicleAISystem.Tick reacts to `PathfindFailed` (= Failed|Stuck) by
        /// calling ReturnToDepot, which CLEARS the whole ServiceDispatch buffer and points
        /// Target at the depot (and outright DELETES the vehicle if it is stuck as well).
        /// That produced a silent loop: we assign the wreck -> its path fails -> the AI
        /// throws our order away -> ProcessAssistVehicle no longer recognises it as being
        /// on a wreck run, so it gets neither corridor nor escalation -> the wreck looks
        /// unowned and we assign it to the very same van again, forever, while it just sits
        /// in the jam (observed: the same wreck re-assigned to the same van every ~15 s and
        /// not a single [assist] line).
        /// So: clear Failed|Stuck before its AI reads them, and retry the route now and then
        /// (staggered per vehicle so the pathfinder is not hammered - a repath flood is what
        /// starved unrelated vehicles once already). Once the wreck consolidation frees a
        /// lane or the jam eases, one of those retries finds a route and it drives on.
        /// </summary>
        public void ShieldPath(Entity van, uint frame)
        {
            if (!EntityManager.HasComponent<PathOwner>(van))
            {
                return;
            }
            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(van);
            if ((pathOwner.m_State & (PathFlags.Failed | PathFlags.Stuck)) == 0)
            {
                return;
            }
            pathOwner.m_State &= ~(PathFlags.Failed | PathFlags.Stuck);
            if (((frame + (uint)van.Index) & 0xFFu) == 0u) // ~every 256 frames per vehicle
            {
                pathOwner.m_State |= PathFlags.Obsolete;
            }
            EntityManager.SetComponentData(van, pathOwner);
            if (Mod.Setting.VerboseLogging && frame % 180u == 0u)
            {
                Mod.Log.Info($"[dispatch] van={van.Index} path to its wreck failed - shielded (would have been sent home)");
            }
        }

        /// <summary>A settled wreck whose recovery request is still open.</summary>
        public bool IsOpenWreck(Entity wreck, out Entity request)
        {
            request = Entity.Null;
            if (wreck == Entity.Null || !EntityManager.Exists(wreck) ||
                !EntityManager.HasComponent<Game.Events.InvolvedInAccident>(wreck) ||
                !EntityManager.HasComponent<Transform>(wreck) ||
                !EntityManager.HasComponent<Game.Simulation.MaintenanceConsumer>(wreck))
            {
                return false;
            }
            request = EntityManager.GetComponentData<Game.Simulation.MaintenanceConsumer>(wreck).m_Request;
            return request != Entity.Null && EntityManager.Exists(request) &&
                EntityManager.HasComponent<MaintenanceRequest>(request);
        }

        /// <summary>Keep the responder on a straight line to the wreck: order first in the
        /// queue, no road-patching (TryWork/Working) en route, target locked on the wreck.</summary>
        public void EnforceBeeline(Entity van, Entity wreck, Entity request)
        {
            Game.Vehicles.MaintenanceVehicle maintenance =
                EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(van);
            bool changed = false;
            if (EntityManager.HasBuffer<ServiceDispatch>(van))
            {
                DynamicBuffer<ServiceDispatch> dispatches = EntityManager.GetBuffer<ServiceDispatch>(van);
                int accepted = math.min(maintenance.m_RequestCount, dispatches.Length);
                for (int i = 1; i < dispatches.Length; i++)
                {
                    if (dispatches[i].m_Request == request)
                    {
                        ServiceDispatch entry = dispatches[i];
                        dispatches.RemoveAt(i);
                        dispatches.Insert(0, entry);
                        if (i >= accepted)
                        {
                            // The order was only PENDING (dispatcher appended it, the van
                            // had not accepted it yet and would have kept serving its road
                            // jobs first - "erst noch 100 Sachen anfahren"). Accept it NOW
                            // and record the handler so the dispatcher stops re-offering it.
                            maintenance.m_RequestCount++;
                            CreateHandleRequest(request, van);
                        }
                        changed = true;
                        break;
                    }
                }
                if (maintenance.m_RequestCount == 0 && dispatches.Length > 0 &&
                    dispatches[0].m_Request == request)
                {
                    // Order sits at [0] but was never accepted at all.
                    maintenance.m_RequestCount = 1;
                    CreateHandleRequest(request, van);
                    changed = true;
                }
            }
            if ((maintenance.m_State & (MaintenanceVehicleFlags.TryWork | MaintenanceVehicleFlags.Working |
                MaintenanceVehicleFlags.ClearingDebris | MaintenanceVehicleFlags.Returning)) != 0)
            {
                maintenance.m_State &= ~(MaintenanceVehicleFlags.TryWork | MaintenanceVehicleFlags.Working |
                    MaintenanceVehicleFlags.ClearingDebris | MaintenanceVehicleFlags.Returning);
                changed = true;
            }
            if ((maintenance.m_State & MaintenanceVehicleFlags.TransformTarget) == 0 ||
                (maintenance.m_State & MaintenanceVehicleFlags.EdgeTarget) != 0)
            {
                maintenance.m_State &= ~MaintenanceVehicleFlags.EdgeTarget;
                maintenance.m_State |= MaintenanceVehicleFlags.TransformTarget;
                changed = true;
            }
            if (changed)
            {
                EntityManager.SetComponentData(van, maintenance);
            }
            if (EntityManager.HasComponent<Target>(van) &&
                EntityManager.GetComponentData<Target>(van).m_Target != wreck)
            {
                EntityManager.SetComponentData(van, new Target(wreck));
                Repath(van);
                if (Mod.Setting.VerboseLogging)
                {
                    Mod.Log.Info($"[dispatch] van={van.Index} beeline to wreck={wreck.Index}");
                }
            }
        }

        /// <summary>Send a van home (mirrors ReturnToDepot): its wreck order was taken over
        /// by a closer provider. Its remaining road-work requests re-dispatch on their own.</summary>
        public void SendHome(Entity van, uint frame)
        {
            m_VanJob.Remove(van); // no longer serving a wreck this pass
            m_VanSentHome[van] = frame; // bar it from an instant re-grab by another wreck
            if (EntityManager.HasBuffer<ServiceDispatch>(van))
            {
                EntityManager.GetBuffer<ServiceDispatch>(van).Clear();
            }
            Game.Vehicles.MaintenanceVehicle maintenance =
                EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(van);
            maintenance.m_RequestCount = 0;
            maintenance.m_MaintainEstimate = 0;
            maintenance.m_State &= ~(MaintenanceVehicleFlags.TransformTarget | MaintenanceVehicleFlags.EdgeTarget |
                MaintenanceVehicleFlags.TryWork | MaintenanceVehicleFlags.Working | MaintenanceVehicleFlags.ClearingDebris);
            maintenance.m_State |= MaintenanceVehicleFlags.Returning;
            EntityManager.SetComponentData(van, maintenance);
            Car car = EntityManager.GetComponentData<Car>(van);
            car.m_Flags &= ~(CarFlags.Warning | CarFlags.Working | CarFlags.SignalAnimation1 | CarFlags.SignalAnimation2);
            EntityManager.SetComponentData(van, car);
            EntityManager.SetComponentData(van, new Target(EntityManager.GetComponentData<Owner>(van).m_Owner));
            Repath(van);
            if (Mod.Setting.VerboseLogging)
            {
                Mod.Log.Info($"[dispatch] van={van.Index} sent home (order taken over)");
            }
        }

        public void Repath(Entity vehicle)
        {
            if (EntityManager.HasComponent<PathOwner>(vehicle))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
                pathOwner.m_State |= PathFlags.Obsolete;
                EntityManager.SetComponentData(vehicle, pathOwner);
            }
        }

        /// <summary>HandleRequest event so the request system records the new handler.
        /// CreateEntity is invoked via reflection - see AbschleppHookup (the net472 ref
        /// assemblies cannot resolve the method group's ReadOnlySpan overload, CS0518).</summary>
        public void CreateHandleRequest(Entity request, Entity handler)
        {
            if (s_CreateEntityMethod == null)
            {
                s_CreateEntityMethod = typeof(EntityManager).GetMethod(
                    nameof(EntityManager.CreateEntity),
                    new[] { typeof(EntityArchetype) });
            }
            Entity e = (Entity)s_CreateEntityMethod.Invoke(EntityManager, new object[] { m_HandleRequestArchetype });
            EntityManager.SetComponentData(e, new HandleRequest(request, handler, completed: false, pathConsumed: false));
        }
    }
}

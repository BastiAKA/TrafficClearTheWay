using ClearTheWay.FunctionGroup.Accidents;
using ClearTheWay.FunctionGroup.GeneralImprovements;
using ClearTheWay.FunctionGroup.WayClearance;
using ClearTheWay.FunctionGroup.WayClearance.Squeezing;
using ClearTheWay.FunctionGroup.WayClearance.Squeezing.AtTarget;
using ClearTheWay.FunctionGroup.WayClearance.Squeezing.Roundabout;
using ClearTheWay.FunctionGroup.WayClearance.TrafficLights;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes.Geometry;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding;
using Game;
using Game.City;
using Game.Pathfind;
using Game.Simulation;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

// Game.Net has a LaneGeometry of its own - ours wins here, like the CarLaneFlags alias above.
using LaneGeometry = ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding.M_LaneGeometry;

namespace ClearTheWay
{
    /// <summary>
    /// Makes traffic form a "ClearTheWay" (emergency corridor) for emergency vehicles
    /// running with lights and siren (CarFlags.Emergency):
    ///  1. Cars on the emergency vehicle's current and upcoming lanes pull over to the
    ///     edge of their lane (m_LanePosition).
    ///  2. The emergency vehicle hugs the opposite edge, so a corridor opens up.
    ///  3. If the emergency vehicle is still blocked by a car that has pulled aside and
    ///     stopped, it is allowed to carefully squeeze past it (CarLaneFlags.IgnoreBlocker,
    ///     the same mechanism the game itself uses to resolve deadlocks).
    /// Vanilla behavior (lane reservations with priority 108/102 and traffic-light
    /// petitions that turn signals green for emergency vehicles) is left untouched and
    /// keeps working on top of this.
    /// </summary>
    public partial class ClearTheWaySystem : GameSystemBase
    {

        private EntityQuery m_EmergencyQuery;
        private EntityQuery m_AssistQuery;
        private EntityQuery m_EmergencyPathQuery;
        private EntityQuery m_WreckQuery;
        private CityConfigurationSystem m_CityConfigurationSystem;

        /// <summary>Everything the way-clearance passes share; see WayClearanceContext.</summary>
        private WayClearanceContext modContext;
        private RecoveryAssist modRecovery;

        private VehicleControl m_Control => modContext.VehicleControl;
        private ResponderStates m_States => modContext.States;
        private Dictionary<Entity, StuckState> m_StuckStates => m_States.Stuck;
        private Dictionary<Entity, uint> m_ForcedChanges => m_States.ForcedChanges;
        private AccidentGuard m_Accidents => modContext.Accidents;
        private SimulationSystem m_SimulationSystem => modContext.Simulation;
        private CorridorBuilder m_Corridor => modContext.Corridor;

        /// <summary>Scratch list for the periodic prune below.</summary>
        private readonly List<Entity> m_PruneScratch = new();

        internal AccidentGuard Accidents => modContext.Accidents;

        /// <summary>The queued traffic-light writes; flushed by GreenLightSystem after the game's
        /// light systems have run (see there for why the write cannot happen in this pass).</summary>
        internal GreenLightChain Lights => modContext?.Lights;
        public IReadOnlyDictionary<Entity, SpeedOverride> SpeedOverrides => modContext.VehicleControl.SpeedOverrides;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_EmergencyQuery = GetEntityQuery(WayClearanceQueries.Emergency);
            m_EmergencyPathQuery = GetEntityQuery(WayClearanceQueries.EmergencyPath);
            m_AssistQuery = GetEntityQuery(WayClearanceQueries.Assist);
            m_WreckQuery = GetEntityQuery(WayClearanceQueries.Wrecks);

            // Two-phase construction: the leaves first, then the passes that reference each
            // other through the context. A pass may therefore only dereference the context
            // from its methods, never from its own constructor.
            modContext = new WayClearanceContext
            {
                EntityManager = EntityManager,
                Simulation = World.GetOrCreateSystemManaged<SimulationSystem>(),
                ObjectSearch = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>(),
                NetSearch = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>(),
                CityConfiguration = m_CityConfigurationSystem,
                Geometry = new LaneGeometry(EntityManager),
                PrefabGeometry = new GeometryProvider(EntityManager)
            };
            modContext.LanePosition = new M_LanePosition(modContext);
            modContext.VehicleControl = new VehicleControl(EntityManager);
            modContext.States = new ResponderStates(EntityManager);
            modContext.PushedCars = new PushedCars(modContext);

            modContext.Channel = new ChannelPlanner(modContext);
            modContext.Room = new LateralRoom(modContext);
            modContext.Corridor = new CorridorBuilder(modContext);
            modContext.LaneGroups = new CorridorLaneGroups(modContext);
            modContext.Roundabout = new RoundaboutExtensions(modContext);
            modContext.Trucks = new VehicleTrailerExt(modContext);
            modContext.MidSize = new Delivery_MidSize(modContext);
            modContext.Lights = new GreenLightChain(modContext);
            modContext.Push = new PushVehicles(modContext);
            modContext.Hold = new HoldVehicles(modContext);
            modContext.Squeeze = new SqueezePast(modContext);
            modContext.LaneChange = new LaneChange(modContext);
            modContext.Obstruction = new LaneObstruction(modContext);

            modContext.Desperate = new DesperateBehaviour(modContext);
            modContext.CorridorRun = new EscalationCorridor(modContext);
            modContext.Steering = new EscalationSteering(modContext);
            modContext.SpeedStage = new EscalationSpeed(modContext);
            modContext.Escalation = new EmergencyEscalation(modContext);
            modContext.NearWreck = new NearWreckProtection(modContext);
            modContext.Reporting = new AccidentReporting(modContext);
            modContext.WreckClearing = new WreckClearing(modContext);
            modContext.WreckLifetime = new WreckLifetime(modContext);
            modContext.Accidents = new AccidentGuard(modContext, m_WreckQuery);

            modContext.Arrival = new ArrivalAssist(modContext);
            modContext.ArrivalTarget = new ArrivalTarget(modContext);

            // Debug vehicle watch: a broad "every live car" query to resolve a watched index, and
            // the dump pass itself. Both are inert unless the WatchVehicle setting is set.
            modContext.WatchScanQuery = GetEntityQuery(
                ComponentType.ReadOnly<Car>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            modContext.Watch = new VehicleWatch(modContext);


            modRecovery = new RecoveryAssist(EntityManager, modContext.Geometry, modContext.VehicleControl, modContext.Corridor, modContext);
        }

        // Main-thread profiling switch for this pass - see ModProfiler for how to use it.
        // Everything here runs on the sim thread, so a slow tick shows up as a frame-rate
        // drop with CPU and GPU both idle. Flip to true, rebuild, read the [perf] lines.
        private static readonly bool kProfile = false;

        protected override void OnUpdate()
        {
            Setting setting = Mod.Setting;
            if (setting == null || !setting.Enabled)
            {
                if (modContext.PushedCars.PushedCarCount != 0)
                {
                    modContext.PushedCars.ReleasePushedCars(uint.MaxValue);
                }
                return;
            }

            using (ModProfiler.Sample(kProfile, "ClearTheWaySystem"))
            {
                RunPass(setting);
            }
            ModProfiler.EndTick(kProfile, "ClearTheWaySystem");
            ModProfiler.Report(m_SimulationSystem.frameIndex);
        }

        private void RunPass(Setting setting)
        {
            m_Control.BeginTick();

            uint frame = m_SimulationSystem.frameIndex;
            if (!m_EmergencyQuery.IsEmptyIgnoreFilter)
            {
                // Positive m_LanePosition is to the right of the driving direction, so in
                // right-hand traffic cars pull to +side and the emergency passes on -side.
                float side = m_CityConfigurationSystem.leftHandTraffic ? -1f : 1f;

                NativeArray<Entity> vehicles = m_EmergencyQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < vehicles.Length; i++)
                    {
                        Entity vehicle = vehicles[i];
                        Car car = EntityManager.GetComponentData<Car>(vehicle);
                        if ((car.m_Flags & CarFlags.Emergency) == 0)
                        {
                            continue;
                        }
                        if (VehicleTrailerExt.IsTrailer(EntityManager, vehicle))
                        {
                            continue;
                        }
                        modContext.Escalation.ProcessEmergencyVehicle(vehicle, setting, side, frame);
                    }
                }
                finally
                {
                    vehicles.Dispose();
                }
            }

            // Delete-shield for every responder: the police/ambulance/fire AIs DELETE their
            // vehicle when a pathfind fails while it is Returning or flagged stuck. At a
            // fully blocked accident exactly that happens to the RETURN path of a unit that
            // just finished its job - it despawns on the spot instead of waiting. Clear
            // Failed|Stuck before the AIs run and retry the path now and then; once the
            // wreck consolidation frees a lane, the unit drives home normally.
            // Gated on accidents actually existing: a PathfindFailed delete only happens when
            // lanes are truly blocked/removed by a wreck - ordinary gridlock leaves the route
            // routable (just jammed), so with no wrecks this shield has nothing to do. Skipping
            // it then avoids iterating every responder in the city (+ a query sync) each tick
            // in the common, no-accident case (it was the one pass that always ran).
            if (setting.PreventAccidentDespawn && !m_WreckQuery.IsEmptyIgnoreFilter &&
                !m_EmergencyPathQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> responders = m_EmergencyPathQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < responders.Length; i++)
                    {
                        Entity responder = responders[i];
                        PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(responder);
                        if ((pathOwner.m_State & (PathFlags.Failed | PathFlags.Stuck)) == 0)
                        {
                            continue;
                        }
                        pathOwner.m_State &= ~(PathFlags.Failed | PathFlags.Stuck);
                        // Staggered retry (~every 256 frames per vehicle) so the failed
                        // route is attempted again without hammering the pathfinder.
                        if (((frame + (uint)responder.Index) & 0xFFu) == 0u)
                        {
                            pathOwner.m_State |= PathFlags.Obsolete;
                        }
                        EntityManager.SetComponentData(responder, pathOwner);
                    }
                }
                finally
                {
                    responders.Dispose();
                }
            }

            // Gentle make-way corridor for recovery/tow vehicles en route to an accident.
            if (setting.AssistTowTrucks && !m_AssistQuery.IsEmptyIgnoreFilter)
            {
                float side = m_CityConfigurationSystem.leftHandTraffic ? -1f : 1f;
                NativeArray<Entity> assist = m_AssistQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < assist.Length; i++)
                    {
                        modRecovery.ProcessAssistVehicle(assist[i], side, frame);
                    }
                }
                finally
                {
                    assist.Dispose();
                }
            }

            // Keep the queue behind a crashed vehicle alive (they would otherwise be
            // despawned by the game's stuck-traffic cleanup instead of waiting).
            if (setting.PreventAccidentDespawn && !m_WreckQuery.IsEmptyIgnoreFilter)
            {
                m_Accidents.ProtectAccidentQueues(setting, frame);
            }

            modContext.PushedCars.ReleasePushedCars(frame);
            PruneStuckStates(frame);

            // Debug: dump a single watched vehicle's full state (inert unless the setting is set).
            modContext.Watch.Dump(setting, frame);
        }

        private void PruneStuckStates(uint frame)
        {
            if ((frame & 0x3FFu) != 0)
            {
                return;
            }
            if (m_StuckStates.Count != 0)
            {
                m_PruneScratch.Clear();
                foreach (KeyValuePair<Entity, StuckState> entry in m_StuckStates)
                {
                    if (frame - entry.Value.m_LastSeenFrame > 1024u)
                    {
                        m_PruneScratch.Add(entry.Key);
                    }
                }
                for (int i = 0; i < m_PruneScratch.Count; i++)
                {
                    m_StuckStates.Remove(m_PruneScratch[i]);
                }
                m_PruneScratch.Clear();
            }
            if (m_ForcedChanges.Count != 0)
            {
                m_PruneScratch.Clear();
                foreach (KeyValuePair<Entity, uint> entry in m_ForcedChanges)
                {
                    if (frame - entry.Value > 2048u || !EntityManager.Exists(entry.Key))
                    {
                        m_PruneScratch.Add(entry.Key);
                    }
                }
                for (int i = 0; i < m_PruneScratch.Count; i++)
                {
                    m_ForcedChanges.Remove(m_PruneScratch[i]);
                }
                m_PruneScratch.Clear();
            }
            // Sacrificed lead blockers: drop them once their exemption window has elapsed (the car
            // survived and is protected normally again) or the entity is gone (it despawned as
            // intended). Either way the record has done its job.
            if (m_States.Sacrifice.Count != 0)
            {
                m_PruneScratch.Clear();
                foreach (KeyValuePair<Entity, uint> entry in m_States.Sacrifice)
                {
                    if (frame > entry.Value || !EntityManager.Exists(entry.Key))
                    {
                        m_PruneScratch.Add(entry.Key);
                    }
                }
                for (int i = 0; i < m_PruneScratch.Count; i++)
                {
                    m_States.Sacrifice.Remove(m_PruneScratch[i]);
                }
                m_PruneScratch.Clear();
            }
            // Each collaborator prunes the state it owns.
            modContext.PushedCars.Prune(frame);
            m_Corridor.Prune(frame);
            modRecovery.Prune(frame);
        }
















    }
}

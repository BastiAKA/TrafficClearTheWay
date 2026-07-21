using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Creatures;
using Game.Objects;
using Game.Pathfind;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes;

namespace ClearTheWay
{
    /// <summary>
    /// Pedestrians wait at the kerb while an emergency vehicle passes.
    ///
    /// Vanilla pedestrians have no notion of an approaching responder: HumanNavigationSystem
    /// only ever makes them wait via CreatureLaneFlags.WaitSignal, which it sets itself when
    /// the crosswalk lane they are about to enter carries a LaneSignal in Stop/SafeStop. An
    /// unsignalled zebra crossing has no LaneSignal at all, so they simply walk out in front
    /// of the ambulance.
    ///
    /// So we do the deciding and let their own navigation do the standing: a pedestrian whose
    /// NEXT path element is a crosswalk gets a zero speed budget plus the exact "wait" pose
    /// vanilla uses for a red light (target = own position, no target direction, no activity).
    /// Pedestrians ALREADY on a crosswalk are deliberately left alone - they are in the road
    /// and freezing them there would block the very vehicle we are helping; letting them
    /// finish clears the lane fastest.
    ///
    /// Runs right before HumanMoveSystem, i.e. after HumanNavigationSystem has recomputed
    /// m_MaxSpeed - the same pre-move override pattern the car side uses (a write before
    /// navigation would simply be overwritten). Nothing is stored: stop writing and they walk
    /// on by themselves, so there is no flag that could strand a citizen.
    /// </summary>
    public partial class ClearTheWayPedestrianSystem : GameSystemBase
    {
        private const float kStopRange = 40f;     // an emergency vehicle this close makes them wait
        private const float kBehindRange = -8f;   // ... unless it has already passed them
        private const uint kSearchInterval = 4u;  // run the expensive lane search only every N ticks; re-apply the hold to the (small) held set cheaply in between

        private SimulationSystem m_SimulationSystem;
        private Game.Net.SearchSystem m_NetSearchSystem;
        private EntityQuery m_EmergencyQuery;

        // The full lane search (a quadtree box per siren + iterating every foot lane's
        // occupants) was the pedestrian system's whole cost and ran EVERY tick. But the
        // decision "this pedestrian should wait" is stable for a few ticks, and the set of
        // held pedestrians is tiny (usually a handful). So we search only every kSearchInterval
        // ticks and, in between, just re-assert the wait pose on the cached set - which is cheap
        // and, crucially, keeps them stopped every tick (the hold is a per-tick override; a
        // naive throttle would let them walk on the off-ticks).
        private readonly System.Collections.Generic.HashSet<Entity> m_Held = new System.Collections.Generic.HashSet<Entity>();
        private readonly System.Collections.Generic.List<Entity> m_ReholdScratch = new System.Collections.Generic.List<Entity>();
        private readonly System.Collections.Generic.HashSet<Entity> m_SeenLanes = new System.Collections.Generic.HashSet<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();

            m_EmergencyQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<Moving>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<ParkedCar>()
                }
            });
        }

        protected override void OnUpdate()
        {
            Setting setting = Mod.Setting;
            if (setting == null || !setting.Enabled || !setting.StopPedestrians ||
                m_EmergencyQuery.IsEmptyIgnoreFilter)
            {
                m_Held.Clear();
                return;
            }

            // Off-tick: skip the expensive lane search, just keep the already-held pedestrians
            // stopped (cheap - the set is tiny). Drop any that despawned, entered a vehicle or
            // stepped onto the crossing since.
            if (m_SimulationSystem.frameIndex % kSearchInterval != 0u)
            {
                if (m_Held.Count == 0)
                {
                    return;
                }
                m_ReholdScratch.Clear();
                m_ReholdScratch.AddRange(m_Held);
                for (int i = 0; i < m_ReholdScratch.Count; i++)
                {
                    if (!ReapplyHold(m_ReholdScratch[i]))
                    {
                        m_Held.Remove(m_ReholdScratch[i]);
                    }
                }
                return;
            }

            // Search tick: rebuild the held set from scratch.
            m_Held.Clear();
            m_SeenLanes.Clear();

            NativeArray<Entity> vehicles = m_EmergencyQuery.ToEntityArray(Allocator.Temp);
            NativeList<Entity> foundLanes = new NativeList<Entity>(128, Allocator.Temp);
            bool treeFetched = false;
            NativeQuadTree<Entity, QuadTreeBoundsXZ> laneTree = default;
            int stopped = 0, sirens = 0, pedLaneHits = 0;
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    // Only vehicles actually running with lights and siren.
                    if ((EntityManager.GetComponentData<Car>(vehicle).m_Flags & CarFlags.Emergency) == 0)
                    {
                        continue;
                    }
                    // Trailers follow their controller.
                    if (VehicleTrailerExt.IsTrailer(EntityManager, vehicle))
                    {
                        continue;
                    }
                    sirens++;
                    // Fetch the LANE search tree once a siren is out (a main-thread job sync).
                    // Walking pedestrians live in their lane's LaneObject buffer, NOT in the
                    // moving search tree - Game.Creatures.ReferencesSystem only adds a creature
                    // to the moving tree when its current lane has no LaneObject buffer, and
                    // sidewalks/crosswalks all have one. So the old moving-tree lookup found
                    // zero pedestrians. Instead we find the pedestrian LANES near the responder
                    // and read who is standing on them.
                    if (!treeFetched)
                    {
                        laneTree = m_NetSearchSystem.GetLaneSearchTree(readOnly: true, out JobHandle deps);
                        deps.Complete();
                        treeFetched = true;
                    }
                    Transform vehicleTransform = EntityManager.GetComponentData<Transform>(vehicle);
                    float3 forward = math.forward(vehicleTransform.m_Rotation);
                    foundLanes.Clear();
                    AreaIterator iterator = new AreaIterator
                    {
                        m_Bounds = new Bounds3(vehicleTransform.m_Position - kStopRange,
                                               vehicleTransform.m_Position + kStopRange),
                        m_Results = foundLanes
                    };
                    laneTree.Iterate(ref iterator);
                    for (int l = 0; l < foundLanes.Length; l++)
                    {
                        Entity lane = foundLanes[l];
                        // Dedup: a foot lane covered by several sirens is processed once per tick.
                        if (!m_SeenLanes.Add(lane))
                        {
                            continue;
                        }
                        // Only foot lanes hold pedestrians we can wait; skip car/track/utility
                        // lanes. The pedestrian about to cross is on the approaching sidewalk
                        // lane (its LaneObject buffer), not yet on the crosswalk itself.
                        if (!EntityManager.HasComponent<Game.Net.PedestrianLane>(lane) ||
                            !EntityManager.HasBuffer<Game.Net.LaneObject>(lane))
                        {
                            continue;
                        }
                        pedLaneHits++;
                        DynamicBuffer<Game.Net.LaneObject> occupants =
                            EntityManager.GetBuffer<Game.Net.LaneObject>(lane, isReadOnly: true);
                        for (int o = 0; o < occupants.Length; o++)
                        {
                            Entity occupant = occupants[o].m_LaneObject;
                            if (occupant == Entity.Null || !EntityManager.Exists(occupant))
                            {
                                continue;
                            }
                            if (TryHoldPedestrian(occupant, vehicleTransform.m_Position, forward))
                            {
                                stopped++;
                                m_Held.Add(occupant);
                            }
                        }
                    }
                }
            }
            finally
            {
                foundLanes.Dispose();
                vehicles.Dispose();
            }
            if (treeFetched)
            {
                m_NetSearchSystem.AddLaneSearchTreeReader(default(JobHandle));
            }

            // Log whenever a siren is out (not only when we held someone) so a silent zero is
            // diagnosable: sirens>0 pedLanes=0 means the lane lookup found no foot lanes in
            // range; pedLanes>0 stopped=0 means found lanes but nobody was about to cross.
            if (setting.VerboseLogging && sirens > 0 && m_SimulationSystem.frameIndex % 120u == 0u)
            {
                Mod.Log.Info($"[pedestrians] {sirens} siren(s), {pedLaneHits} foot-lane hit(s), holding {stopped} at the kerb");
            }
        }

        /// <summary>Makes one pedestrian wait if it is about to step onto a crosswalk in front
        /// of the responder. Returns true when it was held this tick.</summary>
        private bool TryHoldPedestrian(Entity pedestrian, float3 vehiclePos, float3 vehicleForward)
        {
            if (!EntityManager.HasComponent<Human>(pedestrian) ||
                !EntityManager.HasComponent<HumanNavigation>(pedestrian) ||
                !EntityManager.HasComponent<HumanCurrentLane>(pedestrian) ||
                !EntityManager.HasComponent<Transform>(pedestrian) ||
                !EntityManager.HasComponent<PathOwner>(pedestrian) ||
                !EntityManager.HasBuffer<PathElement>(pedestrian))
            {
                return false;
            }
            // Riding in a vehicle, or a responder on foot (paramedic/officer) - never held.
            if (EntityManager.HasComponent<CurrentVehicle>(pedestrian) &&
                EntityManager.GetComponentData<CurrentVehicle>(pedestrian).m_Vehicle != Entity.Null)
            {
                return false;
            }
            if ((EntityManager.GetComponentData<Human>(pedestrian).m_Flags & HumanFlags.Emergency) != 0)
            {
                return false;
            }

            Transform transform = EntityManager.GetComponentData<Transform>(pedestrian);
            // Behind the responder already? Then it may walk.
            if (math.dot(transform.m_Position - vehiclePos, vehicleForward) < kBehindRange)
            {
                return false;
            }

            HumanCurrentLane currentLane = EntityManager.GetComponentData<HumanCurrentLane>(pedestrian);
            // Already out on a crosswalk: let it finish. Freezing it in the roadway would
            // block the responder instead of helping it.
            if (IsCrosswalk(currentLane.m_Lane))
            {
                return false;
            }
            // About to step onto one?
            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(pedestrian);
            DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(pedestrian, isReadOnly: true);
            if (pathOwner.m_ElementIndex >= path.Length ||
                !IsCrosswalk(path[pathOwner.m_ElementIndex].m_Target))
            {
                return false;
            }

            // Stand still exactly the way vanilla stands at a red light (HumanNavigationSystem's
            // WaitSignal branch): no speed budget, target on the spot, no heading, no activity.
            HumanNavigation navigation = EntityManager.GetComponentData<HumanNavigation>(pedestrian);
            navigation.m_MaxSpeed = 0f;
            navigation.m_TargetPosition = transform.m_Position;
            navigation.m_TargetDirection = default;
            navigation.m_TargetActivity = 0;
            EntityManager.SetComponentData(pedestrian, navigation);
            return true;
        }

        /// <summary>Cheap re-assert of the wait pose on an already-decided held pedestrian, used
        /// on the ticks between full searches. Returns false (drop it from the held set) if it
        /// no longer exists, entered a vehicle, or has stepped onto the crossing.</summary>
        private bool ReapplyHold(Entity pedestrian)
        {
            if (!EntityManager.Exists(pedestrian) ||
                !EntityManager.HasComponent<HumanNavigation>(pedestrian) ||
                !EntityManager.HasComponent<HumanCurrentLane>(pedestrian) ||
                !EntityManager.HasComponent<Transform>(pedestrian))
            {
                return false;
            }
            if (EntityManager.HasComponent<CurrentVehicle>(pedestrian) &&
                EntityManager.GetComponentData<CurrentVehicle>(pedestrian).m_Vehicle != Entity.Null)
            {
                return false;
            }
            if (IsCrosswalk(EntityManager.GetComponentData<HumanCurrentLane>(pedestrian).m_Lane))
            {
                return false; // on the crossing now - let it finish
            }
            Transform transform = EntityManager.GetComponentData<Transform>(pedestrian);
            HumanNavigation navigation = EntityManager.GetComponentData<HumanNavigation>(pedestrian);
            navigation.m_MaxSpeed = 0f;
            navigation.m_TargetPosition = transform.m_Position;
            navigation.m_TargetDirection = default;
            navigation.m_TargetActivity = 0;
            EntityManager.SetComponentData(pedestrian, navigation);
            return true;
        }

        private bool IsCrosswalk(Entity lane)
        {
            return lane != Entity.Null &&
                EntityManager.HasComponent<Game.Net.PedestrianLane>(lane) &&
                (EntityManager.GetComponentData<Game.Net.PedestrianLane>(lane).m_Flags &
                    Game.Net.PedestrianLaneFlags.Crosswalk) != 0;
        }
    }
}

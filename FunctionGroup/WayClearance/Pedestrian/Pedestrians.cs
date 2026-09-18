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
        // Search cadence. Was 4, which measured at ~10% of the whole sim tick in Sebastian's
        // city (2026-08-04 A/B: same session, same save, StopPedestrians toggled mid-run -
        // 53.0 -> 47.6 at 45-49 responders, 51.3 -> 46.4 at 55-59). The cost is directly
        // proportional to this number and almost nothing is lost by raising it: the hold is
        // re-asserted on EVERY tick from the cached set (see below), so only the DETECTION of
        // a newly approaching pedestrian is delayed - ~0.2 s at 47 ticks/s, about 30 cm of
        // walking.
        private const uint kSearchInterval = 10u;
        // Lateral / vertical half-extent of the search box. The old box was a cube of
        // +-kStopRange, i.e. 40 m up, down, sideways AND backwards, even though everything
        // behind kBehindRange is thrown away again by the dot test in TryHoldPedestrian and
        // nothing above or below street level can ever be a pedestrian about to cross in front
        // of this responder. On a map with tunnels or elevated roads that vertical reach pulled
        // whole extra lane layers into the result set.
        // Sebastian's call after watching it in-game (2026-08-04): 20 m, not 40. What this pass
        // has to get right is the crossing DIRECTLY in front of the responder; a pedestrian 30 m
        // abeam is on another street and holding it never helped. Note the box follows the
        // vehicle's HEADING (math.forward), not its route - the route-bound one is the corridor
        // in CorridorBuilder. So a responder turning into a junction sweeps its box around as the
        // nose comes round, and picks up the exit leg's crossing a little later than a box
        // centred on the vehicle would have. Accepted deliberately: pedestrians are erratic
        // anyway, and no downside was visible in play.
        private const float kSideRange = 20f;
        private const float kHeightRange = 8f;
        // A responder blocked by the same thing for this long (~60 s) is not about to pass this
        // crossing, so it stops holding anyone. The hold never blocks a car - held pedestrians wait
        // at the kerb, out of the road - so the cost of getting this wrong falls entirely on the
        // citizens: a wedged siren 30 m away would otherwise freeze everyone on the pavement
        // indefinitely, and nothing in the pass had an upper bound on that. Deliberately longer
        // than an ordinary red-light wait and shorter than the escalation stages, which are the two
        // things it must sit between.
        private const uint kHoldGiveUpFrames = 3600u;

        private SimulationSystem m_SimulationSystem;
        private Game.Net.SearchSystem m_NetSearchSystem;
        private ClearTheWaySystem m_ClearTheWaySystem;
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
            m_ClearTheWaySystem = World.GetOrCreateSystemManaged<ClearTheWaySystem>();

            // The Any filter is what keeps this affordable. Without it the query matches EVERY
            // moving car in the city: IsEmptyIgnoreFilter below then never trips, and each
            // search tick pulled the whole fleet through ToEntityArray plus two component
            // lookups per car (the CarFlags.Emergency test and IsTrailer) just to find the two
            // or three vehicles actually running a siren. Sirens only ever sit on these three
            // vehicle types - same reasoning as WayClearanceQueries.Emergency, which is why the
            // archetype filter belongs here and not only in the loop.
            m_EmergencyQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<Moving>()
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Vehicles.PoliceCar>(),
                    ComponentType.ReadOnly<Game.Vehicles.Ambulance>(),
                    ComponentType.ReadOnly<Game.Vehicles.FireEngine>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<ParkedCar>()
                }
            });
        }

        // Main-thread profiling switch for this pass - see ModProfiler for how to use it.
        // This class had no switch at all, which is why its cost had to be established the hard
        // way (toggling StopPedestrians mid-session and comparing halves) instead of simply
        // being read off a [perf] line. It measured at ~10% of the tick, so it is exactly the
        // pass that needed one.
        private static readonly bool kProfile = false;

        protected override void OnUpdate()
        {
            using (ModProfiler.Sample(kProfile, "Pedestrians"))
            {
                RunPass();
            }
            ModProfiler.EndTick(kProfile, "Pedestrians");
        }

        private void RunPass()
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
                    // A responder that has been wedged behind the same blocker for a minute is not
                    // going to reach this crossing any time soon, and holding people at the kerb
                    // for it has no upside left - only citizens standing still for as long as it
                    // stays stuck. Let them cross; the pass picks the responder up again the moment
                    // it starts moving (the stuck clock counts distance, so it resets on real
                    // progress and not on a creep).
                    if (m_ClearTheWaySystem.StuckFrames(vehicle, m_SimulationSystem.frameIndex) >= kHoldGiveUpFrames)
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
                    // Forward-biased box instead of a cube around the vehicle. The region that
                    // can actually produce a hold runs from kBehindRange (8 m behind) to
                    // kStopRange (40 m ahead) ALONG THE HEADING - a cube around the vehicle
                    // spends more than half its volume on ground that TryHoldPedestrian rejects
                    // on its first test. Bounds3 is axis-aligned and the heading is not, so the
                    // box is the AABB enclosing both ends of that stretch, padded sideways and
                    // vertically. AreaIterator does test Y (MathUtils.Intersect on the full
                    // Bounds3), so the height bound bites too.
                    float3 nearEnd = vehicleTransform.m_Position + forward * kBehindRange;
                    float3 farEnd = vehicleTransform.m_Position + forward * kStopRange;
                    float3 pad = new float3(kSideRange, kHeightRange, kSideRange);
                    AreaIterator iterator = new AreaIterator
                    {
                        m_Bounds = new Bounds3(math.min(nearEnd, farEnd) - pad,
                                               math.max(nearEnd, farEnd) + pad),
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
            // BEHIND-TEST FIRST. It used to sit ninth, after six HasComponent calls plus the
            // CurrentVehicle and Human-flag lookups - and it is the test that rejects the most,
            // because the search box necessarily reaches past the responder. It needs nothing
            // but Transform, so everything the responder has already driven past now costs one
            // lookup instead of nine. Order only, no behavioural change: every occupant that
            // survived to here before still survives to here now.
            if (!EntityManager.HasComponent<Transform>(pedestrian))
            {
                return false;
            }
            Transform transform = EntityManager.GetComponentData<Transform>(pedestrian);
            if (math.dot(transform.m_Position - vehiclePos, vehicleForward) < kBehindRange)
            {
                return false;
            }

            if (!EntityManager.HasComponent<Human>(pedestrian) ||
                !EntityManager.HasComponent<HumanNavigation>(pedestrian) ||
                !EntityManager.HasComponent<HumanCurrentLane>(pedestrian) ||
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

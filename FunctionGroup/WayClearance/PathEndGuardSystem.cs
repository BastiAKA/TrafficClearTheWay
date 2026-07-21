using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;

namespace ClearTheWay
{
    /// <summary>
    /// Stops the queue behind a fully-blocking accident from despawning. On a directional
    /// road (e.g. a 2-lane one-way highway) where a wreck blocks every lane, the cars behind
    /// have no route past it: the game truncates their path to the wreck, CarNavigationSystem
    /// sets CarLaneFlags.EndOfPath|EndReached, and PersonalCarAISystem then deletes them at
    /// "path end" (VehicleUtils.PathEndReached) - NOT via PathFlags.Failed/Stuck, which is why
    /// clearing those never helped. This runs BETWEEN CarNavigationSystem (which sets the flag)
    /// and PersonalCarAISystem (which reads it), and clears EndOfPath|EndReached on cars right
    /// at a wreck so PathEndReached is false and the AI leaves them waiting. The gate matches
    /// PathEndReached exactly (EndOfPath|EndReached set, ParkingSpace|Waypoint clear), so cars
    /// genuinely arriving to park/at a waypoint near a wreck are untouched. When the wreck is
    /// finally cleared the game adds PathfindUpdated to its lane and the waiting cars repath on
    /// their own - so no infinite pile-up beyond the wreck's (already capped) lifetime.
    /// </summary>
    public partial class PathEndGuardSystem : GameSystemBase
    {
        // Tombstone diagnostics proved the queue's paths get TRUNCATED at segment/lane
        // boundaries well before the wreck (EndOfPath set 30-100+ m short of it), so the
        // guard must cover the whole queue, not just cars right at the wreck. Real arrivals
        // stay safe: the flag gate excludes ParkingSpace/Waypoint/Connection arrivals.
        // Matches the main pass's shield radius (see kAccidentQueueRange).
        private const float kPathEndGuardRange = 300f;
        // Match ONLY a truncated dead-end (EndOfPath|EndReached) - never genuine arrivals:
        // parking spots, waypoints, building connections and parking areas are excluded.
        private const CarLaneFlags kPathEndMask = CarLaneFlags.EndOfPath | CarLaneFlags.EndReached |
            CarLaneFlags.ParkingSpace | CarLaneFlags.Waypoint | CarLaneFlags.Connection | CarLaneFlags.Area;
        private const CarLaneFlags kPathEndValue = CarLaneFlags.EndOfPath | CarLaneFlags.EndReached;

        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = false;

        private SimulationSystem m_SimulationSystem;
        private Game.Objects.SearchSystem m_ObjectSearchSystem;
        private ClearTheWaySystem m_MainSystem;
        private EntityQuery m_WreckQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_ObjectSearchSystem = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_MainSystem = World.GetOrCreateSystemManaged<ClearTheWaySystem>();
            m_WreckQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Events.InvolvedInAccident>(),
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<CarCurrentLane>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
        }

        protected override void OnUpdate()
        {
            Setting setting = Mod.Setting;
            if (setting == null || !setting.Enabled || !setting.PreventAccidentDespawn ||
                m_WreckQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            using (ModProfiler.Sample(kProfile, "PathEndGuardSystem"))
            {
                RunGuard(setting);
            }
            ModProfiler.EndTick(kProfile, "PathEndGuardSystem");
        }

        private void RunGuard(Setting setting)
        {
            uint frame = m_SimulationSystem.frameIndex;
            GuardStats stats = default;
            if (m_MainSystem.Accidents.NearWreckCarsFrame == frame)
            {
                // Reuse the main pass's deduped near-wreck sweep from THIS tick (it runs
                // pre-navigation, we run post-navigation; cars move well under a meter per
                // tick, so the set is identical). This replaces one 600 m quadtree traversal
                // PER WRECK plus a main-thread job sync every tick - together with the main
                // pass's per-wreck sweeps that was the post-accident frame-rate drop
                // (main thread bound, CPU/GPU idle).
                List<Entity> near = m_MainSystem.Accidents.NearWreckCars;
                for (int i = 0; i < near.Count; i++)
                {
                    GuardCar(near[i], ref stats);
                }
            }
            else
            {
                // Fallback (cache stale, e.g. the instant a setting was toggled): own sweep.
                NativeArray<Entity> wrecks = m_WreckQuery.ToEntityArray(Allocator.Temp);
                NativeQuadTree<Entity, QuadTreeBoundsXZ> tree =
                    m_ObjectSearchSystem.GetMovingSearchTree(readOnly: true, out JobHandle deps);
                deps.Complete();
                NativeList<Entity> found = new NativeList<Entity>(64, Allocator.Temp);
                try
                {
                    for (int w = 0; w < wrecks.Length; w++)
                    {
                        Entity wreck = wrecks[w];
                        if (!EntityManager.HasComponent<Transform>(wreck))
                        {
                            continue;
                        }
                        float3 pos = EntityManager.GetComponentData<Transform>(wreck).m_Position;
                        found.Clear();
                        AreaIterator iterator = new AreaIterator
                        {
                            m_Bounds = new Bounds3(pos - kPathEndGuardRange, pos + kPathEndGuardRange),
                            m_Results = found
                        };
                        tree.Iterate(ref iterator);
                        for (int i = 0; i < found.Length; i++)
                        {
                            GuardCar(found[i], ref stats);
                        }
                    }
                }
                finally
                {
                    found.Dispose();
                    wrecks.Dispose();
                }
                m_ObjectSearchSystem.AddMovingSearchTreeReader(default(JobHandle));
            }

            // Heartbeat: log every 120 frames whenever wrecks exist, even at zero, so we can
            // tell the system runs AND see what state the near-wreck cars are actually in.
            if (setting.VerboseLogging && frame % 120u == 0u)
            {
                Mod.Log.Info($"[endguard] wrecks={m_WreckQuery.CalculateEntityCount()} near={stats.nNear} " +
                    $"endReached={stats.nEndReached} failedStuck={stats.nFailedStuck} guarded={stats.guarded} " +
                    $"laneOr=0x{stats.laneOr:X} pathOr=0x{stats.pathOr:X} involved={stats.nInvolved} noTarget={stats.nNoTarget} pending={stats.nPending}");
            }
        }

        /// <summary>Per-car path-end guard, shared by the cached and the fallback sweep.
        /// A car may appear more than once in the fallback path (overlapping wreck boxes) -
        /// every operation here is idempotent, so that is merely counted twice.</summary>
        private void GuardCar(Entity other, ref GuardStats stats)
        {
            if (!EntityManager.Exists(other) ||
                !EntityManager.HasComponent<Car>(other) ||
                !EntityManager.HasComponent<CarCurrentLane>(other))
            {
                return;
            }
            // Never hold vehicles that are SUPPOSED to reach this spot: emergency
            // vehicles and recovery/maintenance vehicles have the accident itself
            // as their destination - clearing their path end would strand them.
            if ((EntityManager.GetComponentData<Car>(other).m_Flags & CarFlags.Emergency) != 0 ||
                EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(other))
            {
                return;
            }
            stats.nNear++;

            // Clear a truncated path end so the AI leaves the car waiting. Match
            // PathEndReached exactly (EndOfPath|EndReached set, ParkingSpace|Waypoint
            // clear) so real park/waypoint arrivals near a wreck are untouched.
            CarCurrentLane lane = EntityManager.GetComponentData<CarCurrentLane>(other);
            stats.laneOr |= (uint)lane.m_LaneFlags;
            if ((lane.m_LaneFlags & kPathEndMask) == kPathEndValue)
            {
                stats.nEndReached++;
                lane.m_LaneFlags &= ~(CarLaneFlags.EndOfPath | CarLaneFlags.EndReached);
                EntityManager.SetComponentData(other, lane);
                stats.guarded++;
            }

            // Diagnostics to find the ACTUAL delete trigger (near cars carry neither
            // EndReached nor Failed/Stuck when we scan, yet they despawn).
            if (EntityManager.HasComponent<Game.Events.InvolvedInAccident>(other)) stats.nInvolved++;
            if (EntityManager.HasComponent<Target>(other) &&
                !EntityManager.Exists(EntityManager.GetComponentData<Target>(other).m_Target)) stats.nNoTarget++;

            // Also drop Stuck|Failed AND the repath triggers (Obsolete|Divert-
            // Obsolete) for EVERY car at a wreck. The repath is what kills the
            // queue: at a full block the pathfinder hands out a disposal path
            // into a building connection and the car evaporates mid-drive. This
            // guard runs right before the vehicle AIs, closing the same-frame
            // window the main pass (pre-navigation) cannot cover.
            if (EntityManager.HasComponent<PathOwner>(other))
            {
                PathOwner po = EntityManager.GetComponentData<PathOwner>(other);
                stats.pathOr |= (uint)po.m_State;
                if ((po.m_State & PathFlags.Pending) != 0) stats.nPending++;
                if ((po.m_State & (PathFlags.Stuck | PathFlags.Failed |
                    PathFlags.Obsolete | PathFlags.DivertObsolete)) != 0)
                {
                    if ((po.m_State & (PathFlags.Stuck | PathFlags.Failed)) != 0)
                    {
                        stats.nFailedStuck++;
                    }
                    po.m_State &= ~(PathFlags.Stuck | PathFlags.Failed |
                        PathFlags.Obsolete | PathFlags.DivertObsolete);
                    EntityManager.SetComponentData(other, po);
                }
            }
        }
    }
}

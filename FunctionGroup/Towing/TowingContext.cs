using System.Collections.Generic;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace ClearTheWay
{
    /// <summary>
    /// The collaborators and cross-pass state of the towing group, in one place - the towing
    /// counterpart to <see cref="WayClearanceContext"/>.
    ///
    /// Towing is one chain across several passes (couple a wreck, keep the truck heading home,
    /// drag the wreck along behind it, sweep up whatever is orphaned, re-arm old relics), and the
    /// passes share a handful of sets that decide OWNERSHIP of a wreck. Those sets are the
    /// subtle part, so they live here with the reasoning attached rather than inside whichever
    /// pass happened to need them first.
    ///
    /// Fields, assigned in two phases by <see cref="TowHookupSystem"/>: a pass may only
    /// dereference the context from its methods, never from its own constructor.
    /// </summary>
    internal sealed class TowingContext
    {
        public EntityManager EntityManager;
        public SimulationSystem Simulation;
        /// <summary>Road network search tree provider, for putting a wedged truck back on a road.</summary>
        public Game.Net.SearchSystem NetSearch;

        /// <summary>The way-clearance system, read-only and only for diagnostics: its
        /// SpeedOverrides answer the one question a haul-phase log line cannot answer from the
        /// towing side alone - is the truck standing because traffic blocks it, or because WE are
        /// capping its speed? Without it a motionless loaded truck is indistinguishable from one
        /// our own corridor is holding.</summary>
        public ClearTheWaySystem WayClearance;

        // Queries the passes below run on; created by TowHookupSystem (only a system can).
        public EntityQuery OrphanQuery;
        public EntityQuery TrailerOrphanQuery;
        public EntityQuery ArmedRelicQuery;
        public EntityQuery RelicDiagQuery;
        public EntityQuery RelicSweepDryRun;
        public EntityQuery RearmDeadQuery;
        public EntityQuery OrphanCandidateQuery;
        public EntityQuery FrozenReleaseQuery;
        public EntityQuery AccidentSiteQuery;

        /// <summary>Deletes a vehicle safely. A Moving-less entity that still carries a
        /// Game.Simulation.UpdateFrame null-derefs UpdateGroupSystem ("UpdateFrame added to
        /// unsupported type" - a hard crash chased down in an earlier towing rework), so a settled
        /// wreck loses it before Deleted.
        ///
        /// A DRIVING vehicle keeps it: it is a supported type, and the entity lives until the end
        /// of the frame. If the player has it selected or follows it with the camera,
        /// SelectedInfoUISystem.GetInterpolatedPosition reads UpdateFrame unguarded for every
        /// entity with a TransformFrame ring - stripping it from a truck threw the
        /// "AssertionException ... MarkerIconSystem / CameraUpdateSystem" of 2026-10-17.</summary>
        public void SafeDelete(Entity vehicle)
        {
            if (!Lookups.HasComponent<Game.Objects.Moving>(vehicle) &&
                EntityManager.HasComponent<Game.Simulation.UpdateFrame>(vehicle))
            {
                EntityManager.RemoveComponent<Game.Simulation.UpdateFrame>(vehicle);
            }
            EntityManager.AddComponent<Game.Common.Deleted>(vehicle);
        }

        /// <summary>
        /// Takes a hooked wreck out of the lane registry, so it stops being a road blockage -
        /// THE reason loaded trucks stood still with a free road ahead and were deleted.
        ///
        /// A settled wreck keeps OutOfControl, CarCurrentLane and UpdateFrame, so
        /// CarNavigationSystem's out-of-control branch re-registers it every 16 frames on every
        /// lane its body touches (BlockedLane buffer + the lanes' LaneObject buffers), wherever we
        /// teleport it. It has no Moving - the hookup strips it on purpose - and
        /// LaneDataSystem.CheckBlockage counts every lane object WITHOUT Moving as a blockage
        /// (m_BlockageStart/End). The pathfinder treats that range as impassable
        /// (PathfindJobs.IsValidDelta). The wreck hangs right behind the truck, its blocked range
        /// reaches past its body by radius + 0.4 lane widths, and once the truck's own position
        /// lies inside it, no route exists in any direction on a two-lane road: Failed, retry,
        /// Failed - until the give-up released the wreck and deleted the truck (7 of 8 tows on
        /// 2026-10-17, and the same pattern in every earlier log).
        ///
        /// Unregistered the way the game does it (NetUtils.RemoveLaneObject), the lanes are
        /// flagged PathfindUpdated so their blockage is recomputed, and CarCurrentLane is removed
        /// so the navigation query no longer picks the wreck up and re-registers it. Idempotent:
        /// a wreck without CarCurrentLane is already detached. <see cref="ReattachToLanes"/>
        /// undoes it when the wreck comes off the hook without being deleted.
        /// </summary>
        public void DetachFromLanes(Entity wreck)
        {
            if (!Lookups.TryGetComponent(wreck, out Game.Vehicles.CarCurrentLane currentLane))
            {
                return;
            }
            // Buffer work first, structural changes after: a DynamicBuffer is a pointer into the
            // chunk and a structural change can move it.
            m_DetachLanes.Clear();
            if (Lookups.TryGetBuffer(wreck, out DynamicBuffer<Game.Objects.BlockedLane> blockedLanes))
            {
                for (int i = 0; i < blockedLanes.Length; i++)
                {
                    m_DetachLanes.Add(blockedLanes[i].m_Lane);
                }
                blockedLanes.Clear();
            }
            if (currentLane.m_Lane != Entity.Null)
            {
                m_DetachLanes.Add(currentLane.m_Lane);
            }
            if (currentLane.m_ChangeLane != Entity.Null)
            {
                m_DetachLanes.Add(currentLane.m_ChangeLane);
            }
            for (int i = 0; i < m_DetachLanes.Count; i++)
            {
                if (Lookups.TryGetBuffer(m_DetachLanes[i], out DynamicBuffer<Game.Net.LaneObject> laneObjects))
                {
                    Game.Net.NetUtils.RemoveLaneObject(laneObjects, wreck);
                }
            }
            for (int i = 0; i < m_DetachLanes.Count; i++)
            {
                Entity lane = m_DetachLanes[i];
                if (EntityManager.Exists(lane) && Lookups.HasComponent<Game.Net.CarLane>(lane) &&
                    !Lookups.HasComponent<Game.Common.PathfindUpdated>(lane))
                {
                    EntityManager.AddComponent<Game.Common.PathfindUpdated>(lane);
                }
            }
            EntityManager.RemoveComponent<Game.Vehicles.CarCurrentLane>(wreck);
        }

        /// <summary>
        /// Puts a wreck that comes off the hook WITHOUT being deleted back into the lane registry:
        /// with CarCurrentLane back, the next navigation tick registers it on the lanes it stands
        /// on, and it blocks the road like any wreck the recovery pipeline still has to collect.
        /// No-op for an entity that still has its CarCurrentLane (never detached) or is gone.
        /// </summary>
        public void ReattachToLanes(Entity wreck)
        {
            if (!EntityManager.Exists(wreck) || Lookups.HasComponent<Game.Common.Deleted>(wreck) ||
                Lookups.HasComponent<Game.Vehicles.CarCurrentLane>(wreck) ||
                !Lookups.HasComponent<Game.Vehicles.Car>(wreck))
            {
                return;
            }
            EntityManager.AddComponentData(wreck, default(Game.Vehicles.CarCurrentLane));
        }

        private readonly List<Entity> m_DetachLanes = new List<Entity>(8);

        // Passes.
        public TowCoupling Coupling;
        public TowFollow Follow;
        public TowOrphans Orphans;
        public TowRelics Relics;
        public TowRearmDeadObjects RearmDead;
        public TowOrphanFinder OrphanFinder;
        public TowReleaseFrozen ReleaseFrozen;
        public TowStuckRecovery StuckRecovery;

        /// <summary>Per-wreck rest tracking for the orphan finder: where a candidate wreck last was
        /// and since when it has not moved. A hauled wreck is teleported so its position keeps
        /// changing (timer resets); a stranded one sits, so its timer grows. See <see cref="TowOrphanFinder"/>.</summary>
        public readonly Dictionary<Entity, WreckRest> OrphanRest = new Dictionary<Entity, WreckRest>();

        /// <summary>
        /// Trucks currently carrying a wreck. Also read by the post-pass that flashes the amber
        /// beacons, since a recovery run must look like one.
        /// </summary>
        public readonly HashSet<Entity> TrucksWithLoad = new HashSet<Entity>();

        /// <summary>
        /// Wrecks this mod is towing right now.
        ///
        /// IN-MEMORY ONLY, which is exactly why it cannot be the ownership test on its own: it is
        /// empty after a save/load, and a towed wreck whose truck vanished across a load was then
        /// never recognised as ours again and stood on the road forever. The persistent
        /// <see cref="TowMarker"/> component is the durable signal; this set is the fast path.
        /// </summary>
        public readonly HashSet<Entity> OurTows = new HashSet<Entity>();

        /// <summary>Last travel direction of each tow, latched so the dragged wreck keeps
        /// trailing straight behind the truck instead of swinging when the truck slows.</summary>
        public readonly Dictionary<Entity, float3> TowTravelDir = new Dictionary<Entity, float3>();

        /// <summary>Previous truck position per tow, used to derive that travel direction.</summary>
        public readonly Dictionary<Entity, float3> LastTruckPos = new Dictionary<Entity, float3>();

        /// <summary>Frame each relic was re-armed for recovery, for the delete fallback timeout.
        /// Session-only; re-seeded after a load from the persistent RelicRecovery tag.</summary>
        public readonly Dictionary<Entity, uint> RelicArmedFrame = new Dictionary<Entity, uint>();

        /// <summary>Frame a LOADED tow truck last asked for a new route home, so a repeatedly
        /// failing path is repathed at once but then throttled. See <see cref="PathShield"/>.</summary>
        public readonly Dictionary<Entity, uint> PathRetry = new Dictionary<Entity, uint>();

        /// <summary>Frame a loaded tow truck's route home FIRST failed (cleared as soon as it has
        /// one again). Bounds how long <see cref="PathShield"/> keeps it alive without a path.</summary>
        public readonly Dictionary<Entity, uint> PathFailingSince = new Dictionary<Entity, uint>();

        /// <summary>Where each hauling truck last was, for the motionless test in
        /// <see cref="TowStuckRecovery"/>.</summary>
        public readonly Dictionary<Entity, TruckRest> TruckRest = new Dictionary<Entity, TruckRest>();

        /// <summary>Frame each frozen-but-driveable car was first seen in that state, so the
        /// release pass can let it settle before acting. See <see cref="TowReleaseFrozen"/>.</summary>
        public readonly Dictionary<Entity, uint> FrozenSince = new Dictionary<Entity, uint>();
    }
}

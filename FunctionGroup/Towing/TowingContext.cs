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
        /// Remembers the lanes a wreck blocked at the moment it is hooked, so their blockage can
        /// be recomputed once it has been dragged away - THE reason loaded trucks stood still on a
        /// free road, failed every pathfind and were deleted (7 of 8 tows on 2026-10-17, 2 of 2 that
        /// morning on 0.1.13.3, 5 of 15 on 2026-10-11).
        ///
        /// The blockage is LANE data: CarLane.m_BlockageStart/End, computed by LaneDataSystem
        /// (CheckBlockage: every lane object without Moving) and only when the lane carries
        /// Updated or PathfindUpdated. AccidentVehicleSystem flags the lanes when the car comes
        /// to rest; dragging the wreck away moves its lane objects (LaneObjectUpdater), but
        /// nothing flags the lanes again - so the accident site stays "blocked" after the wreck
        /// is gone. PathfindJobs.IsValidDelta lets no route through that range, and the truck
        /// stands right behind it: no way forward, and cars cannot turn mid-segment. The same
        /// gap WreckClearing.PokeBlockedLanes closes after moving a wreck aside.
        ///
        /// The wreck must NOT be taken out of the lane registry instead: a Car without
        /// CarCurrentLane is an invalid state (PathfindTargetSeeker.GetCarLane and
        /// ImpactSystem read it unguarded) and crashed the game at the next hookup.
        /// </summary>
        public void ScheduleSitePoke(Entity wreck, uint frame)
        {
            if (!Lookups.TryGetBuffer(wreck, out DynamicBuffer<Game.Objects.BlockedLane> blockedLanes, isReadOnly: true) ||
                blockedLanes.Length == 0)
            {
                return;
            }
            var lanes = new List<Entity>(blockedLanes.Length);
            for (int i = 0; i < blockedLanes.Length; i++)
            {
                lanes.Add(blockedLanes[i].m_Lane);
            }
            m_SitePokes[wreck] = new SitePoke { m_DueFrame = frame + kSitePokeDelayFrames, m_Lanes = lanes };
        }

        /// <summary>
        /// Flags the remembered accident-site lanes PathfindUpdated once the wreck's lane objects
        /// have moved with it (two navigation cycles after the hookup), so LaneDataSystem drops
        /// the stale blockage. Returns true on the tick it fired, so the caller can ask the truck
        /// for a fresh route right away instead of waiting for the next retry.
        /// </summary>
        public bool RunSitePokeIfDue(Entity wreck, uint frame)
        {
            if (!m_SitePokes.TryGetValue(wreck, out SitePoke poke) || frame < poke.m_DueFrame)
            {
                return false;
            }
            m_SitePokes.Remove(wreck);
            for (int i = 0; i < poke.m_Lanes.Count; i++)
            {
                Entity lane = poke.m_Lanes[i];
                if (EntityManager.Exists(lane) && Lookups.HasComponent<Game.Net.CarLane>(lane) &&
                    !Lookups.HasComponent<Game.Common.PathfindUpdated>(lane))
                {
                    EntityManager.AddComponent<Game.Common.PathfindUpdated>(lane);
                }
            }
            return true;
        }

        /// <summary>Drops a pending poke for a wreck that leaves the tow early (released, deleted).</summary>
        public void ForgetSitePoke(Entity wreck)
        {
            m_SitePokes.Remove(wreck);
        }

        /// <summary>Two CarNavigationSystem cycles (it updates each vehicle every 16 frames): by
        /// then the wreck's lane objects have moved off the accident site, so the recompute sees
        /// the site empty.</summary>
        private const uint kSitePokeDelayFrames = 32u;

        private struct SitePoke
        {
            public uint m_DueFrame;
            public List<Entity> m_Lanes;
        }

        private readonly Dictionary<Entity, SitePoke> m_SitePokes = new Dictionary<Entity, SitePoke>();

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

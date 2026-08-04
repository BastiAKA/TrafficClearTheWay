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

        /// <summary>Deletes a settled wreck safely. A Moving-less entity that still carries a
        /// Game.Simulation.UpdateFrame null-derefs UpdateGroupSystem ("UpdateFrame added to
        /// unsupported type" - a hard crash chased down in an earlier towing rework), so strip
        /// it before adding Deleted.</summary>
        public void SafeDelete(Entity wreck)
        {
            if (EntityManager.HasComponent<Game.Simulation.UpdateFrame>(wreck))
            {
                EntityManager.RemoveComponent<Game.Simulation.UpdateFrame>(wreck);
            }
            EntityManager.AddComponent<Game.Common.Deleted>(wreck);
        }

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

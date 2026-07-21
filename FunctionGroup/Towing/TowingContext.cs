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

        // Queries the passes below run on; created by TowHookupSystem (only a system can).
        public EntityQuery OrphanQuery;
        public EntityQuery TrailerOrphanQuery;
        public EntityQuery ArmedRelicQuery;
        public EntityQuery RelicDiagQuery;
        public EntityQuery RelicSweepDryRun;
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
    }
}

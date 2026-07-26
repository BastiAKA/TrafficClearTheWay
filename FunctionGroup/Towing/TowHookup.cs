using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ClearTheWay
{
    /// <summary>
    /// Real towing instead of repair-in-place: when a recovery truck (MaintenanceType.Vehicle)
    /// stops at its wreck target, the wreck is coupled to it and hauled to the depot.
    ///
    /// IMPORTANT: this deliberately does NOT use the game's trailer machinery
    /// (LayoutElement + CarTrailerLane). CarTrailerMoveSystem reads CarTractorData /
    /// CarTrailerData straight off the prefabs without guards - maintenance trucks and
    /// ordinary cars do not have those, which hard-crashes the game (learned the hard way).
    /// Instead the wreck keeps a Controller reference as a tow marker and OUR follow pass
    /// drags it behind the truck every frame (position/rotation/velocity + the vehicle's
    /// TransformFrame ring so rendering interpolates properly). All components involved are
    /// vanilla and serializable, so saves mid-tow stay consistent - after loading, the pair
    /// is rediscovered purely from the component pattern.
    /// </summary>
    public partial class TowHookupSystem : GameSystemBase
    {
        private const float kHookupRange = 60f;    // couple when the truck is this close to its wreck target. Raised 32 -> 60 (Sebastian): trucks routinely ground to a halt at 44-55 m in the jam AROUND the accident - the last stretch is the one part of the trip the corridor cannot clear, because the queue there has nowhere to go. Vanilla itself works a wreck from up to 30 m, so this is already generous; the cost is that the wreck visibly slides up to the rope point behind the truck at hookup, which is more noticeable the further away it couples.
        private const float kHookupMaxSpeed = 3f;  // ... and this slow (it just pulled up / stopped)
        private const float kHitchDistance = 2.8f; // drawbar coupling point, behind the truck center (~rear bumper of a maintenance van)
        private const float kTrailDistance = 1.7f; // rope length: wreck center trails this far behind the coupling point (~4.5 m behind truck center = ~1-2 m gap behind the truck's rear; Sebastian's spec)
        private const float kFollowRate = 0.3f;    // per-tick lerp toward the point behind the truck (lower = smoother, higher = tighter/jumpier)

        private SimulationSystem m_SimulationSystem;
        private Game.Net.SearchSystem m_NetSearchSystem;
        private readonly System.Collections.Generic.List<Entity> m_PruneScratch = new System.Collections.Generic.List<Entity>();
        /// <summary>Shared towing collaborators and ownership state; see TowingContext.</summary>
        private TowingContext m_Ctx;
        private EntityQuery m_TruckQuery;
        private EntityQuery m_TowedQuery;
        private EntityQuery m_OrphanQuery;
        private EntityQuery m_TrailerOrphanQuery;
        // Diagnostic-only (VerboseLogging): leftover "relic" wrecks - damaged/destroyed cars that
        // no longer carry InvolvedInAccident (towed away and stripped, then stranded). They are
        // invisible to every accident-based query, so this finds them structurally to confirm the
        // eventual relic sweeper. + a count of AccidentSite entities to see if a site still holds them.
        private EntityQuery m_RelicDiagQuery;
        private EntityQuery m_AccidentSiteQuery;
        // Relics to re-arm for recovery: damaged/destroyed cars still carrying a (dead) tow
        // Controller and Stopped, but not a live accident wreck, not one of our active tows, and
        // not already re-armed. Re-arming drops the Controller, so an armed relic no longer matches.
        private EntityQuery m_RelicSweepDryRun;
        // Relics we have re-armed and are watching for the delete fallback.
        private EntityQuery m_ArmedRelicQuery;
        // Stranded relics that STILL carry our TowMarker (an abandoned tow): re-armed by
        // TowRearmDeadObjects once no actively-towing truck is near them - the gap the query above
        // leaves, since it excludes TowMarker.
        private EntityQuery m_RearmDeadQuery;
        // Widest net for the log-only orphan finder: any stopped, not-moving damaged/out-of-control
        // wreck. The finder's movement test decides which are actually stranded.
        private EntityQuery m_OrphanCandidateQuery;
        // Cars we froze: Stopped + OutOfControl with no damage and no live accident left. Not tow
        // targets - they are roadworthy and only missing the OutOfControl removal that our old
        // coupling took away from them. Released by TowReleaseFrozen.
        private EntityQuery m_FrozenReleaseQuery;
        // frame each relic was re-armed, for the recovery timeout (session-only; re-seeded on load
        // from the persistent RelicRecovery tag so the watchdog still works after a save/load).
        private const uint kRelicRecoveryTimeout = 18000u; // ~5 min @ ~58 f/s: recovered by a vehicle, else deleted
        // Wrecks confirmed to be on OUR hook (carrier is a recovery truck). Only these may be
        // removed once their carrier is gone - anything else near a tow truck is somebody
        // else's vehicle. Rebuilt continuously by the follow pass, so a save/load mid-tow at
        // worst leaves one wreck standing instead of deleting a stranger's trailer.
        /// <summary>Trucks currently hauling a wreck (rebuilt every tick by the follow pass).
        /// Read by the dispatch assist (skip as candidates) and the warning-light pass.</summary>
        internal HashSet<Entity> TrucksWithLoad => m_Ctx.TrucksWithLoad;
        // Last known travel direction per towing truck, derived from its actual per-frame movement
        // (not Moving.m_Velocity, whose direction here does NOT map to plain world-forward and put
        // the wreck in front). Latched across the accident jam's stop-and-go so the wreck stays
        // reliably behind. m_LastTruckPos feeds the position-delta that defines that direction.

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();

            m_TruckQuery = GetEntityQuery(TowQueries.TruckQuery);
            m_TowedQuery = GetEntityQuery(TowQueries.TowedQuery);
            m_OrphanQuery = GetEntityQuery(TowQueries.OrphanQuery);
            m_TrailerOrphanQuery = GetEntityQuery(TowQueries.TrailerOrphanQuery);
            m_RelicDiagQuery = GetEntityQuery(TowQueries.RelicDiagQuery);
            m_AccidentSiteQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Events.AccidentSite>());
            m_RelicSweepDryRun = GetEntityQuery(TowQueries.RelicSweepDryRun);
            m_ArmedRelicQuery = GetEntityQuery(TowQueries.ArmedRelicQuery);
            m_RearmDeadQuery = GetEntityQuery(TowQueries.RearmDeadQuery);
            m_OrphanCandidateQuery = GetEntityQuery(TowQueries.OrphanCandidateQuery);
            m_FrozenReleaseQuery = GetEntityQuery(TowQueries.FrozenReleaseQuery);

            // Two-phase construction: the context first, then the passes that reach each
            // other through it.
            m_Ctx = new TowingContext
            {
                EntityManager = EntityManager,
                Simulation = m_SimulationSystem,
                NetSearch = m_NetSearchSystem,
                OrphanQuery = m_OrphanQuery,
                TrailerOrphanQuery = m_TrailerOrphanQuery,
                ArmedRelicQuery = m_ArmedRelicQuery,
                RelicDiagQuery = m_RelicDiagQuery,
                RelicSweepDryRun = m_RelicSweepDryRun,
                RearmDeadQuery = m_RearmDeadQuery,
                OrphanCandidateQuery = m_OrphanCandidateQuery,
                FrozenReleaseQuery = m_FrozenReleaseQuery,
                AccidentSiteQuery = m_AccidentSiteQuery
            };
            m_Ctx.Coupling = new TowCoupling(m_Ctx);
            m_Ctx.Follow = new TowFollow(m_Ctx);
            m_Ctx.Orphans = new TowOrphans(m_Ctx);
            m_Ctx.Relics = new TowRelics(m_Ctx);
            m_Ctx.RearmDead = new TowRearmDeadObjects(m_Ctx);
            m_Ctx.OrphanFinder = new TowOrphanFinder(m_Ctx);
            m_Ctx.ReleaseFrozen = new TowReleaseFrozen(m_Ctx);
            m_Ctx.StuckRecovery = new TowStuckRecovery(m_Ctx);
        }

        protected override void OnUpdate()
        {
            Setting setting = Mod.Setting;
            if (setting == null || !setting.Enabled || !setting.TowWrecks)
            {
                return;
            }
            uint frame = m_SimulationSystem.frameIndex;

            // --- Follow pass: drag towed wrecks along behind their trucks ---
            m_Ctx.TrucksWithLoad.Clear();
            if (!m_TowedQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> towed = m_TowedQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < towed.Length; i++)
                    {
                        m_Ctx.Follow.FollowOrFinish(towed[i], frame, setting);
                    }
                }
                finally
                {
                    towed.Dispose();
                }
            }

            // NO flatbed cleanup pass any more. Flatbed towing is retired (the drawbar does
            // the job), so nothing here ever spawns a flatbed - yet the sweep still deleted
            // every CarTrailer whose prefab matched our Abschlepphaenger clone once its
            // tractor parked. And that clone is a REGISTERED CarTrailerPrefab, so the game's
            // own random trailer selection happily hands it to ordinary citizens: those were
            // civilian trailers, deleted out from under a parked car and leaving a dangling
            // LayoutElement entry ("der Trailer spinnt", 14 removals in 6 minutes of play).
            // Whatever tows our clone out there is somebody else's business.

            // --- Hookup pass ---
            if (!m_TruckQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> trucks = m_TruckQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < trucks.Length; i++)
                    {
                        m_Ctx.Coupling.TryHookup(trucks[i], frame);
                    }
                }
                finally
                {
                    trucks.Dispose();
                }
            }

            // --- Orphan sweep: delete towed wrecks the follow pass can no longer clean up ---
            if (frame % 32u == 0u)
            {
                m_Ctx.Orphans.OrphanSweep();
                m_Ctx.Orphans.OrphanTrailerSweep();
            }

            // --- Relic recovery: re-arm stranded leftover wrecks for regular recovery + watchdog ---
            if (frame % 64u == 0u)
            {
                m_Ctx.Relics.RelicRecoveryPass(frame, setting);
            }

            // --- Dead-object re-arm: stranded wrecks STILL carrying our TowMarker that no
            //     actively-towing truck is working (>500 m) - the case the relic re-arm above
            //     skips because it excludes TowMarker. Runs after the follow pass, so
            //     TrucksWithLoad is fresh for this tick. Staggered off the relic pass's frame. ---
            if (frame % 64u == 32u)
            {
                m_Ctx.RearmDead.RearmStrandedRelics(frame, setting);
            }

            // --- Release frozen cars: roadworthy vehicles left Stopped + OutOfControl by a tow
            //     that should never have coupled them. Nothing recovers these - no Damaged means
            //     no maintenance request, and no InvolvedInAccident means AccidentVehicleSystem
            //     can never lift the OutOfControl that blocks every vehicle AI. We hand them back
            //     to their own AI instead. Staggered off the other cleanup passes. ---
            if (frame % 64u == 16u)
            {
                m_Ctx.ReleaseFrozen.ReleaseFrozenCars(frame, setting);
            }

            // --- Orphan finder (LOG-ONLY): report every wreck that has not moved for a while, so
            //     we can confirm detection before the acting version strips/tags/dispatches. ---
            if (frame % 128u == 64u)
            {
                m_Ctx.OrphanFinder.FindOrphans(frame, setting);
            }

            // --- Housekeeping: drop entries for vehicles and wrecks that no longer exist. The
            //     maps below mostly clean up at the natural moment (tow finished, path recovered),
            //     this is the net underneath for everything that despawned mid-flow. Rare on
            //     purpose - small maps, and nothing depends on it being prompt. ---
            if ((frame & 0x3FFu) == 0u)
            {
                EntityMapPrune.PruneDead(EntityManager, m_Ctx.PathRetry, m_PruneScratch);
                EntityMapPrune.PruneDead(EntityManager, m_Ctx.PathFailingSince, m_PruneScratch);
                EntityMapPrune.PruneDead(EntityManager, m_Ctx.TruckRest, m_PruneScratch);
                EntityMapPrune.PruneDead(EntityManager, m_Ctx.RelicArmedFrame, m_PruneScratch);
                EntityMapPrune.PruneDead(EntityManager, m_Ctx.TowTravelDir, m_PruneScratch);
                EntityMapPrune.PruneDead(EntityManager, m_Ctx.LastTruckPos, m_PruneScratch);
                EntityMapPrune.PruneDead(EntityManager, m_Ctx.OurTows, m_PruneScratch);
                EntityMapPrune.PruneDead(EntityManager, m_Ctx.Coupling.NotTowTruckLogged, m_PruneScratch);
                EntityMapPrune.PruneDead(EntityManager, m_Ctx.Coupling.InvalidTargetLogged, m_PruneScratch);
            }

            // --- Relic finder (diagnostic, log-only) ---
            if (setting.VerboseLogging && frame % 300u == 0u)
            {
                m_Ctx.Relics.RelicDiagnostic();
            }
        }







        // SafeDelete now lives on TowingContext - every towing pass needs it.

        /// <summary>Set the component if the archetype already has it, otherwise add it.</summary>
        private void SetOrAdd<T>(Entity entity, T value) where T : unmanaged, IComponentData
        {
            if (EntityManager.HasComponent<T>(entity))
            {
                EntityManager.SetComponentData(entity, value);
            }
            else
            {
                EntityManager.AddComponentData(entity, value);
            }
        }

        private static System.Reflection.MethodInfo s_CreateEntityMethod;

        /// <summary>Calls EntityManager.CreateEntity(EntityArchetype) via reflection, because a
        /// DIRECT call does not compile here: CreateEntity's method group also has a
        /// ReadOnlySpan&lt;ComponentType&gt; overload, the compiler must resolve System.ReadOnlySpan
        /// to consider it, and the .NET Framework reference assemblies do not define it - so any
        /// CreateEntity call, even the EntityArchetype one, fails with CS0518.
        ///
        /// Currently UNUSED: its only caller was the flatbed spawn, now retired to TowFlatbed.cs.
        /// Kept because this workaround is needed again by ANY future code that creates an entity
        /// from an archetype, and the failure mode is obscure enough to be worth not rediscovering.</summary>
        private Entity CreateEntityFromArchetype(Unity.Entities.EntityArchetype archetype)
        {
            if (s_CreateEntityMethod == null)
            {
                s_CreateEntityMethod = typeof(EntityManager).GetMethod(
                    nameof(EntityManager.CreateEntity),
                    new[] { typeof(Unity.Entities.EntityArchetype) });
            }
            return (Entity)s_CreateEntityMethod.Invoke(EntityManager, new object[] { archetype });
        }
    }
}

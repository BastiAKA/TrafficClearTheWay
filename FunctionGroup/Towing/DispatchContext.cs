using System.Collections.Generic;
using Game.Simulation;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// Shared state and collaborators of the tow-dispatch pass.
    ///
    /// The interesting field here is <see cref="VanJob"/>. The pass used to iterate WRECKS, which
    /// meant a van holding several wreck requests counted as the responder for each of them and
    /// had its target rewritten several times per pass - it never held a path, stood still
    /// blocking the responder behind it, and the resulting repath flood despawned unrelated
    /// vehicles. Resolving exactly ONE job per van and keeping it here makes the pass
    /// self-settling: once a van's target already IS its job, nothing is written at all.
    /// </summary>
    internal sealed class DispatchContext
    {
        public EntityManager EntityManager;
        public SimulationSystem Simulation;
        public TowHookupSystem HookupSystem;

        // Queries and archetypes; created by TowDispatchSystem (only a system can).
        public EntityQuery WreckQuery;
        public EntityQuery VanQuery;
        public EntityQuery DepotQuery;
        public EntityQuery AllWreckQuery;         // diagnostics only
        public EntityQuery VanillaDamagedQuery;   // diagnostics only
        public EntityArchetype HandleRequestArchetype;

        // Passes.
        public TowAssignment Assignment;
        public TowBeeline Beeline;
        public TowDispatchReporting Reporting;

        /// <summary>Wreck -> frame the dispatcher last intervened for it. Rate-limits the
        /// interventions so two nearby wrecks cannot fight over the same trucks every pass.</summary>
        public readonly Dictionary<Entity, uint> LastAction = new Dictionary<Entity, uint>();

        /// <summary>Van -> frame it was last sent home. A van just released must not be claimed
        /// straight back by the other wreck, or the two ping-pong it between them forever.</summary>
        public readonly Dictionary<Entity, uint> VanSentHome = new Dictionary<Entity, uint>();

        /// <summary>Frame each van was last GIVEN a wreck order. A truck already on its way keeps
        /// it for kTakeoverGraceFrames, so a cluster of wrecks cannot re-decide the assignment out
        /// from under it every pass. See TowAssignment.</summary>
        /// <summary>Frame a van last asked for a new route to its wreck. See <see cref="PathShield"/>.</summary>
        public readonly Dictionary<Entity, uint> PathRetry = new Dictionary<Entity, uint>();

        public readonly Dictionary<Entity, uint> VanAssigned = new Dictionary<Entity, uint>();

        /// <summary>Van -> the ONE wreck it is working, resolved once per pass. See the class
        /// summary: this is what stopped the dispatch thrash.</summary>
        public readonly Dictionary<Entity, Entity> VanJob = new Dictionary<Entity, Entity>();

        /// <summary>Wreck -> the van THIS dispatcher last put on it. <see cref="VanJob"/> is rebuilt
        /// every pass from the vans' actual game state, so it only ever reflects an order the van
        /// has really taken on. That is what makes it trustworthy - and also what made a wreck at
        /// the kNearVanRange boundary flip forever (measured 2026-08-04: wreck 284940, 32 dispatches
        /// in 11 minutes, strictly alternating van/depot, 29 % of the whole session's dispatch
        /// work): the mod assigned a van, the depot branch then claimed the SAME request on the next
        /// pass, the van's order evaporated, VanJob came back empty, and the wreck looked
        /// unassigned again. This remembers our OWN decision, which survives that window and is
        /// what the depot branch has to check before competing with it.</summary>
        public readonly Dictionary<Entity, Entity> LastAssignedVan = new Dictionary<Entity, Entity>();

        /// <summary>Wreck -> frame this dispatcher last handed its request to a tow DEPOT. The same
        /// blind spot as LastAssignedVan, one step further along, and measurably worse: a depot
        /// consumes the request the moment it accepts it and spawns a truck, so
        /// <see cref="TowAssignment.AssignToTowDepot"/>'s "is it still queued here?" test is false
        /// again on the very next pass and the wreck looks unserved. The spawned truck meanwhile
        /// cannot be seen either - VanQuery excludes ParkedCar, so it is invisible while it is
        /// still in the depot and until it has taken the wreck as its Target. Measured 2026-08-09:
        /// wrecks 2371706 and 2371707 were handed to depot 1442392 <b>37 times each in 11.5
        /// minutes</b> - 74 of the session's 81 depot dispatches, one truck spawned per accepted
        /// request, and not one of them ever coupled. That fleet is what jammed the depot exit.</summary>
        public readonly Dictionary<Entity, uint> LastDepotDispatch = new Dictionary<Entity, uint>();

        /// <summary>Shared scratch list for the periodic prunes.</summary>
        public readonly List<Entity> PruneScratch = new List<Entity>();
    }
}

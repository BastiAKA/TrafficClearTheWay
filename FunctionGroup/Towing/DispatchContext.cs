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

        /// <summary>Van -> the ONE wreck it is working, resolved once per pass. See the class
        /// summary: this is what stopped the dispatch thrash.</summary>
        public readonly Dictionary<Entity, Entity> VanJob = new Dictionary<Entity, Entity>();

        /// <summary>Shared scratch list for the periodic prunes.</summary>
        public readonly List<Entity> PruneScratch = new List<Entity>();
    }
}

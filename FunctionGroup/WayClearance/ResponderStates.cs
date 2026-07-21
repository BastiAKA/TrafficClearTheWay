using System.Collections.Generic;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// The per-vehicle scratch state that genuinely IS shared between passes, kept in one
    /// place so the sharing is visible instead of implied.
    ///
    /// Two things live here:
    ///  - <see cref="Stuck"/>: how long each responder has been blocked, by what, and every
    ///    escalation latch (squeeze, oncoming commit, corridor hug, arrival stop). Both the
    ///    emergency chain and the recovery escalation read and write it - a squeeze latched by
    ///    one must be seen by the other, or the maneuver restarts every tick and the responder
    ///    wobbles.
    ///  - <see cref="ForcedChanges"/>: lane changes we asked the game for, with the frame they
    ///    were issued, so they can be timed out rather than re-issued forever.
    ///
    /// Deliberately a plain store with no logic beyond expiry: the behaviour belongs in the
    /// passes, and hiding these behind accessors would only obscure that they are shared.
    /// </summary>
    internal sealed class ResponderStates
    {
        private readonly EntityManager EntityManager;
        private readonly List<Entity> pruneEntitis = new List<Entity>();

        /// <summary>Per-responder blocking history and escalation latches.</summary>
        public readonly Dictionary<Entity, StuckState> Stuck = new Dictionary<Entity, StuckState>();

        /// <summary>Vehicle -> frame a lane change was forced, for timing it out.</summary>
        public readonly Dictionary<Entity, uint> ForcedChanges = new Dictionary<Entity, uint>();

        public ResponderStates(EntityManager entityManager)
        {
            EntityManager = entityManager;
        }

        /// <summary>Forgets vehicles that have not been seen for a while. Called on a slow
        /// cadence; the thresholds are generous because dropping a latch early restarts a
        /// maneuver mid-flight.</summary>
        public void Prune(uint frame)
        {

            if (Stuck.Count != 0)
            {
                pruneEntitis.Clear();
                foreach (KeyValuePair<Entity, StuckState> entry in Stuck)
                {
                    if (frame - entry.Value.m_LastSeenFrame > 1024u)
                    {
                        pruneEntitis.Add(entry.Key);
                    }
                }
                for (int i = 0; i < pruneEntitis.Count; i++)
                {
                    Stuck.Remove(pruneEntitis[i]);
                }

                pruneEntitis.Clear();
            }


            if (ForcedChanges.Count != 0)
            {
                pruneEntitis.Clear();
                foreach (KeyValuePair<Entity, uint> entry in ForcedChanges)
                {
                    if (frame - entry.Value > 2048u || !EntityManager.Exists(entry.Key))
                    {
                        pruneEntitis.Add(entry.Key);
                    }
                }
                for (int i = 0; i < pruneEntitis.Count; i++)
                {
                    ForcedChanges.Remove(pruneEntitis[i]);
                }
                pruneEntitis.Clear();
            }
        }
    }
}

using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// "Has a recovery vehicle been dispatched to this wreck?" - the one question two very
    /// different passes need the same answer to.
    ///
    /// <see cref="WreckLifetime"/> uses it to pick the wreck's give-up deadline
    /// (kWreckClaimedGiveUpAge vs kWreckGiveUpAge), and the orphan finder's diagnostics need it to
    /// say WHICH of those two a stranded wreck is being measured against - without it an
    /// [orphanfind] line cannot be read at all, because the two deadlines are three times apart.
    ///
    /// A pure read of game state, no caching: the answer changes the moment a dispatch is accepted
    /// or its handler despawns, and a stale "claimed" would hold a wreck for half an hour.
    /// </summary>
    internal static class RecoveryClaim
    {
        /// <summary>True once a recovery/tow vehicle has been dispatched to this wreck, i.e. its
        /// maintenance request carries a Dispatched handler that still exists.</summary>
        public static bool IsClaimed(EntityManager entityManager, Entity wreck)
        {
            if (!Lookups.TryGetComponent(wreck, out Game.Simulation.MaintenanceConsumer consumer))
            {
                return false;
            }
            Entity request = consumer.m_Request;
            if (request == Entity.Null || !entityManager.Exists(request) ||
                !Lookups.TryGetComponent(request, out Game.Simulation.Dispatched requestDispatched))
            {
                return false;
            }
            Entity handler = requestDispatched.m_Handler;
            return handler != Entity.Null && entityManager.Exists(handler);
        }
    }
}

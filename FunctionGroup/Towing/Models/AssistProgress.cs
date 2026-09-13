using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// Progress watch for a recovery vehicle on a wreck run: since when has it failed to get
    /// meaningfully closer to its wreck, and what was its best distance so far.
    ///
    /// Reset only by real progress, never by a creep in the jam - an instantaneous speed check
    /// was useless here, because in stop-and-go traffic every inch forward reset the timer and
    /// the escalation never fired once.
    /// </summary>
    internal struct AssistProgress
    {
        public uint m_SinceFrame;
        public float m_BestDistance;
        public Unity.Mathematics.float3 m_LastPos;  // last position at which it had physically MOVED - the metric that still works out on the route, where straight-line distance to the wreck says nothing
        public uint m_MovedSinceFrame;              // ... and the frame that happened. 0 = never seen yet.
        public bool m_NoProgressLogged; // detailed "stopped escalating" diagnostic already emitted for this stall (reset when progress resumes). NOT a give-up: the vehicle goes on driving and often couples afterwards - only the traffic-churning escalation stops.
        public uint m_LastUnblockFrame; // last time the blocker in front of it was removed (rate limit for the last resort)
        public Entity m_SoftFlaggedHead; // the rig we flagged Obsolete (soft, gentle) but have not yet hard-deleted; Entity.Null when none is pending
        public uint m_SoftFlagFrame;    // frame that soft flag was set (0 = none) - after kSacrificeShieldWindow with the SAME rig still blocking, escalate to the hard removal
        public Entity m_BlockerCandidate; // the rig currently being timed for identity stability before it may be flagged
        public uint m_BlockerSince;       // ... and since when it has held that position. See TowStuckRecovery's sibling problem: with two cars standing abreast the game's Blocker flips between them every few seconds (IgnoreBlocker is cleared on every change), so without this the unblock flags whichever one happened to be current.
    }
}

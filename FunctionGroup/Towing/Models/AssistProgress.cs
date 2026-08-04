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
        public bool m_GaveUpLogged; // detailed give-up diagnostic already emitted for this stall (reset when progress resumes)
        public uint m_LastUnblockFrame; // last time the blocker in front of it was removed (rate limit for the last resort)
        public Entity m_SoftFlaggedHead; // the rig we flagged Obsolete (soft, gentle) but have not yet hard-deleted; Entity.Null when none is pending
        public uint m_SoftFlagFrame;    // frame that soft flag was set (0 = none) - after kSacrificeShieldWindow with the SAME rig still blocking, escalate to the hard removal
    }
}

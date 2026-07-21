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
    }
}

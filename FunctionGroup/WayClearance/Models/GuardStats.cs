namespace ClearTheWay
{
    /// <summary>
    /// Heartbeat counters for one tick of <see cref="PathEndGuardSystem"/>. Purely diagnostic:
    /// the enriched version of this line is what reframed the accident-despawn investigation
    /// (it showed 89 of 92 "near-wreck" cars were themselves crash victims, and that
    /// EndOfPath never fired at all in real scenes).
    /// </summary>
    internal struct GuardStats
    {
        public int guarded, nNear, nEndReached, nFailedStuck, nInvolved, nNoTarget, nPending;
        public uint laneOr, pathOr;
    }
}

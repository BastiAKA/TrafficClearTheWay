namespace ClearTheWay
{
    /// <summary>
    /// Counters the wreck-lifetime stage accumulates across one tick's wrecks, for the
    /// <c>[accident]</c> heartbeat.
    ///
    /// A struct with named fields rather than several <c>ref int</c> parameters: four of these are
    /// plain ints, so a transposed pair would compile silently and only ever show up as a
    /// confusing diagnostic line much later.
    /// </summary>
    internal struct WreckSweepStats
    {
        /// <summary>True age of the oldest wreck seen this tick (frames since it first crashed).</summary>
        public uint m_MaxWreckAge;
        /// <summary>How many wrecks are totaled rather than merely damaged.</summary>
        public int m_Destroyed;
        /// <summary>How many had their delete deadline pushed forward to wait for recovery.</summary>
        public int m_Extended;
        /// <summary>How many blocked lanes were marked secured, stopping the repath broadcast.</summary>
        public int m_SecuredLanes;
        /// <summary>How many still-jiggling wrecks had their velocity zeroed.</summary>
        public int m_Frozen;
    }
}

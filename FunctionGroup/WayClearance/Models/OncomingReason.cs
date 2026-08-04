namespace ClearTheWay
{
    /// <summary>
    /// WHY a responder is or is not using the oncoming carriageway this tick - logged as
    /// <c>oncWhy=</c> beside <c>onc=</c>.
    ///
    /// It exists because <c>onc=0</c> on its own is unreadable: it is the same value whether the
    /// escalation never asked, whether the road has no oncoming lane within reach, or whether the
    /// lane is there but too busy to commit to. Those three call for three completely different
    /// changes (nothing, the offset window, the commit gap), and picking between them from
    /// <c>onc=0</c> alone is guesswork - field case veh 2350571, which never once crossed over and
    /// gave no clue why.
    /// </summary>
    internal enum OncomingReason : byte
    {
        /// <summary>The escalation did not even ask - not evading hard enough, mid lane change, at
        /// the dispatch site, or the setting is off.</summary>
        NotAsked = 0,

        /// <summary>No opposite-direction lane within the offset window (kOncomingMinOffset ..
        /// kOncomingMaxOffset). Read <c>oncOff=</c> next to it: that is the nearest oncoming lane
        /// actually found, so a value just above the cap means the WINDOW is the limit, and a
        /// value of -1 means there is genuinely no oncoming carriageway here.</summary>
        NoLane = 1,

        /// <summary>The lane is there but a vehicle is coming: the gap ahead was below the commit
        /// sight (kOncomingCommitSight, or kOncomingDesperateCommitSight while desperate).</summary>
        Gap = 2,

        /// <summary>A pass stalled here recently and new commits are blocked for a while
        /// (kOncomingRetryBlockFrames), so the ordinary machinery gets a turn.</summary>
        Blocked = 3,

        /// <summary>Out there now, or merging back off it.</summary>
        Active = 4
    }
}

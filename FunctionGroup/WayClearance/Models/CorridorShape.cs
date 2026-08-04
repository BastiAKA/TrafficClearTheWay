namespace ClearTheWay
{
    /// <summary>
    /// WHICH corridor a responder is running. One value, decided in one place
    /// (<see cref="ClearTheWay.FunctionGroup.WayClearance.ChannelPlanner.ChooseShape"/>) and held
    /// in <see cref="StuckState.m_Shape"/> for as long as the corridor is actually being
    /// maintained.
    ///
    /// Before this existed the shape was implied by three independent per-tick tests scattered
    /// over the corridor build and the steering stage - a speed threshold, a free-lane scan and a
    /// lane count. They could disagree, and at the speed threshold they flipped from tick to tick:
    /// the traffic around the responder got opposite push directions in consecutive ticks and its
    /// own hug target jumped between the middle seam and the corridor edge.
    ///
    /// Stored as a byte in StuckState (that struct lives in a Dictionary, not in a save game, so
    /// the width is about cache lines, not serialisation).
    /// </summary>
    internal enum CorridorShape : byte
    {
        /// <summary>Nothing decided yet for this responder.</summary>
        None = 0,

        /// <summary>The classic single seam: the responder's own lane clears to the corridor side,
        /// the neighbour on the other side clears away, and the gap opens between them. The
        /// fallback shape - used on narrow roads, on roundabout rings, and whenever the responder
        /// is moving too fast for the wide-road shapes to be worth it.</summary>
        Classic = 1,

        /// <summary>The central channel for wide carriageways: the road parts around a seam in the
        /// middle, every lane clearing outward, the offset growing toward the edges.</summary>
        Channel = 2,

        /// <summary>A same-direction lane is simply EMPTY ahead and the responder is heading for
        /// it. The corridor then parts AWAY from that lane instead of into it.</summary>
        FreeLane = 3
    }
}

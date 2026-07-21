using System.Collections.Generic;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// Cached central-channel plan for one carriageway (segment owner + direction): the lanes
    /// ordered physical left-to-right, which side each clears to, and how far.
    ///
    /// The plan is segment-invariant - it only changes when the road is rebuilt - so it is
    /// computed once and reused across every responder and across ticks (a TTL refresh covers
    /// road edits). Without this cache the full SubLane scan ran per responder per tick and
    /// cost ~5 ms/tick at ~170 sirens.
    /// </summary>
    internal sealed class ChannelPlan
    {
        public uint m_Frame;
        public bool m_IsChannel;
        public int m_Seam;
        public readonly List<Entity> m_Lanes = new List<Entity>(8);      // physical left->right
        public readonly List<float> m_PushDir = new List<float>(8);
        public readonly List<float> m_PushMeters = new List<float>(8);
    }
}

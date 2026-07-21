using Unity.Entities;
using Unity.Mathematics;

namespace ClearTheWay
{
    /// <summary>
    /// Tombstone diagnostics: last-known state of a car near a wreck, so the moment one
    /// vanishes we can log WHAT it was and HOW it went (Deleted / Unspawned / gone).
    ///
    /// This is what proved the queue "despawn" was neither a collision chain nor a path-end
    /// delete, but vanilla's traffic evaporation for unroutable trips. The hunt is over; the
    /// forensics stay behind a compile-time switch so they can be re-armed for the next one.
    /// </summary>
    internal struct CarSnapshot
    {
        public uint m_Frame;
        public float3 m_Pos;
        public float m_Speed;
        public uint m_LaneFlags;
        public uint m_PathState;
        public bool m_Involved;
        public bool m_Dummy;
        public char m_Type;
        public Entity m_Target;
        public bool m_TargetExists;
        public char m_TargetType;
        public float3 m_TargetPos;
    }
}

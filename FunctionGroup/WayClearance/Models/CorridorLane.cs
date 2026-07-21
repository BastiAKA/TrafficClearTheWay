using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// One lane taking part in the emergency corridor around a responder, together with the
    /// direction and distance the cars on it should clear to. Built fresh each tick by the
    /// corridor pass and then walked by the traffic-shaping pass.
    /// </summary>
    internal struct CorridorLane
    {
        public Entity m_Lane;
        public float m_MinPos;
        public float m_PushDirection;
        public float m_StartOffset; // meters from the emergency vehicle to m_MinPos on this lane
        public bool m_Inverted;
        public bool m_OnPath;
        public float m_PushMeters;  // per-lane target offset (m); 0 => use the default kEdgeMeters. The central-channel mode sets a graduated value so outer lanes clear further and the middle seam opens even in a packed jam.
    }
}

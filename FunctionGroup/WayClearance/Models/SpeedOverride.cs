using Unity.Mathematics;

namespace ClearTheWay
{
    /// <summary>
    /// One pending speed/steering override for a vehicle, recorded by the pre-navigation pass
    /// and applied by <see cref="ClearTheWayPostSystem"/> after CarNavigationSystem has run.
    ///
    /// It has to be a two-stage handover: CarNavigationSystem recomputes m_MaxSpeed and
    /// m_TargetPosition from scratch for the ~1/16 of vehicles it processes each tick, so
    /// anything written before it is silently lost on exactly the tick a vehicle is simulated.
    /// </summary>
    public struct SpeedOverride
    {
        public float m_Speed;       // target speed cap/floor (m/s)
        public byte m_Mode;         // 0 = ceiling (hold down), 1 = floor (grant up)
        public bool m_HasTarget;    // steer toward m_Target (nose-out)
        public float3 m_Target;
        public quaternion m_Rotation;
    }
}

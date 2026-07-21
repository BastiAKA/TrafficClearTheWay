namespace ClearTheWay
{
    /// <summary>
    /// Per-car push-direction claim. When several responders run close together (a convoy to
    /// the same accident) their corridors overlap: the same car can sit on responder A's own
    /// lane (pushed +side) AND on responder B's counter-evade lane (pushed -side). Without a
    /// claim the car is lerped left and right in alternating passes of the SAME tick - it
    /// jitters in the middle of the road and visibly twists against its push direction
    /// ("left-pushed cars turn right"). The first direction wins and stays claimed while it
    /// keeps being pushed; a conflicting push skips the car entirely (the claiming responder's
    /// pass keeps handling it).
    /// </summary>
    internal struct PushClaim
    {
        public float m_Dir;
        public uint m_Frame;
    }
}

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
        /// <summary>The lateral offset (in lane-position units, unsigned) this car was last told
        /// to reach. HoldVehicles needs it to tell "has made room" from "has barely twitched":
        /// a car only realises its offset by DRIVING, so it may be slowed once it is nearly
        /// there - never while it still has most of the way to go.</summary>
        public float m_TargetUnits;
    }
}

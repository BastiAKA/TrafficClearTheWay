using Unity.Entities;
using Unity.Mathematics;

namespace ClearTheWay
{
    /// <summary>
    /// Per-responder scratch state carried across ticks: what is blocking it, how long it has
    /// been blocked, and the latches that keep each escalation stage (squeeze, overtake,
    /// oncoming pass, corridor hug, arrival stop) committed instead of flickering on and off
    /// with a noisy per-tick signal. Most of the mod's historic wobble bugs were a missing
    /// latch here.
    /// </summary>
    internal struct StuckState
    {
        public Entity m_Blocker;
        public uint m_BlockerSinceFrame;
        public uint m_SqueezeUntilFrame;
        public uint m_LastSeenFrame;
        public uint m_NextOvertakeFrame;
        public uint m_OncomingActiveUntil;
        public uint m_MergeWantSince;
        public Entity m_NearTargetEntity;   // dispatch target the near-counter refers to
        public uint m_NearTargetFrames;     // cumulative frames spent within kArriveAssistRange of it without arriving
        public float m_ArriveBestDist;      // closest the vehicle has ever gotten to that target
        public uint m_ArriveStallSince;     // frame its approach last improved (0 = not tracking yet)
        public uint m_ArriveCommitFrame;    // frame the kerb-stop was committed (0 = not committed); latches the maneuver so rolling off a junction lane cannot abort it
        public uint m_LastDefreezeFrame;    // frame we last flagged the lane Obsolete to recover a stuck-mid-change freeze (0 = never); throttles the recompute
        public float m_FreeLaneDir;         // latched free-lane choice (+1/-1 physical, 0 = none). Without the latch the choice flickers: "free" was a knife-edge test for ZERO vehicles ahead, so a single car entering the lane flipped it, the hug direction flipped with it, and the responder swung side to side instead of committing to either the free lane or the corridor.
        public uint m_FreeLaneUntilFrame;   // ...held until this frame, refreshed while the lane stays usable
        public uint m_HugUntilFrame;        // corridor hug latch: engaged until this frame (refreshed while cars are actually pushed) so the hug holds a steady line instead of flickering with the push count
        public float3 m_OncSamplePos;       // along-lane position at the last oncoming progress sample (lateral swinging does not move it)
        public uint m_OncSampleFrame;       // frame of that sample (0 = not tracking)
        public uint m_OncBlockUntil;        // no new oncoming commit until this frame - set after a stalled pass was forced to merge back
        public byte m_Shape;                // committed CorridorShape: WHICH corridor this responder is running. One decision instead of three per-tick tests that could disagree - see CorridorShape.
        public uint m_ShapeUntilFrame;      // ...held until this frame. REFRESHED every tick cars are actually pushed for the corridor, so a formed Rettungsgasse is never re-planned underneath the responder; it lapses a few ticks after the corridor stops being maintained (or at once when the responder turns desperate).
        public uint m_ColleagueUntilFrame;  // convoy latch: "a colleague is ahead of me" held until this frame. The raw 20 m test flips as the colleague pulls away and closes up again, and with it the whole evade stage and the hug width - the same flicker the hug latch was added for.
        public float3 m_ProgressPos;        // where the current blocked spell started. The spell ends on real GROUND COVERED (kStuckProgressMeters), not on a moment of speed: in stop-and-go a creep over the old 5 m/s gate reset every escalation clock, so hard evade / desperate / the light rule almost never matured. Same lesson as kAssistProgressMeters on the recovery side.
    }
}

using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// The working state of ONE responder for ONE tick of the escalation chain.
    ///
    /// The chain is a sequence of stages that each read what the earlier ones decided - whether
    /// the vehicle is stuck, whether it committed to the oncoming lane, whether a corridor is
    /// actually forming around it - and there are two dozen such values. They used to be locals
    /// in a single 579-line method.
    ///
    /// Passing them as positional arguments instead would be worse than the long method: most of
    /// them are bools, so a transposed pair would compile silently and change behaviour in a way
    /// no test here would catch. Named fields on a struct passed by ref keep every stage reading
    /// exactly as it did when it was one method.
    ///
    /// Field names deliberately match the original local names one-for-one.
    /// </summary>
    internal struct EscalationState
    {
        public Entity m_Vehicle;
        public float m_Side;
        public uint m_Frame;

        /// <summary>Set by any stage that modified the lane component; the write happens once at the end.</summary>
        public bool m_Changed;

        /// <summary>Own speed, computed before the corridor because the corridor shape depends on it.</summary>
        public float m_EmergencySpeed;

        /// <summary>Last tick's latches for this vehicle (blocker, squeeze, oncoming, hug, arrival).</summary>
        public StuckState m_PreviousStuck;

        // --- Gates decided before any action is taken ---
        public bool m_NearArrivalTarget;  // at the dispatch site: most machinery is suppressed so it can stop
        public EvadeStage m_Evade;        // how hard the traffic ahead is being asked to clear - Soft corridor / Hard (onto the kerb) / Deep (fully onto the pavement). See EvadeStage.
        public bool m_Desperate;          // stuck ~10 s: unlocks crossing to the oncoming side
        public bool m_BehindColleague;    // another responder right ahead - queue behind it, do not fan out
        public bool m_CanManeuver;        // lane state permits lateral maneuvers at all

        // --- Oncoming carriageway ---
        public int m_OncomingState;        // 0 none / 1 merging back / 2 fully across
        public float m_OncomingClearAhead; // how far the oncoming lane is clear
        public bool m_FullCrossover;       // == m_OncomingState 2
        public bool m_Merging;             // == m_OncomingState 1

        // --- Escalations for a responder that is going nowhere ---
        public bool m_DrainAhead;      // let the queue ahead drive off rather than freezing it
        public bool m_LightOverride;   // sat at a signalised junction ~3 min: treat the light as broken

        // --- Corridor result ---
        public int m_Pushed;             // how many cars this tick's corridor actually moved
        public bool m_CorridorWorking;   // == m_Pushed > 0; suppresses swerving out of a working corridor

        // --- Turns and lane changes ---
        public int m_TurnHint;           // +1 right / -1 left turn required by the route, 0 none
        public float m_TurnDistance;
        public bool m_ApproachingTurn;
        public bool m_TurnLean;          // lean toward the turn side (only when no corridor is working)

        // --- Stuck escapes ---
        public bool m_EvadeSideBlocked;  // the side the corridor rule sends it to is walled off
        public bool m_StuckEscape;       // squeeze went nowhere, never started, or the side is blocked

        // --- Results of the steering and speed stages ---
        public bool m_LateralSteered;    // some stage wrote m_LanePosition, so do not drift back to centre
        public bool m_Squeezing;         // IgnoreBlocker is set
    }
}

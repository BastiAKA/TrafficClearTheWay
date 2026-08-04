using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;

namespace ClearTheWay
{
    /// <summary>
    /// The LATERAL half of the escalation: where the responder puts itself across the road.
    ///
    /// In order: decide whether the route requires a turn, take a whole lane if going around is
    /// the only way out, open a slot on the target lane so the change can finish in packed
    /// traffic, then steer - hug the corridor seam, lean toward a turn, or (on a roundabout,
    /// where the rule inverts) keep the outer edge.
    ///
    /// The governing rule: while a corridor is actually WORKING around the vehicle it never
    /// swerves out of it. Doing so tears up the very gap the queue just opened. Only a turn the
    /// route requires, or a genuine stuck escape, moves it out of lane.
    /// </summary>
    internal sealed class EscalationSteering
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private VehicleControl m_Control => m_Ctx.VehicleControl;
        private CorridorBuilder m_Corridor => m_Ctx.Corridor;
        private Dictionary<Entity, StuckState> m_StuckStates => m_Ctx.States.Stuck;
        private Dictionary<Entity, uint> m_ForcedChanges => m_Ctx.States.ForcedChanges;

        public EscalationSteering(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Runs the lateral stages for one responder this tick.</summary>
        public void Apply(ref EscalationState s, ref CarCurrentLane currentLane, Setting setting, Entity vehicle, float side, uint frame)
        {
            // Change lane only when it is worth it: in slow traffic, onto a same-direction
            // lane that is SIGNIFICANTLY freer than the current one (or that the vehicle's
            // path actually requires). Not while passing on the oncoming carriageway.
            // NOT while a corridor is actually forming around us either: swerving out of a
            // working Rettungsgasse tears up the very gap the queue just opened, and the
            // cars of the new lane then have to start over. Stay in lane and let the
            // corridor + squeeze do the work; only a turn the route REQUIRES still moves
            // the vehicle over (it has to reach its turn lane by the next intersection).
            // Only slow vehicles ever act on the turn hint (both the overtake gate and the
            // turn lean require crawling speed), so skip the nav-buffer walk entirely for the
            // many responders cruising at speed - with ~150 concurrent sirens this ran every
            // tick for every one of them.
            s.m_TurnHint = 0;
            s.m_TurnDistance = 0f;
            if (s.m_EmergencySpeed < kOvertakeSlowSpeed)
            {
                s.m_TurnHint = m_Ctx.LaneChange.GetUpcomingTurn(vehicle, out s.m_TurnDistance);
            }
            s.m_CorridorWorking = s.m_Pushed > 0;
            // Piece C - close to the turn the responder wants: do NOT force a lane change onto
            // the turn lane (a forced m_ChangeLane there can violate the junction's lane
            // connections and jam mid-change - exactly the freeze Sebastian saw). Instead we
            // half-merge laterally toward the turn side (below) and let the game's own
            // navigation make the real turn-lane change once there is room. Approaching only
            // while slow and not already mid-change / at the arrival site.
            s.m_ApproachingTurn = s.m_TurnHint != 0 && s.m_TurnDistance <= kTurnApproachDist &&
                s.m_EmergencySpeed < kOvertakeSlowSpeed && !s.m_NearArrivalTarget && s.m_CanManeuver &&
                currentLane.m_ChangeLane == Entity.Null;
            // While the corridor IS working the responder stays in the middle gap all the way
            // to the front and turns from there (the classic Rettungsgasse picture) - the turn
            // lean below only takes over when no corridor is forming around it.
            s.m_TurnLean = s.m_ApproachingTurn && !s.m_CorridorWorking;
            // The German corridor rule sends the emergency vehicle to the LEFT (-side). When
            // there is no drivable same-direction lane on that side - a tram median, a wall,
            // or the leftmost lane against the kerb - hugging further left cannot help a
            // vehicle that is already stuck bumper-to-bumper. NOTE this is an ESCAPE test,
            // not a steering mode: being on the leftmost lane next to the median/oncoming is
            // the NORMAL corridor situation (the gap opens along the median). Evaluated only
            // once the vehicle is genuinely stuck (desperate, ~10s behind the same blocker,
            // standing) - checking it every tick misfired on a third of all responders,
            // suppressed the corridor hug city-wide and let them lane-change through working
            // corridors (the "drives inside other cars" bug). Also saves the SubLane scan on
            // every cruising responder.
            s.m_EvadeSideBlocked = s.m_Desperate && s.m_EmergencySpeed < 0.5f && s.m_OncomingState == 0 &&
                m_Ctx.Obstruction.IsEvadeSideBlocked(vehicle, currentLane, side);
            // A wide, non-aside blocker (typically a truck / articulated rig) cannot be
            // squeezed past at the edge: the separation gate nominally passes at ~1 m but two
            // wide bodies physically overlap, so the responder sits at lanePos +-3 with
            // ignore=1 and speed 0 for minutes. When a latched squeeze against the same blocker
            // has gone nowhere for a while, stop squeezing and OVERTAKE AROUND it instead -
            // move onto a middle/neighbour lane so traffic parts left and right. Only starts
            // a lane change through the game's own TryOvertakeLaneChange (safe - it merely
            // SETS m_ChangeLane).
            bool squeezeFutile = frame < s.m_PreviousStuck.m_SqueezeUntilFrame &&
                s.m_EmergencySpeed < 0.5f && s.m_PreviousStuck.m_Blocker != Entity.Null &&
                frame - s.m_PreviousStuck.m_BlockerSinceFrame >= kSqueezeGiveUpFrames;
            // ... but a squeeze may NEVER have started in the first place: behind a stopped
            // articulated rig (its trailer would close the gap, so PullCarsAside deliberately
            // leaves it in lane) or in a lane too tight for the separation gate, IgnoreBlocker
            // is never set, so squeezeFutile - which requires an ACTIVE squeeze latch - stays
            // false forever. The responder then neither squeezed nor overtook and just sat
            // there (the "overtake too rarely when slow" case). Treat "slow behind a blocker
            // for kSqueezeGiveUpFrames with no squeeze running" as an escape in its own right.
            bool boxedIn = s.m_EmergencySpeed < 0.5f && s.m_PreviousStuck.m_Blocker != Entity.Null &&
                frame - s.m_PreviousStuck.m_BlockerSinceFrame >= kSqueezeGiveUpFrames &&
                (currentLane.m_LaneFlags & CarLaneFlags.IgnoreBlocker) == 0;
            // Stuck-escape reasons override the corridor-working suppression AND the
            // approaching-turn lean: when the responder is genuinely stuck (squeeze went
            // nowhere, could never start, or the evade side is walled off) going AROUND is the
            // only way out, even right at the turn. Without this, Piece C's lean would freeze
            // it at the junction (observed: a vehicle stuck at lanePos +2 squeezing for 1.5 min
            // next to a turn).
            s.m_StuckEscape = squeezeFutile || s.m_EvadeSideBlocked || boxedIn;
            // While a corridor is actually working around the vehicle it NEVER swerves out of
            // it (that tears up the very gap the queue just opened) - it rides the middle gap
            // to the front and turns from there. Overtaking is for stretches where no corridor
            // is forming, plus the genuine stuck escapes above. If the vehicle truly starves
            // in a wrong lane at the front, the blocker latch -> squeeze -> squeezeFutile
            // chain opens this gate after ~10s anyway.
            // An EMPTY lane on this carriageway is the one case that overrides the
            // corridor-working suppression on its own: there is nothing to tear up over there,
            // and threading a seam through the queue while free asphalt sits one lane over is
            // exactly the "sneaking into the middle" the corridor build now refuses to plan.
            float freeLaneDir = m_Corridor.FreeLaneDir;
            if (!s.m_FullCrossover && !s.m_NearArrivalTarget && setting.OvertakeStuckTraffic &&
                ((!s.m_ApproachingTurn && !s.m_CorridorWorking) || s.m_StuckEscape || freeLaneDir != 0f) &&
                s.m_EmergencySpeed < kOvertakeSlowSpeed &&
                frame >= s.m_PreviousStuck.m_NextOvertakeFrame &&
                !m_ForcedChanges.ContainsKey(vehicle) &&
                m_Ctx.LaneChange.TryOvertakeLaneChange(vehicle, ref currentLane, frame, s.m_TurnHint,
                    relaxDensity: s.m_StuckEscape || freeLaneDir != 0f, preferDir: freeLaneDir))
            {
                // Into the state copy this pass already carries, NOT into a fresh read: the hug
                // latch a few lines below writes s.m_PreviousStuck back, and with a separate copy
                // that write restored the OLD m_NextOvertakeFrame. A responder that had just
                // forced a lane change was therefore free to force another on the next tick
                // whenever its corridor was pushing - which is most of the time it overtakes at
                // all, so the cooldown that is supposed to keep the maneuver committed was
                // effectively absent exactly when it mattered.
                s.m_PreviousStuck.m_NextOvertakeFrame = frame + kOvertakeCooldown;
                s.m_PreviousStuck.m_LastSeenFrame = frame;
                m_StuckStates[vehicle] = s.m_PreviousStuck;
                s.m_Changed = true;
            }

            // A forced lane change in progress (this tick or a previous one): actively open a
            // longitudinal slot on the target lane so the takeover actually completes in packed
            // traffic instead of stalling half-way.
            if (currentLane.m_ChangeLane != Entity.Null && m_ForcedChanges.ContainsKey(vehicle) &&
                EntityManager.HasComponent<Game.Net.CarLane>(currentLane.m_ChangeLane))
            {
                bool changeInverted = currentLane.m_CurvePosition.z < currentLane.m_CurvePosition.x;
                m_Ctx.Hold.OpenLaneChangeSlot(vehicle, currentLane.m_ChangeLane, currentLane.m_CurvePosition.x, changeInverted);
            }

            // Hug the left edge while a corridor is forming - but only when we did NOT use
            // the oncoming lane (that already set the lateral position), and not at the
            // site itself (there it just stops in place). This IS the middle gap: own-lane
            // cars part right, the left neighbours part left, and the responder threads the
            // seam - all the way to the front, even while a turn is coming up (turnLean only
            // replaces the hug when no corridor is working). Skipped only in the rare
            // genuinely-stuck-against-a-wall escape (evadeSideBlocked): steering further left
            // there just plants the vehicle on the median.
            // Hug hysteresis: `pushed` naturally flickers 0<->1 as distant corridor cars enter
            // and leave the window, and gating the hug on it directly made the vehicle lurch a
            // metre left and snap back to centre in alternating seconds at 30+ m/s (logged:
            // lanePos 0 -> -1.65 -> 0 -> -1.48 -> 0 while cruising). The latch keeps the hug
            // engaged for ~2s after the last actual push so it holds a steady line.
            if (s.m_Pushed > 0 && s.m_OncomingState == 0)
            {
                s.m_PreviousStuck.m_HugUntilFrame = frame + kHugHoldFrames;
                m_StuckStates[vehicle] = s.m_PreviousStuck;
            }
            bool hugActive = s.m_Pushed > 0 || s.m_Evade >= EvadeStage.Hard || frame < s.m_PreviousStuck.m_HugUntilFrame;
            s.m_LateralSteered = s.m_OncomingState > 0;
            if (s.m_OncomingState == 0 && !s.m_EvadeSideBlocked && !s.m_TurnLean && hugActive && s.m_CanManeuver && !s.m_NearArrivalTarget)
            {
                // While actually PASSING a blocker (squeeze latched or IgnoreBlocker set),
                // swing out wider than the normal evade line: the vehicle visibly arcs around
                // the car it overtakes instead of scraping along it, and eases back onto the
                // corridor line once the latch expires.
                bool passingBlocker = frame < s.m_PreviousStuck.m_SqueezeUntilFrame ||
                    (currentLane.m_LaneFlags & CarLaneFlags.IgnoreBlocker) != 0;
                // Hug direction first - the crossable-room lookup below needs to know which side
                // it is asking about. On a wide road running the central channel, hug toward the
                // shared middle seam (m_Corridor.ChannelHugDir) instead of the corridor-side edge
                // (-side), so every responder on the segment threads the same gap.
                float hugDir = m_Corridor.ChannelHugDir != 0f ? m_Corridor.ChannelHugDir : -side;
                float meters = passingBlocker ? kPassEvadeMeters : (s.m_Evade >= EvadeStage.Hard ? kEvadeMeters : kEdgeMeters);
                // A tram bed or green strip beside the lane is room the RESPONDER may use too, not
                // just the traffic it pushes. Only while evading or actually passing - one rolling
                // through a corridor that already parted keeps its lane.
                if (s.m_Evade >= EvadeStage.Hard || passingBlocker)
                {
                    meters += m_Ctx.Room.CrossableMeters(currentLane.m_Lane, hugDir, frame);
                }
                if (s.m_BehindColleague)
                {
                    // Queue behind the colleague on the normal corridor line - no wide swing.
                    meters = math.min(meters, kEdgeMeters);
                }
                // Speed taper: at crawling speed the vehicle needs the full middle-gap line,
                // but at cruising speed the corridor ahead has seconds to part - it only needs
                // to SHADE left, not ride the median at 120 km/h.
                meters *= math.clamp(1f - (s.m_EmergencySpeed - kHugTaperStartSpeed) / kHugTaperRange, kHugMinScale, 1f);
                meters = math.min(meters, m_Ctx.PrefabGeometry.MaxLateralMeters(vehicle));
                float units = meters / m_Ctx.PrefabGeometry.LateralSlack(vehicle, currentLane.m_Lane);
                float target = hugDir * units;
                float newPos = math.lerp(currentLane.m_LanePosition, target, s.m_Evade >= EvadeStage.Hard ? kPullRate * 1.5f : kPullRate);
                s.m_LateralSteered = true;
                if (math.abs(newPos - currentLane.m_LanePosition) > 0.001f)
                {
                    currentLane.m_LanePosition = newPos;
                    s.m_Changed = true;
                }
            }

            // Piece C - half-merge toward the turn, but ONLY when no corridor is working
            // (turnLean): in a working corridor the responder rides the gap to the front and
            // turns from there instead. Without a corridor, LEAN it partially toward the turn
            // side while it approaches the junction: turnHint +1 = right turn = +lanePosition
            // (right of travel), -1 = left. A PARTIAL offset (kEdgeMeters, not the full evade)
            // so it stays half in its lane and does not clip the kerb ("so halb einscheren") -
            // it edges toward the turn lane and the squeeze below pushes past the car in the
            // way, then the game's own navigation performs the actual connection-respecting
            // turn change.
            if (s.m_TurnLean && s.m_OncomingState == 0)
            {
                float turnUnits = math.min(kEdgeMeters, m_Ctx.PrefabGeometry.MaxLateralMeters(vehicle)) / m_Ctx.PrefabGeometry.LateralSlack(vehicle, currentLane.m_Lane);
                float turnTarget = s.m_TurnHint * turnUnits;
                float turnNewPos = math.lerp(currentLane.m_LanePosition, turnTarget, kPullRate);
                s.m_LateralSteered = true;
                if (math.abs(turnNewPos - currentLane.m_LanePosition) > 0.001f)
                {
                    currentLane.m_LanePosition = turnNewPos;
                    s.m_Changed = true;
                }
            }

            // Roundabout mirror image: the ring cars are swept toward the centre, so the
            // responder keeps the outer edge instead of the corridor seam (RoundaboutExtensions).
            m_Ctx.Roundabout.HugOuterEdge(ref s, ref currentLane, setting, vehicle, side);
        }
    }
}

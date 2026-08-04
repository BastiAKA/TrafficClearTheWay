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
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding;
// Game.Net has a LaneGeometry of its own - ours wins here, like the CarLaneFlags alias above.

namespace ClearTheWay
{
    /// <summary>
    /// The LONGITUDINAL half of the escalation: squeezing past, and how fast the responder and
    /// the traffic around it may go.
    ///
    /// Two rules that cost real debugging:
    ///  - At the dispatch site IgnoreBlocker is actively DROPPED. The game clamps speed to at
    ///    least 3 m/s toward an ignored blocker, so a squeezing vehicle physically cannot come to
    ///    rest - it orbited its target for minutes instead of arriving.
    ///  - No speed floor is granted near the target either: vanilla navigation is braking into
    ///    the path end there, and a floor pushes the vehicle straight past it.
    ///
    /// It also ends the tick: returning m_LanePosition to centre when nothing is displacing the
    /// vehicle any more. That was missing entirely once - the offset simply froze at whatever a
    /// finished maneuver left behind, parking the vehicle on the oncoming carriageway while its
    /// lane was still the original one, and it crawled in circles against an invisible wall.
    /// </summary>
    internal sealed class EscalationSpeed
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private VehicleControl m_Control => m_Ctx.VehicleControl;
        private M_LaneGeometry m_Geometry => m_Ctx.Geometry;
        private CorridorBuilder m_Corridor => m_Ctx.Corridor;
        private Dictionary<Entity, StuckState> m_StuckStates => m_Ctx.States.Stuck;
        private Dictionary<Entity, uint> m_ForcedChanges => m_Ctx.States.ForcedChanges;

        public EscalationSpeed(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Runs the squeeze and speed stages, then writes the lane component.</summary>
        public void Apply(ref EscalationState s, ref CarCurrentLane currentLane, Setting setting, Entity vehicle, float side, uint frame)
        {
            if (s.m_NearArrivalTarget)
            {
                // At the site the vehicle must be able to BRAKE TO A STOP and do its job.
                // IgnoreBlocker is the one flag that actively prevents that - the game clamps
                // the speed to >=3 m/s toward an ignored blocker, so a squeezing vehicle
                // physically cannot come to rest. Never start a squeeze here and drop an
                // active one.
                if ((currentLane.m_LaneFlags & CarLaneFlags.IgnoreBlocker) != 0)
                {
                    currentLane.m_LaneFlags &= ~CarLaneFlags.IgnoreBlocker;
                    s.m_Changed = true;
                }
            }
            else
            {
                // Drop a STALE squeeze first: IgnoreBlocker is set but there is nothing left to
                // pass (the blocker moved off / was cleared) and the latch has expired. Left on,
                // the flag sticks forever - only the arrival branch above ever cleared it - and
                // the game floors speed to >=3 m/s toward an ignored blocker, so the responder
                // can never brake or arrive: it orbits its target for minutes, riding the wide
                // pass line at lanePos ~2.8 (observed: veh stuck 25 min, ignore=1 blocker=0).
                bool ignoreSet = (currentLane.m_LaneFlags & CarLaneFlags.IgnoreBlocker) != 0;
                bool noBlocker = !EntityManager.HasComponent<Blocker>(vehicle) ||
                    EntityManager.GetComponentData<Blocker>(vehicle).m_Blocker == Entity.Null;
                if (ignoreSet && noBlocker && frame >= s.m_PreviousStuck.m_SqueezeUntilFrame)
                {
                    currentLane.m_LaneFlags &= ~CarLaneFlags.IgnoreBlocker;
                    s.m_Changed = true;
                }
                else if (setting.SqueezePastBlockers && m_Ctx.Squeeze.TrySqueezePastBlocker(vehicle, ref currentLane, frame))
                {
                    s.m_Changed = true;
                }
            }

            // Speed management for the emergency vehicle itself (applied post-navigation):
            //  - Nose-out: a stopped, evading vehicle that is not yet squeezing gets a
            //    lateral target + creep speed so its written offset becomes real distance
            //    and the separation gate can open. Without this its zero speed budget means
            //    the offset never physically happens.
            //  - Cleared boost: once it is squeezing (IgnoreBlocker) past a blocker with
            //    comfortable lateral clearance, it may move noticeably faster than the 3 m/s
            //    squeeze crawl, so a cleared corridor is actually used at speed.
            s.m_Squeezing = (currentLane.m_LaneFlags & CarLaneFlags.IgnoreBlocker) != 0;
            if (s.m_Squeezing)
            {
                float sep = m_Geometry.GetBlockerSeparation(vehicle, currentLane);
                // No cleared boost near the dispatch target - see the oncoming floor above:
                // let vanilla navigation brake into the path end instead of overshooting it.
                if (sep >= kClearedSeparation && s.m_EmergencySpeed < kClearedSpeed && !s.m_NearArrivalTarget)
                {
                    m_Control.SetFloor(vehicle, kClearedSpeed, hasTarget: false, default, default);
                }
                // Squeezing, but the gap is not there yet: separation sits in the dead band
                // between kMinSqueezeSeparation (squeeze allowed) and kClearedSeparation (boost
                // allowed). That case used to get NOTHING, and it could not get out of it alone.
                // The game's push-past clamps to 3 m/s for exactly ONE entity - the one in
                // Blocker.m_Blocker - and CarNavigationSystem.CheckBlocker drops IgnoreBlocker
                // again the moment the reported blocker changes. With two stopped cars abreast
                // the blocker alternates every tick, so neither is ever really ignored and the
                // responder stands at 0; and since a car realises its written m_LanePosition
                // only by DRIVING, the separation can never grow into the boost band either.
                // Self-locking (logged: veh at (476,1471) for ~2800 frames at sep 1.02/1.44, the
                // blocker flipping between two cars, with nothing but the 3-minute light rule
                // left to free it). So it gets the same nose-out as a responder that has not
                // started squeezing - the creep budget is what turns the offset into distance.
                else
                {
                    s.m_NoseCreep = TryNoseCreep(in s, currentLane, vehicle);
                }
            }
            // Free-ahead advance: nothing blocks the responder and its own lane is clear well
            // ahead (typically the last stretch up to the junction it turns at), yet vanilla
            // navigation coasts and it hangs back short of the stop line - the move it otherwise
            // only made ~10 s later once the desperate escalation opened things up. Grant a
            // moderate forward floor so it rolls right up to the intersection NOW. Gated hard on
            // a genuinely clear path (no Blocker at all + no car within kDensityWindow ahead on
            // its own lane) so the floor never drives it into traffic: any car ahead or any
            // crossing vehicle registers as a Blocker and cancels this, and vanilla still brakes
            // at the actual junction.
            else if (!s.m_NearArrivalTarget && !s.m_FullCrossover && !s.m_Merging &&
                     currentLane.m_ChangeLane == Entity.Null &&
                     s.m_EmergencySpeed < kAdvanceClearSpeed &&
                     EntityManager.GetComponentData<Blocker>(vehicle).m_Blocker == Entity.Null &&
                     EntityManager.HasComponent<Game.Net.CarLane>(currentLane.m_Lane) &&
                     m_Geometry.LaneVehiclesAhead(currentLane.m_Lane, currentLane.m_CurvePosition.x,
                         currentLane.m_CurvePosition.z < currentLane.m_CurvePosition.x) == 0)
            {
                m_Control.SetFloor(vehicle, kAdvanceClearSpeed, hasTarget: false, default, default);
            }
            // Committing to the gap. A lane change INTO the corridor is exactly when a responder
            // needs to keep rolling, and it is the one case every floor above excludes (they all
            // require m_ChangeLane == null). Stalling halfway is the worst outcome: the vehicle
            // sits across two lanes, its lateral offset decays back toward centre, and nothing
            // recovers it until the 15 s defreeze. Gated on real room ahead, so it only ever
            // completes a move into space that is actually there.
            else if (!s.m_NearArrivalTarget && !s.m_FullCrossover &&
                     currentLane.m_ChangeLane != Entity.Null &&
                     s.m_EmergencySpeed < kMergeCommitSpeed &&
                     HasRoomToCommit(vehicle, currentLane))
            {
                m_Control.SetFloor(vehicle, kMergeCommitSpeed, hasTarget: false, default, default);
            }
            else
            {
                s.m_NoseCreep = TryNoseCreep(in s, currentLane, vehicle);
            }

            // #6: while the vehicle is actively passing (forced overtake, squeeze past a
            // blocker, or a full oncoming-lane crossover) hold the traffic it is drawing level
            // with to a gentle ceiling so it does not race the vehicle and can be cleared. Skip
            // while merging back in - those cars ahead are being nudged FORWARD to open a gap.
            bool passing = s.m_Squeezing || s.m_FullCrossover ||
                m_ForcedChanges.ContainsKey(vehicle) || currentLane.m_ChangeLane != Entity.Null;
            if (passing && !s.m_Merging)
            {
                m_Ctx.Hold.SlowPassedTraffic(vehicle);
            }

            // Steer BACK into the lane whenever nothing is displacing us any more. This was
            // missing entirely: every other car we push aside is registered in m_PushedCars
            // and drifts back via ReleasePushedCars, but the emergency vehicle's own offset
            // was only ever written, never undone. The moment a maneuver stopped (oncoming
            // sticky expired, the corridor dissolved, or the vehicle came near its target),
            // the writes simply ceased and m_LanePosition FROZE at whatever it was - up to
            // several lane units out, i.e. physically parked on the oncoming carriageway
            // while its lane is still the original one. Its navigation then steers at a
            // target far off to the side: the vehicle crawls in circles against an invisible
            // wall and never finds its lane again. The game itself only drifts the offset by
            // ~0.002/tick, so it never recovers on its own. Not while squeezing though -
            // there the offset IS the clearance that lets us past the blocker.
            if (!s.m_LateralSteered && !s.m_Squeezing && math.abs(currentLane.m_LanePosition) > 0.01f)
            {
                currentLane.m_LanePosition = math.lerp(currentLane.m_LanePosition, 0f, kReleaseRate * 2f);
                if (math.abs(currentLane.m_LanePosition) < 0.02f)
                {
                    currentLane.m_LanePosition = 0f;
                }
                s.m_Changed = true;
            }

            if (s.m_Changed)
            {
                EntityManager.SetComponentData(vehicle, currentLane);
            }

            if (setting.VerboseLogging && frame % kLogIntervalFrames == (uint)(vehicle.Index % (int)kLogIntervalFrames))
            {
                m_Ctx.Escalation.LogVehicleState(vehicle, currentLane, s.m_Pushed, frame, s.m_Evade, s.m_OncomingState, s.m_EvadeSideBlocked, s.m_DrainAhead, s.m_NoseCreep,
                    s.m_OncReason, s.m_OncNearestOffset);
            }
        }
        /// <summary>
        /// Nose-out for a responder that has come to a stand while evading: a creep budget plus a
        /// lateral target, so its written m_LanePosition becomes actual distance.
        ///
        /// A standing car has no lateral lever at all - the game only realises the offset while
        /// the vehicle moves - so without this the offset stays a number on a component and every
        /// separation gate downstream stays shut. Returns true when the floor was granted.
        /// </summary>
        private bool TryNoseCreep(in EscalationState s, CarCurrentLane currentLane, Entity vehicle)
        {
            if (s.m_Evade < EvadeStage.Hard || s.m_NearArrivalTarget || s.m_EmergencySpeed >= 1f ||
                currentLane.m_ChangeLane != Entity.Null ||
                math.abs(currentLane.m_LanePosition) <= 0.3f ||
                !m_Ctx.LanePosition.TryGetLateralTarget(vehicle, currentLane, 2f, out float3 noseTarget, out quaternion noseRotation))
            {
                return false;
            }
            m_Control.SetFloor(vehicle, kCreepSpeed, hasTarget: true, noseTarget, noseRotation);
            return true;
        }

        /// <summary>
        /// Is there enough space in front to finish a lane change under power? A negative
        /// separation means no blocker at all - the road ahead is open.
        /// </summary>
        private bool HasRoomToCommit(Entity vehicle, CarCurrentLane currentLane)
        {
            float separation = m_Geometry.GetBlockerSeparation(vehicle, currentLane);
            return separation < 0f || separation >= kMergeCommitSeparation;
        }

    }
}

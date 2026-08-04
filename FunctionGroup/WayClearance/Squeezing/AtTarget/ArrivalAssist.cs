using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding;

namespace ClearTheWay.FunctionGroup.WayClearance.Squeezing.AtTarget
{
    /// <summary>
    /// Getting a responder to actually ARRIVE at its dispatch target and stop there.
    ///
    /// Vanilla has a close-arrival fallback, but it only fires on a standing, blocked vehicle -
    /// and our own corridor keeps the lane rolling, so it never triggered. A responder that
    /// cannot reach the exact path end (end lane taken by the wreck or its queue) then orbits
    /// the block forever.
    ///
    /// The counter accumulates on a WIDE ring, because a circling vehicle is only briefly inside
    /// the narrow one, and it survives a target change, because ambulances re-evaluate their
    /// dispatch often and the banked count used to be thrown away. Arrival is never forced at
    /// speed: the snapped path end puts the nav target BESIDE a moving car, it swerves hard and
    /// freezes mid-rotation (police parked across the lane).
    /// </summary>
    internal sealed class ArrivalAssist
    {
        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = false;

        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private M_LaneGeometry m_Geometry => m_Ctx.Geometry;
        private VehicleControl m_Control => m_Ctx.VehicleControl;
        private CorridorBuilder m_Corridor => m_Ctx.Corridor;
        private Dictionary<Entity, StuckState> m_StuckStates => m_Ctx.States.Stuck;
        private Dictionary<Entity, uint> m_ForcedChanges => m_Ctx.States.ForcedChanges;
        public ArrivalAssist(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Emergency vehicles often cannot reach their EXACT path end near an accident (the
        /// end lane is occupied by the wreck/queue, or a repath moved it) and orbit the block
        /// forever. Vanilla has a fallback for exactly this - AccidentTarget/Emergency + lane
        /// IsBlocked + within 30 m => EndNavigation, treat as arrived (PoliceCarAISystem
        /// ~L345 / FireEngineAISystem ~L424) - but OUR corridor keeps the lane moving and
        /// un-blocked, so with the mod active that fallback never fires. Recreate it on our
        /// own trigger: once the vehicle has spent kArriveAssistFrames cumulative within
        /// kArriveAssistRange of its dispatch target without ever reaching the path end,
        /// perform the identical EndNavigation; the vehicle's own AI then runs its normal
        /// arrival branch (secure site / extinguish / disembark) on its next tick. The
        /// counter is cumulative on purpose: an orbiting car is only near the target during
        /// each pass, so a reset-when-far counter would never trip.
        /// </summary>
        public bool TryArrivalAssist(Entity vehicle, ref CarCurrentLane currentLane, float side, uint frame, out bool nearTarget)
        {
            using (ModProfiler.Sample(kProfile, "ArrivalAssist"))
            {
                return TryArrivalAssistImpl(vehicle, ref currentLane, side, frame, out nearTarget);
            }
        }

        private bool TryArrivalAssistImpl(Entity vehicle, ref CarCurrentLane currentLane, float side, uint frame, out bool nearTarget)
        {
            nearTarget = false;
            m_StuckStates.TryGetValue(vehicle, out StuckState stuck);
            bool committed = stuck.m_ArriveCommitFrame != 0u;
            // Already arriving (path endgame reached / on a special end lane) or mid lane
            // change - the vanilla flow is in charge, nothing to assist. A lane change we
            // forced OURSELVES toward the kerb must not bail here though: the committed
            // branch below services it (creep ceiling + FixedLane release).
            if ((currentLane.m_LaneFlags & (CarLaneFlags.EndOfPath | CarLaneFlags.EndReached |
                    CarLaneFlags.ParkingSpace | CarLaneFlags.Area | CarLaneFlags.TransformTarget)) != 0 ||
                (!committed && currentLane.m_ChangeLane != Entity.Null) ||
                !EntityManager.HasComponent<Game.Net.CarLane>(currentLane.m_Lane) ||
                !EntityManager.HasComponent<Target>(vehicle))
            {
                return false;
            }
            // Never force an arrival on a RETURNING unit: with EndOfPath set mid-road the
            // AIs' returning branch deletes the vehicle instead of parking it, and its
            // "target" is the station anyway.
            if (EntityManager.HasComponent<Game.Vehicles.PoliceCar>(vehicle) &&
                (EntityManager.GetComponentData<Game.Vehicles.PoliceCar>(vehicle).m_State & PoliceCarFlags.Returning) != 0)
            {
                return false;
            }
            if (EntityManager.HasComponent<Game.Vehicles.Ambulance>(vehicle) &&
                (EntityManager.GetComponentData<Game.Vehicles.Ambulance>(vehicle).m_State & AmbulanceFlags.Returning) != 0)
            {
                return false;
            }
            if (EntityManager.HasComponent<Game.Vehicles.FireEngine>(vehicle) &&
                (EntityManager.GetComponentData<Game.Vehicles.FireEngine>(vehicle).m_State & FireEngineFlags.Returning) != 0)
            {
                return false;
            }
            Entity target = EntityManager.GetComponentData<Target>(vehicle).m_Target;
            float3 position = EntityManager.GetComponentData<Transform>(vehicle).m_Position;
            float dist = m_Ctx.ArrivalTarget.DistanceToDispatchTarget(target, position);
            // The tight 30 m ring is the "it has effectively arrived, stop meddling" signal
            // (gates our corridor/speed machinery off at the scene). The wider orbit ring is
            // what the stuck-counter accumulates on: a responder that cannot reach the exact
            // path end circles the block at a radius well OUTSIDE 30 m, so counting only
            // within 30 m never trips (logged: an ambulance held nearFor=256 for 90 s while
            // orbiting, then its target flipped and the count was lost - it never arrived).
            nearTarget = dist <= kArriveAssistRange;
            bool withinOrbit = dist <= kArriveOrbitRange;
            // A genuinely elsewhere target restarts the counters; a target that flaps while
            // the vehicle keeps orbiting the SAME spot (ambulances re-evaluate their dispatch
            // target often) keeps its banked progress instead of throwing it away each time.
            if (target != stuck.m_NearTargetEntity)
            {
                stuck.m_NearTargetEntity = target;
                if (!withinOrbit)
                {
                    stuck.m_NearTargetFrames = 0;
                    stuck.m_ArriveStallSince = 0u;
                    stuck.m_ArriveCommitFrame = 0u;
                }
            }
            if (!withinOrbit)
            {
                // Drifted well away from the target (counters stay banked for the next pass
                // of the orbit) - a committed stop is off though; the normal machinery
                // resumes until the vehicle is back in the ring.
                stuck.m_ArriveCommitFrame = 0u;
                m_StuckStates[vehicle] = stuck;
                return false;
            }
            stuck.m_NearTargetFrames++;
            stuck.m_LastSeenFrame = frame;

            // Progress watch: is it still getting CLOSER? A responder that cannot reach the
            // exact path end reaches a closest point and then only gets further as it loops
            // the block - that stall, not wall-clock time, is the real "it can get no nearer"
            // signal, and it trips even while the car keeps circling at speed (the old
            // speed<=kArriveAssistMaxSpeed gate never fired mid-orbit: the near arc is too
            // short to brake in, so a police car circled the accident forever).
            if (stuck.m_ArriveStallSince == 0u || dist < stuck.m_ArriveBestDist - kArriveProgressMeters)
            {
                stuck.m_ArriveBestDist = dist;
                stuck.m_ArriveStallSince = frame;
            }
            bool stalled = frame - stuck.m_ArriveStallSince >= kArriveStallFrames;

            // Commit to an on-foot arrival only once it is genuinely close (kArriveAssistRange)
            // AND can get no closer: either it has circled long enough (orbit counter) or its
            // approach has stalled. Otherwise let the normal corridor keep bringing it in.
            if (!committed)
            {
                if (dist > kArriveAssistRange ||
                    (stuck.m_NearTargetFrames < kArriveAssistFrames && !stalled))
                {
                    m_StuckStates[vehicle] = stuck;
                    return false;
                }
                // The route genuinely ends OFF the street - the target building has its own
                // driveway/parking lot the vehicle can (and should) drive into. Queueing into
                // a drive looks exactly like a stall to the progress watch, so give vanilla a
                // much longer leash there before forcing a street-side stop.
                if (m_Ctx.ArrivalTarget.RouteEndsOffStreet(vehicle) &&
                    frame - stuck.m_ArriveStallSince < kArrivePrivateStallFrames)
                {
                    m_StuckStates[vehicle] = stuck;
                    return false;
                }
                stuck.m_ArriveCommitFrame = frame;
                if (Mod.Setting.VerboseLogging)
                {
                    Mod.Log.Info($"[arrive] veh={vehicle.Index} committed kerb-stop dist={dist:F1} m " +
                        $"(nearFor={stuck.m_NearTargetFrames} stalled={(stalled ? 1 : 0)})");
                }
            }
            else if (frame - stuck.m_ArriveCommitFrame >= kArriveCommitTimeout)
            {
                // The stop could not complete (kerb lane never cleared / endless junction
                // roll) - abandon it, re-arm the counters and let the corridor machinery
                // have another go; the assist re-trips once the vehicle stalls again.
                stuck.m_ArriveCommitFrame = 0u;
                stuck.m_NearTargetFrames = 0;
                stuck.m_ArriveStallSince = 0u;
                m_StuckStates[vehicle] = stuck;
                if (Mod.Setting.VerboseLogging)
                {
                    Mod.Log.Info($"[arrive] veh={vehicle.Index} kerb-stop timed out at dist={dist:F1} m - resuming normal assist");
                }
                return false;
            }

            // Committed. Release the FixedLane pin of a kerb lane change once it finished or
            // timed out - the main release block never runs while this method early-returns.
            if (m_ForcedChanges.TryGetValue(vehicle, out uint kerbChangeStart) &&
                (currentLane.m_ChangeLane == Entity.Null || frame - kerbChangeStart > kForcedChangeTimeout))
            {
                currentLane.m_LaneFlags &= ~CarLaneFlags.FixedLane;
                m_ForcedChanges.Remove(vehicle);
            }
            if (currentLane.m_ChangeLane != Entity.Null)
            {
                // Mid lane change toward the kerb: roll it through gently, never stop halfway
                // across two lanes.
                m_Control.SetCeiling(vehicle, kArriveRollSpeed);
                m_StuckStates[vehicle] = stuck;
                return true;
            }
            // Never park on a junction/turn lane (that is the ambulance-diagonally-across-
            // the-corner shot): those lanes are curved and any lateral offset plants the
            // vehicle across the corner. Roll on at a creep until a straight edge lane is
            // under the wheels, then stop there.
            if (!EntityManager.HasComponent<Owner>(currentLane.m_Lane) ||
                !EntityManager.HasComponent<Game.Net.Edge>(
                    EntityManager.GetComponentData<Owner>(currentLane.m_Lane).m_Owner))
            {
                m_Control.SetCeiling(vehicle, kArriveRollSpeed);
                m_StuckStates[vehicle] = stuck;
                return true;
            }
            // Park on the KERBMOST lane, not wherever the orbit happened to leave the
            // vehicle: a stop in a middle/left lane (kerb offset or not) straddles the
            // neighbour lane at an angle. Try a forced change toward the kerb for a while;
            // if that lane never clears, parking in the current lane is the fallback.
            if (frame - stuck.m_ArriveCommitFrame < kArriveKerbChangeWindow &&
                !m_ForcedChanges.ContainsKey(vehicle) &&
                m_Ctx.ArrivalTarget.TryKerbLaneChange(vehicle, ref currentLane, side, frame))
            {
                m_Control.SetCeiling(vehicle, kArriveRollSpeed);
                m_StuckStates[vehicle] = stuck;
                return true;
            }

            // It is close but cannot reach the exact path end. Ease it over to the RIGHT kerb
            // (the side civilians do NOT use for the corridor) just short of the wreck and
            // bring it to a stop - a police car disembarks its officers on foot at a
            // standstill, so they walk the last few metres to secure the scene. The offset
            // only physically happens while the car is still ROLLING (a dead-stopped car keeps
            // its written m_LanePosition purely notional - see the nose-out note), so creep it
            // to the kerb first, then hard-stop, then snap the arrival on a later tick (never
            // under a moving car - it swerves and freezes across the lane).
            float kerbUnits = math.min(kEvadeMeters, m_Ctx.PrefabGeometry.MaxLateralMeters(vehicle)) / m_Ctx.PrefabGeometry.LateralSlack(vehicle, currentLane.m_Lane);
            currentLane.m_LanePosition = math.lerp(currentLane.m_LanePosition, side * kerbUnits, kPullRate * 1.5f);
            bool atKerb = math.abs(currentLane.m_LanePosition) >= kerbUnits - 0.1f;
            float arriveSpeed = math.length(EntityManager.GetComponentData<Moving>(vehicle).m_Velocity);
            if (arriveSpeed > kArriveStopSpeed)
            {
                // Still rolling: creep to the shoulder (do not race the wreck), then hold at a
                // dead stop once the kerb is reached. If a blocker stops it short of the kerb,
                // it simply decelerates below the stop speed and arrives where it stands (the
                // branch below) - better an on-foot arrival a metre off the kerb than a hang.
                m_Control.SetCeiling(vehicle, atKerb ? 0f : kCreepSpeed);
                m_StuckStates[vehicle] = stuck;
                return true; // handled: hold the kerb offset, keep the rest of the pass off
            }
            stuck.m_NearTargetFrames = 0;
            stuck.m_ArriveStallSince = 0u;
            stuck.m_ArriveCommitFrame = 0u;
            m_StuckStates[vehicle] = stuck;

            // Identical to PoliceCarAISystem.EndNavigation: snap the curve to its end, mark
            // EndOfPath, drop the remaining route. The AI's PathEndReached branch fires next
            // tick. Also clear any repath-demand flags so FindNewPath does not immediately
            // route the vehicle away from its forced arrival.
            currentLane.m_CurvePosition.z = currentLane.m_CurvePosition.y;
            currentLane.m_LaneFlags |= CarLaneFlags.EndOfPath;
            if (EntityManager.HasBuffer<CarNavigationLane>(vehicle))
            {
                EntityManager.GetBuffer<CarNavigationLane>(vehicle).Clear();
            }
            if (EntityManager.HasComponent<PathOwner>(vehicle))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
                pathOwner.m_ElementIndex = 0;
                pathOwner.m_State &= ~(PathFlags.Obsolete | PathFlags.DivertObsolete);
                EntityManager.SetComponentData(vehicle, pathOwner);
            }
            if (EntityManager.HasBuffer<PathElement>(vehicle))
            {
                EntityManager.GetBuffer<PathElement>(vehicle).Clear();
            }
            if (Mod.Setting.VerboseLogging)
            {
                Mod.Log.Info($"[arrive] veh={vehicle.Index} forced arrival at target={target.Index} dist={dist:F1} m " +
                    $"(cumulative {kArriveAssistFrames} frames within {kArriveOrbitRange:F0} m without reaching the path end)");
            }
            return true;
        }
    }
}

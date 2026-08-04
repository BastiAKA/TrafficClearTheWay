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
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing
{
    /// <summary>
    /// Longitudinal control of the traffic around a responder: hold it, slow it, or move it ON.
    ///
    /// Which one is right depends entirely on what the responder is doing. Cars being overtaken
    /// are held so it can clear them; cars ahead of a MERGE point are pushed FORWARD instead, to
    /// open the gap it slots into; a queue at a dead junction is pushed through it. Nothing is
    /// ever frozen to a dead stop - every hold keeps a small forward budget, so a held queue keeps
    /// draining and nothing gets permanently stuck.
    /// </summary>
    internal sealed class HoldVehicles
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private M_LaneGeometry m_Geometry => m_Ctx.Geometry;
        private VehicleControl m_Control => m_Ctx.VehicleControl;
        private CorridorBuilder m_Corridor => m_Ctx.Corridor;
        private Dictionary<Entity, StuckState> m_StuckStates => m_Ctx.States.Stuck;
        private Dictionary<Entity, uint> m_ForcedChanges => m_Ctx.States.ForcedChanges;
        private Dictionary<Entity, uint> m_PushedCars => m_Ctx.PushedCars.Cars;
        private SimulationSystem m_SimulationSystem => m_Ctx.Simulation;
        private Dictionary<Entity, PushClaim> m_PushClaims => m_Ctx.PushedCars.PushClaims;
        private List<Entity> m_ReleaseBuffer => m_Ctx.PushedCars.ReleaseBuffer;
        private Dictionary<Entity, uint> m_BlockerShift => m_Ctx.PushedCars.BlockerShift;
        public HoldVehicles(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Cars currently being overtaken must not drive off mid-squeeze: rolling cars are
        /// braked to a crawl; standing cars get their navigation target steered sideways so
        /// their written evade offset becomes a PHYSICAL offset. A blocked car's own
        /// navigation never moves its target laterally (zero speed budget), which is why
        /// corridors previously existed in the data but not on the street.
        /// </summary>
        public void HoldStillIfBeingOvertaken(Entity other, bool evadeActive, float aheadDistance, CarCurrentLane otherLane)
        {
            if (!evadeActive || aheadDistance > kHoldRange ||
                !EntityManager.HasComponent<CarNavigation>(other))
            {
                return;
            }
            // We force these cars to stand - protect them from the game's stuck-vehicle
            // detection, which would otherwise despawn held traffic around the corridor.
            if (EntityManager.HasComponent<PathOwner>(other))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(other);
                if ((pathOwner.m_State & PathFlags.Stuck) != 0)
                {
                    pathOwner.m_State &= ~PathFlags.Stuck;
                    EntityManager.SetComponentData(other, pathOwner);
                }
            }
            float speed = EntityManager.HasComponent<Moving>(other)
                ? math.length(EntityManager.GetComponentData<Moving>(other).m_Velocity)
                : 0f;
            // Standing cars: steer them onto their evade offset at low speed so the corridor
            // physically opens (their own blocked navigation never moves them sideways).
            // 2.5 m forward lead (was 0.5): with barely any forward component the bearing to a
            // ~1.5 m lateral offset pointed almost broadside and the car pivoted diagonally
            // across the road (the wedged-van look) - the v0.22 "never steer sideways" lesson
            // through the back door. With a forward-dominant target (plus the bearing clamp in
            // TryGetLateralTarget) it CREEPS diagonally aside like a real corridor.
            if (speed <= 1f && otherLane.m_ChangeLane == Entity.Null &&
                m_Ctx.LanePosition.TryGetLateralTarget(other, otherLane, 2.5f, out float3 target, out quaternion rotation))
            {
                m_Control.SetFloor(other, kSidestepSpeed, hasTarget: true, target, rotation);
                return;
            }
            // Hold ONLY a car that has actually made room - the same "is aside" threshold the
            // squeeze gate uses, so both agree on when a blocker has done its part. A car
            // realizes its written lateral offset by DRIVING, so braking one that is still
            // mid-lane freezes it exactly where it blocks the responder and it can never
            // finish moving aside: that is the deadlock where cars just stop as soon as a
            // responder is near and it then waits behind them forever. Keep it rolling
            // instead - it keeps steering toward the offset every tick and clears the way.
            // Cap it at a minimal CREEP rather than a dead stop: a car in front must always be
            // able to inch forward, otherwise a held queue never drains and the whole cluster
            // stays stuck. The ceiling still lets its navigation stop it behind a car that
            // genuinely cannot move, so it only ever creeps into real space.
            // "Has made room" is relative to what THIS car was told to do, not a flat number.
            // With today's targets (2-4 units) the old constant slowed a car after ~8% of its
            // way out; since a car realises its offset only by driving, it then just steered
            // and stayed put - the "they only turn, they never pull out" report. Fall back to
            // the flat threshold when no claim is on record (nobody pushed it this pass).
            float reached = m_PushClaims.TryGetValue(other, out PushClaim heldClaim) && heldClaim.m_TargetUnits > 0f
                ? heldClaim.m_TargetUnits * kAsideReachedFraction
                : kMinAsidePosition;
            if (math.abs(otherLane.m_LanePosition) >= reached)
            {
                m_Control.SetCeiling(other, kMinCreepSpeed);
            }
        }

        /// <summary>
        /// #6: hold the traffic the emergency vehicle is actively passing to a gentle speed
        /// ceiling so it does not race the vehicle. Reuses the corridor lanes already built
        /// this tick and the same along-corridor distance metric as PullCarsAside; targets
        /// cars from just behind the vehicle's nose to a short distance ahead. SetCeiling takes
        /// the minimum, so cars already braked harder (held/crawling) are unaffected.
        /// </summary>
        public void SlowPassedTraffic(Entity vehicle)
        {
            for (int c = 0; c < m_Corridor.CorridorLanes.Count; c++)
            {
                CorridorLane corridorLane = m_Corridor.CorridorLanes[c];
                if (!EntityManager.HasBuffer<LaneObject>(corridorLane.m_Lane))
                {
                    continue;
                }
                float curveLength = 1f;
                if (EntityManager.HasComponent<Curve>(corridorLane.m_Lane))
                {
                    curveLength = math.max(1f, EntityManager.GetComponentData<Curve>(corridorLane.m_Lane).m_Length);
                }
                DynamicBuffer<LaneObject> laneObjects = EntityManager.GetBuffer<LaneObject>(corridorLane.m_Lane, isReadOnly: true);
                for (int i = 0; i < laneObjects.Length; i++)
                {
                    LaneObject laneObject = laneObjects[i];
                    // Window-gate BEFORE any component lookup, exactly as PullCarsAside does:
                    // aheadDistance needs only the buffer's own curve position, so gating on it
                    // costs one multiply - whereas the ~7 lookups below were being paid for EVERY
                    // car on the lane, and this runs over every corridor lane of every passing
                    // responder. The buffer is sorted by curve position (NetUtils.AddLaneObject),
                    // so once past the far edge of the pass window we stop entirely.
                    float delta = corridorLane.m_Inverted
                        ? corridorLane.m_MinPos - laneObject.m_CurvePosition.x
                        : laneObject.m_CurvePosition.x - corridorLane.m_MinPos;
                    float aheadDistance = corridorLane.m_StartOffset + delta * curveLength;
                    if (corridorLane.m_Inverted)
                    {
                        if (aheadDistance > kPassWindowAhead) continue;      // ahead of the window; closer cars still follow
                        if (aheadDistance < -kPassWindowBehind) break;       // behind it; the rest are further behind
                    }
                    else
                    {
                        if (aheadDistance < -kPassWindowBehind) continue;    // behind the window; cars ahead still follow
                        if (aheadDistance > kPassWindowAhead) break;         // ahead of it; the rest are further ahead
                    }
                    Entity other = laneObject.m_LaneObject;
                    if (other == vehicle || !EntityManager.Exists(other) ||
                        !EntityManager.HasComponent<Car>(other) ||
                        !EntityManager.HasComponent<CarCurrentLane>(other) ||
                        EntityManager.HasComponent<ParkedCar>(other) ||
                        EntityManager.HasComponent<Deleted>(other))
                    {
                        continue;
                    }
                    // Never slow another emergency vehicle; trailers follow their controller.
                    if ((EntityManager.GetComponentData<Car>(other).m_Flags & CarFlags.Emergency) != 0)
                    {
                        continue;
                    }
                    if (VehicleTypes.VehicleTrailerExt.IsTrailer(EntityManager, other))
                    {
                        continue;
                    }
                    // Curve position is only comparable to the EV's on the same lane.
                    if (EntityManager.GetComponentData<CarCurrentLane>(other).m_Lane != corridorLane.m_Lane)
                    {
                        continue;
                    }
                    m_Control.SetCeiling(other, kPassCeilingSpeed);
                }
            }
        }

        /// <summary>
        /// When the emergency vehicle merges back off the oncoming lane, let the few cars
        /// just ahead of it creep forward so a gap opens right where it wants to slot in.
        /// </summary>
        public void NudgeMergeGap(Entity vehicle, CarCurrentLane currentLane)
        {
            Entity lane = currentLane.m_Lane;
            if (!EntityManager.HasBuffer<LaneObject>(lane) || !EntityManager.HasComponent<Curve>(lane))
            {
                return;
            }
            float length = math.max(1f, EntityManager.GetComponentData<Curve>(lane).m_Length);
            bool inverted = currentLane.m_CurvePosition.z < currentLane.m_CurvePosition.x;
            DynamicBuffer<LaneObject> laneObjects = EntityManager.GetBuffer<LaneObject>(lane, isReadOnly: true);
            int nudged = 0;
            for (int i = 0; i < laneObjects.Length && nudged < kMergeGapCars; i++)
            {
                Entity other = laneObjects[i].m_LaneObject;
                if (other == vehicle || !EntityManager.Exists(other) ||
                    !EntityManager.HasComponent<CarNavigation>(other) ||
                    (EntityManager.HasComponent<Car>(other) &&
                     (EntityManager.GetComponentData<Car>(other).m_Flags & CarFlags.Emergency) != 0))
                {
                    continue;
                }
                float ahead = (inverted
                    ? currentLane.m_CurvePosition.x - laneObjects[i].m_CurvePosition.x
                    : laneObjects[i].m_CurvePosition.x - currentLane.m_CurvePosition.x) * length;
                if (ahead > 0.5f && ahead < kMergeGapRange)
                {
                    m_Control.SetFloor(other, kMergeGapSpeed, hasTarget: false, default, default);
                    nudged++;
                }
            }
        }

        /// <summary>
        /// Break the queue in front through a stuck junction (the 3-minute rule): grant every
        /// car ahead on this on-path corridor lane a forward speed budget so it rolls on. The
        /// floor only raises the speed cap - each car's navigation still stops it behind a car
        /// that genuinely cannot move, so they proceed as fast as the junction actually clears.
        /// </summary>
        public void PushQueueForward(Entity vehicle, CorridorLane corridorLane)
        {
            if (!EntityManager.HasBuffer<LaneObject>(corridorLane.m_Lane) ||
                !EntityManager.HasComponent<Curve>(corridorLane.m_Lane))
            {
                return;
            }
            float curveLength = math.max(1f, EntityManager.GetComponentData<Curve>(corridorLane.m_Lane).m_Length);
            DynamicBuffer<LaneObject> laneObjects = EntityManager.GetBuffer<LaneObject>(corridorLane.m_Lane, isReadOnly: true);
            for (int i = 0; i < laneObjects.Length; i++)
            {
                Entity other = laneObjects[i].m_LaneObject;
                if (other == vehicle || !EntityManager.Exists(other) ||
                    !EntityManager.HasComponent<CarNavigation>(other) ||
                    (EntityManager.HasComponent<Car>(other) &&
                     (EntityManager.GetComponentData<Car>(other).m_Flags & CarFlags.Emergency) != 0))
                {
                    continue;
                }
                float delta = corridorLane.m_Inverted
                    ? corridorLane.m_MinPos - laneObjects[i].m_CurvePosition.x
                    : laneObjects[i].m_CurvePosition.x - corridorLane.m_MinPos;
                float ahead = corridorLane.m_StartOffset + delta * curveLength;
                if (ahead > 0.5f && ahead < kLightBreakRange)
                {
                    m_Control.SetFloor(other, kLightBreakSpeed, hasTarget: false, default, default);
                }
            }
        }

        /// <summary>
        /// Force a longitudinal slot open on the lane a responder is changing INTO, so a forced
        /// lane change actually completes in packed traffic instead of stalling half-way (the
        /// "lane takeover does not assert" case). The car just ahead of the insertion point is
        /// nudged forward, the car just behind is held back - a gap opens where the responder
        /// merges. The lateral corridor push alone never did this: it shifts cars sideways, but
        /// a bumper-to-bumper lane has no gap ALONG it to slot into.
        /// </summary>
        public void OpenLaneChangeSlot(Entity vehicle, Entity targetLane, float curvePosition, bool inverted)
        {
            if (!EntityManager.HasBuffer<LaneObject>(targetLane) || !EntityManager.HasComponent<Curve>(targetLane))
            {
                return;
            }
            float length = math.max(1f, EntityManager.GetComponentData<Curve>(targetLane).m_Length);
            DynamicBuffer<LaneObject> laneObjects = EntityManager.GetBuffer<LaneObject>(targetLane, isReadOnly: true);
            for (int i = 0; i < laneObjects.Length; i++)
            {
                Entity other = laneObjects[i].m_LaneObject;
                if (other == vehicle || !EntityManager.Exists(other) ||
                    !EntityManager.HasComponent<CarNavigation>(other) ||
                    (EntityManager.HasComponent<Car>(other) &&
                     (EntityManager.GetComponentData<Car>(other).m_Flags & CarFlags.Emergency) != 0))
                {
                    continue;
                }
                float ahead = (inverted
                    ? curvePosition - laneObjects[i].m_CurvePosition.x
                    : laneObjects[i].m_CurvePosition.x - curvePosition) * length;
                if (ahead > 0.5f && ahead < kMergeGapRange)
                {
                    m_Control.SetFloor(other, kMergeGapSpeed, hasTarget: false, default, default); // car ahead: roll forward
                }
                else if (ahead <= 0.5f && ahead > -kOvertakeClearBehind)
                {
                    m_Control.SetCeiling(other, kHoldSpeed); // car behind: hold back so the gap stays open
                    m_Control.ClearStuck(other);
                }
            }
        }
    }
}

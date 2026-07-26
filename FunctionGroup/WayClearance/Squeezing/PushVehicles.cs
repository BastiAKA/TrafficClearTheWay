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
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes;
// Game.Net has a LaneGeometry of its own - ours wins here, like the CarLaneFlags alias above.
using LaneGeometry = ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding.M_LaneGeometry;

namespace ClearTheWay.FunctionGroup.WayClearance.Squeezing
{
    /// <summary>
    /// Pulls the traffic on one corridor lane aside so a responder can get through.
    ///
    /// Two hard-won rules govern this:
    ///  - A car only realises a lateral offset by DRIVING; the navigation has no sideways lever on
    ///    a standing car. So a car that has not yet moved aside is never braked to a halt - braking
    ///    it froze it mid-lane forever ("cars just stop when an ambulance is near").
    ///  - Overlapping corridors of a convoy must not fight over the same car. The first push
    ///    direction claims it and a conflicting push skips it, otherwise the car is lerped both
    ///    ways in alternating passes of the same tick and visibly jitters.
    ///
    /// Articulated rigs are handled apart throughout: a trailer has no lateral lever of its own
    /// and off-tracks when the tractor is yanked aside, which NARROWS the corridor. Rigs drift
    /// gently while rolling and hold straight when stopped.
    /// </summary>
    internal sealed class PushVehicles
    {
        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = false;

        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private VehicleControl vehicleControl => m_Ctx.VehicleControl;

        private Dictionary<Entity, uint> m_PushedCars => m_Ctx.PushedCars.Cars;
        private Dictionary<Entity, PushClaim> m_PushClaims => m_Ctx.PushedCars.PushClaims;
        private Dictionary<Entity, uint> m_BlockerShift => m_Ctx.PushedCars.BlockerShift;
        public PushVehicles(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <param name="evadeMeters">How far an evading car is pushed aside, in metres. The caller
        /// owns the escalation policy - the default is the normal hard evade, and a responder that
        /// has been stuck long enough simply asks for more (kDeepEvadeMeters).</param>
        public int PullCarsAside(Entity vehicle, CorridorLane corridorLane, uint frame, bool hardEvade, bool allowHold = true, bool drainAhead = false, bool shakeStuck = false, float evadeMeters = kEvadeMeters)
        {
            using (ModProfiler.Sample(kProfile, "TrafficShaper.PullCarsAside"))
            {
                return PullCarsAsideImpl(vehicle, corridorLane, frame, hardEvade, allowHold, drainAhead, shakeStuck, evadeMeters);
            }
        }

        private int PullCarsAsideImpl(Entity vehicle, CorridorLane corridorLane, uint frame, bool hardEvade, bool allowHold, bool drainAhead, bool shakeStuck, float evadeMeters)
        {
            if (!EntityManager.HasBuffer<LaneObject>(corridorLane.m_Lane))
            {
                return 0;
            }

            // Distances along the corridor are cumulative (start offset of the lane plus
            // position on it), so evade and hold-still keep working across segment and
            // intersection boundaries instead of ending at the current lane.
            float curveLength = 1f;
            if (EntityManager.HasComponent<Curve>(corridorLane.m_Lane))
            {
                curveLength = math.max(1f, EntityManager.GetComponentData<Curve>(corridorLane.m_Lane).m_Length);
            }
            // Lane width is constant for this corridor lane - compute it ONCE here instead of
            // re-deriving it for every car below (GetLateralSlack used to do it per car).
            float corridorLaneWidth = m_Ctx.PrefabGeometry.LaneWidth(corridorLane.m_Lane);

            // Skip cars outside the corridor window BEFORE any component lookup. aheadDistance
            // needs only the buffer's own curve position, so gating on it costs one multiply -
            // whereas the ~8 component lookups below were being paid for EVERY car on the lane,
            // most of them out of range (this per-car scan is the corridor's dominant cost with
            // ~170 responders). The LaneObject buffer is sorted by curve position
            // (NetUtils.AddLaneObject), so once we pass the far edge of the window we stop
            // entirely. The cap keeps a generous margin past the corridor so no in-range car is
            // dropped despite the front/back curve-position ordering.
            float aheadCap = (Mod.Setting != null ? Mod.Setting.CorridorDistance : 120f) + kEvadeRange;
            DynamicBuffer<LaneObject> laneObjects = EntityManager.GetBuffer<LaneObject>(corridorLane.m_Lane, isReadOnly: true);
            int count = 0;
            for (int i = 0; i < laneObjects.Length; i++)
            {
                LaneObject laneObject = laneObjects[i];
                float delta = corridorLane.m_Inverted
                    ? corridorLane.m_MinPos - laneObject.m_CurvePosition.x
                    : laneObject.m_CurvePosition.x - corridorLane.m_MinPos;
                float aheadDistance = corridorLane.m_StartOffset + delta * curveLength;
                if (corridorLane.m_Inverted)
                {
                    if (aheadDistance > aheadCap) continue;      // ahead of the window; closer cars still follow
                    if (aheadDistance < -kBehindRange) break;    // behind the window; the rest are further behind
                }
                else
                {
                    if (aheadDistance < -kBehindRange) continue; // behind the window; cars ahead still follow
                    if (aheadDistance > aheadCap) break;         // ahead of the window; the rest are further ahead
                }
                Entity other = laneObject.m_LaneObject;
                if (other == vehicle || !EntityManager.Exists(other))
                {
                    continue;
                }
                if (!EntityManager.HasComponent<Car>(other) ||
                    !EntityManager.HasComponent<CarCurrentLane>(other) ||
                    EntityManager.HasComponent<ParkedCar>(other) ||
                    EntityManager.HasComponent<Deleted>(other))
                {
                    continue;
                }
                // Other emergency vehicles keep their line.
                if ((EntityManager.GetComponentData<Car>(other).m_Flags & CarFlags.Emergency) != 0)
                {
                    continue;
                }
                // Recovery vehicles keep their line too - but only while they are ROLLING. That
                // rule exists because tow trucks driving in convoy to a pile-up are corridor
                // owners themselves and kept shoving each other onto the kerb. A STOPPED one is
                // the opposite case: it is what the colleague behind it is wedged against, and
                // since we never touched it, nothing could ever open that chain (field log:
                // three recovery trucks nose to tail, each blockedBy the next, all at 0.0 for
                // ~2 minutes, no lateral gap for anyone to squeeze through). Easing a standing
                // one aside costs it nothing - it is not going anywhere - and is what lets the
                // queue behind it get past.
                if (EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(other))
                {
                    float otherSpeed = EntityManager.HasComponent<Moving>(other)
                        ? math.length(EntityManager.GetComponentData<Moving>(other).m_Velocity)
                        : 0f;
                    if (otherSpeed >= kMaintenancePushMaxSpeed)
                    {
                        continue;
                    }
                }
                // Trailers follow their controller.
                if (VehicleTrailerExt.IsTrailer(EntityManager, other))
                {
                    continue;
                }
                // (aheadDistance was already computed and window-gated at the top of the loop.)
                CarCurrentLane otherLane = EntityManager.GetComponentData<CarCurrentLane>(other);
                if (otherLane.m_Lane != corridorLane.m_Lane)
                {
                    continue;
                }
                // Protect every vehicle in the immediate maneuver area from despawn - the
                // disruption we cause can otherwise get any of them flagged stuck & deleted.
                if (aheadDistance < kProtectStuckRange)
                {
                    vehicleControl.ClearStuck(other);
                }
                // Stuck-jam shake (async): the responder is genuinely stuck, so nudge the STOPPED
                // cars around it to re-evaluate their route. Flagging the lane Obsolete is the
                // game's own recovery path - CarNavigationSystem re-localizes and re-paths the car
                // next tick, on the async pathfinder jobs (our cost here is one flag write). A
                // stopped CS2 car does not re-path on its own once its way clears; this animates
                // it, the same way a TTE signal change does. Staggered per car so only a trickle
                // is queued each tick. Only stopped, not-mid-change, not-already-flagged cars.
                if (shakeStuck && ((frame + (uint)other.Index) & kShakeMask) == 0u &&
                    otherLane.m_ChangeLane == Entity.Null &&
                    (otherLane.m_LaneFlags & CarLaneFlags.Obsolete) == 0 &&
                    EntityManager.HasComponent<Moving>(other) &&
                    math.lengthsq(EntityManager.GetComponentData<Moving>(other).m_Velocity) < 0.25f)
                {
                    otherLane.m_LaneFlags |= CarLaneFlags.Obsolete;
                    EntityManager.SetComponentData(other, otherLane);
                }
                // Gridlock drain: the responder has been fully stopped behind a blocker for a
                // long time and cannot pass (no oncoming crossover, not squeezing). Holding /
                // side-stepping the queue AHEAD of it then only freezes the very cars whose
                // driving off would clear the jam - a tram or a boxed-in car that just needs to
                // roll through the junction. So stop touching everything ahead of the nose and
                // let it flow; the responder follows once the road drains. (Cars alongside /
                // behind keep normal handling so nobody rear-ends the responder.)
                // NOTE: this only SKIPS our own push/hold - it never mutates the car's lane
                // state. An earlier version nulled m_ChangeLane here to "abort" a stuck change;
                // that corrupted CarCurrentLane mid-change and hard-crashed a Burst job plus
                // mass-despawned traffic. Do NOT write m_ChangeLane from here.
                if (drainAhead && aheadDistance > 0f)
                {
                    continue;
                }
                // A car that is already changing lanes is clearing the corridor by itself,
                // but while it is being overtaken it must not roll off either.
                if (otherLane.m_ChangeLane != Entity.Null)
                {
                    m_Ctx.Hold.HoldStillIfBeingOvertaken(other, hardEvade && allowHold, aheadDistance, otherLane);
                    count++;
                    continue;
                }

                // Overlapping corridors (several responders close together): if another
                // responder is already parting this car the OTHER way, do not fight over it -
                // the first direction wins and this pass leaves the car alone entirely (the
                // claiming responder's own pass keeps pushing/holding it). Same-direction
                // pushes simply refresh the claim.
                float pushDir = corridorLane.m_PushDirection;
                if (m_PushClaims.TryGetValue(other, out PushClaim claim) &&
                    frame - claim.m_Frame <= kPushClaimFrames &&
                    claim.m_Dir * pushDir < 0f)
                {
                    count++;
                    continue;
                }
                m_PushClaims[other] = new PushClaim { m_Dir = pushDir, m_Frame = frame };

                // Articulated rigs need special handling throughout: the trailer has no lateral
                // lever and swings out when the tractor is pulled across, actually narrowing the
                // gap. Their whole make-way behaviour lives in Truck_WithTrailer.
                bool articulated = VehicleTrailerExt.IsArticulated(EntityManager, other);

                // Cars right in front of a stuck emergency vehicle evade harder - partially
                // onto the sidewalk or parking strip - so a corridor opens even when they
                // are boxed in and cannot change lanes. Rigs never evade onto the sidewalk.
                bool evade = hardEvade && !articulated && aheadDistance <= kEvadeRange;

                if (articulated)
                {
                    m_Ctx.Trucks.MakeWay(other, ref otherLane, corridorLane, corridorLaneWidth, aheadDistance, frame);
                    count++;
                    continue;
                }

                // Aim in meters, not lane units: on narrow lanes one unit is only a few
                // decimeters, which made the corridor invisible exactly where it is
                // needed most. Values beyond +-0.5 reach onto the sidewalk/parking strip.
                // Central-channel lanes carry their own graduated offset (outer lanes clear
                // further); classic lanes leave it 0 and fall back to the flat kEdgeMeters.
                float baseMeters = corridorLane.m_PushMeters > 0f ? corridorLane.m_PushMeters : kEdgeMeters;
                float meters = evade ? math.max(evadeMeters, baseMeters) : baseMeters;
                // A van, garbage truck or bus needs MORE than a car: it clears the lane by being
                // pushed diagonally forward, and at ~45 degrees the room it makes is a quarter of
                // its own length (Delivery_MidSize). At a car's offset its body still lies across
                // the gap.
                meters = m_Ctx.MidSize.PushMeters(other, meters);
                // Past the lane edge there is often a tram bed, green strip or median that a car
                // can perfectly well stand on - and on a narrow road, clearing only to the edge
                // leaves no gap at all (the field report this was built for). Only while EVADING:
                // the gentle corridor keeps traffic inside its lane, and driving on the grass
                // stays an escalation, not the normal picture. LateralRoom returns 0 when there is
                // nothing crossable there, or when a tram is currently on the bed.
                if (evade)
                {
                    meters += m_Ctx.Room.CrossableMeters(corridorLane.m_Lane, corridorLane.m_PushDirection, frame);
                }
                meters = math.min(meters, m_Ctx.PrefabGeometry.MaxLateralMeters(other));
                float units = meters / m_Ctx.PrefabGeometry.LateralSlack(other, corridorLaneWidth);
                // Record where this car was actually sent, so HoldVehicles can tell "has made
                // room" from "has barely twitched" and does not brake it after a few centimetres.
                m_PushClaims[other] = new PushClaim { m_Dir = pushDir, m_Frame = frame, m_TargetUnits = units };
                float target = corridorLane.m_PushDirection * units;
                float rate = evade ? kPullRate * 1.5f : kPullRate;
                float newPos = math.lerp(otherLane.m_LanePosition, target, rate);
                if (math.abs(newPos - otherLane.m_LanePosition) > 0.0005f)
                {
                    otherLane.m_LanePosition = newPos;
                    EntityManager.SetComponentData(other, otherLane);
                }

                m_Ctx.Hold.HoldStillIfBeingOvertaken(other, hardEvade, aheadDistance, otherLane);

                m_PushedCars[other] = frame;
                count++;
            }
            return count;
        }
    }
}

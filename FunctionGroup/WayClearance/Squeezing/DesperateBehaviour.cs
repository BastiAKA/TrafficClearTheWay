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

namespace ClearTheWay.FunctionGroup.WayClearance.Squeezing
{
    /// <summary>
    /// Last resort: putting the responder onto the ONCOMING carriageway to get past a queue.
    ///
    /// Returns 0 none / 1 merging back / 2 fully across. Most of the historic wobble bugs lived
    /// here, and the current shape is the fix:
    ///  - COMMIT HYSTERESIS. Starting needs a large clear gap; once out there it stays out for a
    ///    sticky window. Without it the vehicle darted out and back constantly.
    ///  - SPEED DECOUPLING. Once committed the maneuver stays alive regardless of speed. The
    ///    pass-speed boost otherwise exceeded the evade speed cap, which disabled the pass, which
    ///    dropped the speed, which re-enabled it - a feedback oscillation.
    ///  - MERGE DEBOUNCE. The only soft merge reason is "the queue is passed". A distant oncoming
    ///    car used to trigger it, and since a held car is nearly always in sight that aborted the
    ///    pass moments after every commit.
    ///  - STALL ABORT. A committed pass that gains no ground over its watch window is going
    ///    nowhere (red junction, blocked continuation) and merges back instead of bouncing along
    ///    the oncoming side forever.
    /// </summary>
    internal sealed class DesperateBehaviour
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
        public DesperateBehaviour(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Shift the stuck emergency vehicle across the center line onto the oncoming
        /// carriageway of the same road edge. The oncoming lane is found geometrically
        /// (nearest car lane on the corridor side that is not part of the vehicle's own
        /// lane group). With a sufficient gap in oncoming traffic the vehicle moves fully
        /// onto that lane; otherwise it straddles the center line and waits. The offset is
        /// pure lateral displacement (m_LanePosition), so pathing stays on its own lane and
        /// the vehicle merges back automatically once it is no longer stuck.
        /// </summary>
        /// <summary>
        /// Returns: 0 = not using the oncoming lane, 1 = straddling the center line
        /// (merging / waiting for a gap), 2 = fully on the oncoming carriageway.
        /// </summary>
        public int TryOncomingDisplacement(Entity vehicle, ref CarCurrentLane currentLane, float side, bool desperate, uint frame,
            out float oncomingClearAhead, out OncomingReason reason, out float nearestOffset)
        {
            using (ModProfiler.Sample(kProfile, "DesperateBehaviour"))
            {
                return TryOncomingDisplacementImpl(vehicle, ref currentLane, side, desperate, frame,
                    out oncomingClearAhead, out reason, out nearestOffset);
            }
        }

        private int TryOncomingDisplacementImpl(Entity vehicle, ref CarCurrentLane currentLane, float side, bool desperate, uint frame,
            out float oncomingClearAhead, out OncomingReason reason, out float nearestOffset)
        {
            oncomingClearAhead = 0f;
            reason = OncomingReason.NoLane;
            nearestOffset = -1f;
            Entity lane = currentLane.m_Lane;
            if (!EntityManager.HasComponent<Curve>(lane) ||
                !EntityManager.HasComponent<Game.Net.CarLane>(lane) ||
                !EntityManager.HasComponent<Owner>(lane))
            {
                return 0;
            }
            bool myInvert = (EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            Owner owner = EntityManager.GetComponentData<Owner>(lane);
            // Only on road segments - intersections have their own geometry.
            if (!EntityManager.HasComponent<Game.Net.Edge>(owner.m_Owner) ||
                !EntityManager.HasBuffer<Game.Net.SubLane>(owner.m_Owner))
            {
                return 0;
            }

            float t = currentLane.m_CurvePosition.x;
            Curve myCurve = EntityManager.GetComponentData<Curve>(lane);
            float3 myPos = MathUtils.Position(myCurve.m_Bezier, t);
            float2 myTangent = math.normalizesafe(MathUtils.Tangent(myCurve.m_Bezier, t).xz);
            bool inverted = currentLane.m_CurvePosition.z < currentLane.m_CurvePosition.x;
            float2 travelDir = inverted ? -myTangent : myTangent;
            float2 corridorDir = -side * MathUtils.Right(travelDir);

            // How far the vehicle's BODY is physically toward the oncoming side right now
            // (lags the target offset when merging). Used both for the hysteresis and for
            // deciding how long oncoming traffic must keep waiting.
            float physicalOffset = 0f;
            if (EntityManager.HasComponent<Transform>(vehicle))
            {
                float3 vehiclePos = EntityManager.GetComponentData<Transform>(vehicle).m_Position;
                physicalOffset = math.dot(vehiclePos.xz - myPos.xz, corridorDir);
            }

            uint myGroup = 0;
            bool grouped = EntityManager.HasComponent<SlaveLane>(lane);
            if (grouped)
            {
                myGroup = EntityManager.GetComponentData<SlaveLane>(lane).m_Group;
            }

            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner.m_Owner, isReadOnly: true);
            Entity bestLane = Entity.Null;
            Curve bestCurve = default;
            float bestS = 0f;
            float bestOffset = float.MaxValue;
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity candidate = subLanes[i].m_SubLane;
                if (candidate == lane ||
                    (subLanes[i].m_PathMethods & PathMethod.Road) == 0 ||
                    !EntityManager.HasComponent<Game.Net.CarLane>(candidate) ||
                    EntityManager.HasComponent<MasterLane>(candidate) ||
                    !EntityManager.HasComponent<Curve>(candidate))
                {
                    continue;
                }
                // Skip lanes of the vehicle's own direction group - those are handled by
                // the overtake lane change, not by crossing the center line.
                if (grouped && EntityManager.HasComponent<SlaveLane>(candidate) &&
                    EntityManager.GetComponentData<SlaveLane>(candidate).m_Group == myGroup)
                {
                    continue;
                }
                // Only genuine oncoming lanes (opposite travel direction on the edge) -
                // not bus lanes or turn pockets of the own direction.
                if (((EntityManager.GetComponentData<Game.Net.CarLane>(candidate).m_Flags & Game.Net.CarLaneFlags.Invert) != 0) == myInvert)
                {
                    continue;
                }
                Curve candidateCurve = EntityManager.GetComponentData<Curve>(candidate);
                float3 posForward = MathUtils.Position(candidateCurve.m_Bezier, t);
                float3 posReverse = MathUtils.Position(candidateCurve.m_Bezier, 1f - t);
                bool useForward = math.distancesq(posForward.xz, myPos.xz) <= math.distancesq(posReverse.xz, myPos.xz);
                float s = useForward ? t : 1f - t;
                float3 candidatePos = useForward ? posForward : posReverse;
                float offset = math.dot(candidatePos.xz - myPos.xz, corridorDir);
                // Report the nearest oncoming lane REGARDLESS of the window, so a rejection can be
                // read: a value just over kOncomingMaxOffset means the window is what stopped us
                // (typically the responder is a lane or two in from the centre line, or there is a
                // median), while -1 means the road genuinely has no oncoming side here.
                if (offset >= kOncomingMinOffset && (nearestOffset < 0f || offset < nearestOffset))
                {
                    nearestOffset = offset;
                }
                if (offset < kOncomingMinOffset || offset > kOncomingMaxOffset)
                {
                    continue;
                }
                if (offset < bestOffset)
                {
                    bestOffset = offset;
                    bestLane = candidate;
                    bestCurve = candidateCurve;
                    bestS = s;
                }
            }
            if (bestLane == Entity.Null)
            {
                reason = OncomingReason.NoLane;
                return 0;
            }

            // Distance to the nearest oncoming vehicle ahead on that lane.
            float nearestAhead = float.MaxValue;
            float length = math.max(1f, bestCurve.m_Length);
            float paramSign = math.dot(math.normalizesafe(MathUtils.Tangent(bestCurve.m_Bezier, bestS).xz), travelDir) < 0f ? -1f : 1f;
            if (EntityManager.HasBuffer<LaneObject>(bestLane))
            {
                DynamicBuffer<LaneObject> laneObjects = EntityManager.GetBuffer<LaneObject>(bestLane, isReadOnly: true);
                for (int i = 0; i < laneObjects.Length; i++)
                {
                    float2 range = laneObjects[i].m_CurvePosition;
                    float distanceAhead = paramSign > 0f
                        ? (math.min(range.x, range.y) - bestS) * length
                        : (bestS - math.max(range.x, range.y)) * length;
                    if (distanceAhead > -6f)
                    {
                        nearestAhead = math.min(nearestAhead, distanceAhead);
                    }
                }
            }

            // Position-based hysteresis so the vehicle does not dart out and back: to START
            // using the oncoming lane there must be a sizeable gap (~75 m); once it is out
            // there it only merges back when a vehicle gets within ~50 m. Between the two it
            // stays where it is. If there is no room to start, it does not touch the oncoming
            // lane at all and waits in-lane (normal corridor + squeeze) instead of hopping.
            // Sticky commitment (anti-wobble): the vehicle counts as committed to the
            // oncoming lane if its body is already out there OR it committed recently. While
            // committed it only merges back at the close (50 m) sight; to newly commit it
            // needs a big (75 m) gap. This stops the lateral target from flip-flopping every
            // tick when an oncoming car hovers around the commit distance.
            // "Reason to be out here": there is still a queue to overtake on the own lane.
            // LaneVehiclesAhead counts the vehicle itself too, so <= 1 means it has passed
            // everything and must merge back - otherwise it cruises the oncoming lane
            // forever on an empty road (the "driving in circles" bug).
            int ownAhead = m_Geometry.LaneVehiclesAhead(lane, t, inverted);
            m_StuckStates.TryGetValue(vehicle, out StuckState oncomingStuck);
            bool onOncoming = physicalOffset > kOncomingClearMargin;
            bool committed = onOncoming || frame < oncomingStuck.m_OncomingActiveUntil;

            // Stall watch: a committed pass that gains no ground is going NOWHERE - the
            // classic case is a red/blocked junction right ahead: the own-lane queue never
            // clears (ownAhead stays high, so softMerge never fires), the oncoming lane is
            // empty (no hardMerge), and the vehicle bounces between its 10 m/s pass floor
            // and the junction stop line for minutes, visibly twisting on the wrong
            // carriageway. Progress is measured along the LANE (myPos), so the lateral
            // swinging itself never counts as movement.
            bool stallMerge = false;
            if (committed)
            {
                if (oncomingStuck.m_OncSampleFrame == 0u ||
                    math.distancesq(myPos.xz, oncomingStuck.m_OncSamplePos.xz) >= kOncomingStallMeters * kOncomingStallMeters)
                {
                    oncomingStuck.m_OncSampleFrame = math.max(frame, 1u);
                    oncomingStuck.m_OncSamplePos = myPos;
                }
                else if (frame - oncomingStuck.m_OncSampleFrame >= kOncomingStallFrames)
                {
                    stallMerge = true;
                }
            }
            else
            {
                oncomingStuck.m_OncSampleFrame = 0u;
            }

            // The ONLY debounced reason to merge back is that the overtake is finished -
            // nothing left ahead on the own lane (ownAhead <= 1). A merely distant oncoming
            // car is NOT a reason to abort: that made the vehicle dart out and back every
            // time a held car sat within 50 m (the strong wobble). Such cars are instead
            // held (below) and approached at a crawl. Only a genuinely close oncoming car
            // (<25 m, hardMerge) forces an immediate merge for safety. Queue gaps make
            // ownAhead flicker to <=1 for a moment, so the merge is still debounced.
            bool softWant = ownAhead <= 1;
            if (softWant)
            {
                if (oncomingStuck.m_MergeWantSince == 0u)
                {
                    oncomingStuck.m_MergeWantSince = math.max(frame, 1u);
                }
            }
            else
            {
                oncomingStuck.m_MergeWantSince = 0u;
            }
            bool softMerge = oncomingStuck.m_MergeWantSince != 0u &&
                             frame - oncomingStuck.m_MergeWantSince >= kMergeDebounceFrames;
            bool hardMerge = nearestAhead < kOncomingHardMergeSight;

            bool mergeBack;
            if (committed)
            {
                mergeBack = hardMerge || softMerge || stallMerge;
                oncomingStuck.m_OncomingActiveUntil = mergeBack ? 0u : frame + kOncomingStickyFrames;
                if (stallMerge && frame >= oncomingStuck.m_OncBlockUntil)
                {
                    // Do not dart straight back out: the situation that stalled the pass is
                    // still there. Block new commits for a while so the normal machinery
                    // (corridor, green petition, squeeze) gets a turn at the junction.
                    oncomingStuck.m_OncBlockUntil = frame + kOncomingRetryBlockFrames;
                    if (Mod.Setting != null && Mod.Setting.VerboseLogging)
                    {
                        Mod.Log.Info($"[oncoming] veh={vehicle.Index} pass stalled (no ground gained for {kOncomingStallFrames}f) - merging back, retry blocked {kOncomingRetryBlockFrames}f");
                    }
                }
            }
            else
            {
                // A DESPERATE responder commits on a much smaller gap. This is the rule
                // kDesperateFrames has described from the start - "stuck this long => cross over
                // even against oncoming traffic" - and it was never implemented: the flag was
                // passed into this method and the body ignored it, so a responder standing for a
                // minute needed the same 75 m of clear road as one that had just started evading.
                // In city traffic that gap does not come, which is why the crossover essentially
                // never happened (veh 2350571: onc=0 in every log line, on a SINGLE-LANE road
                // where the oncoming side is the only way past a stopped truck at all).
                // Still well above kOncomingHardMergeSight, so it cannot commit into a car it
                // would have to merge away from on the next tick.
                float commitSight = desperate ? kOncomingDesperateCommitSight : kOncomingCommitSight;
                if (frame < oncomingStuck.m_OncBlockUntil)
                {
                    m_StuckStates[vehicle] = oncomingStuck;
                    reason = OncomingReason.Blocked;
                    return 0; // a stalled pass merged back here recently - let the normal machinery try
                }
                if (nearestAhead < commitSight)
                {
                    m_StuckStates[vehicle] = oncomingStuck;
                    reason = OncomingReason.Gap;
                    oncomingClearAhead = nearestAhead;
                    return 0; // no room to commit; wait in-lane
                }
                oncomingStuck.m_OncomingActiveUntil = frame + kOncomingStickyFrames;
                mergeBack = false;
            }
            m_StuckStates[vehicle] = oncomingStuck;

            // Committed pass: fully onto the oncoming lane. Merging: retreat all the way
            // back to the own lane so the vehicle can slot into the gap that opens there.
            float targetMeters = mergeBack ? 0f : bestOffset;

            // Convert meters to m_LanePosition units of the vehicle's own lane.
            float slack = m_Ctx.PrefabGeometry.LateralSlack(vehicle, lane);
            float targetPosition = -side * (targetMeters / slack);
            // Retreat quickly when merging, ease out gently when going over.
            float rate = mergeBack ? kPullRate * 3f : kPullRate * 1.5f;
            currentLane.m_LanePosition = math.lerp(currentLane.m_LanePosition, targetPosition, rate);

            // Oncoming vehicles cannot "see" the laterally displaced emergency vehicle (it
            // is not a LaneObject on their lane), so hold them near it while its BODY is
            // still on the oncoming side. Using the physical offset (not the target) means
            // they keep waiting until the vehicle is at least halfway back into its own
            // lane, instead of resuming into a vehicle still crossing over.
            if (physicalOffset > kOncomingClearMargin &&
                EntityManager.HasBuffer<LaneObject>(bestLane))
            {
                DynamicBuffer<LaneObject> oncoming = EntityManager.GetBuffer<LaneObject>(bestLane, isReadOnly: true);
                for (int i = 0; i < oncoming.Length; i++)
                {
                    float2 range = oncoming[i].m_CurvePosition;
                    float ahead = paramSign > 0f
                        ? (math.min(range.x, range.y) - bestS) * length
                        : (bestS - math.max(range.x, range.y)) * length;
                    if (ahead > -6f && ahead < kOncomingMergeSight)
                    {
                        m_Control.SetCeiling(oncoming[i].m_LaneObject, kHoldSpeed);
                    }
                }
            }
            // Report how clear the oncoming lane is ahead so the caller can grant pass speed
            // only on a genuinely free lane and crawl up to a close (held) car otherwise.
            oncomingClearAhead = nearestAhead;
            reason = OncomingReason.Active;
            return mergeBack ? 1 : 2;
        }
    }
}

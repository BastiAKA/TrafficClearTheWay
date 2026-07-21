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

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding
{
    /// <summary>
    /// Moving the responder to a DIFFERENT same-direction lane, and spotting the turn its route
    /// requires.
    ///
    /// Only ever SETS m_ChangeLane and lets the game perform the change - writing m_ChangeLane to
    /// null from outside corrupts the lane-object registry and hard-crashes a Burst job.
    ///
    /// A change is worth it only in slow traffic and only onto a lane significantly freer than
    /// the current one, or one the route actually needs. A genuinely stuck responder is allowed a
    /// much smaller slot, because the normal clearance rule never passes in a packed jam - which
    /// is exactly where a lane takeover is needed.
    /// </summary>
    internal sealed class LaneChange
    {
        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = false;

        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private Game.City.CityConfigurationSystem m_CityConfigurationSystem => m_Ctx.CityConfiguration;
        private M_LaneGeometry m_Geometry => m_Ctx.Geometry;
        private VehicleControl m_Control => m_Ctx.VehicleControl;
        private CorridorBuilder m_Corridor => m_Ctx.Corridor;
        private Dictionary<Entity, StuckState> m_StuckStates => m_Ctx.States.Stuck;
        private Dictionary<Entity, uint> m_ForcedChanges => m_Ctx.States.ForcedChanges;
        public LaneChange(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// When stuck, change onto an adjacent parallel lane of the same road that is free
        /// around the vehicle - passing the queue is better than squeezing through it, even
        /// if the vehicle has to merge back later (the corridor logic then clears the merge
        /// target, and its high lane-reservation priority makes traffic let it back in).
        /// Initiation mirrors the vanilla lane-change semantics from CarLaneSelectIterator;
        /// FixedLane pins the choice so the next optimal-lane pass cannot revert it, and is
        /// released again once the change finished.
        /// </summary>
        /// <summary>
        /// Direction of the first turn coming up on the vehicle's path within the corridor
        /// distance: -1 = left, +1 = right, 0 = none. Lets a stuck emergency vehicle move
        /// into the lane it needs for its turn even against traffic, so it does not starve
        /// in a straight lane while its route wants to turn.
        /// </summary>
        public int GetUpcomingTurn(Entity vehicle, out float distance)
        {
            distance = 0f;
            if (!EntityManager.HasBuffer<CarNavigationLane>(vehicle))
            {
                return 0;
            }
            DynamicBuffer<CarNavigationLane> navLanes = EntityManager.GetBuffer<CarNavigationLane>(vehicle, isReadOnly: true);
            for (int i = 0; i < navLanes.Length; i++)
            {
                CarNavigationLane navLane = navLanes[i];
                CarLaneFlags flags = navLane.m_Flags;
                if ((flags & CarLaneFlags.TurnLeft) != 0)
                {
                    return -1;   // distance = accumulated length of the lanes before the turn
                }
                if ((flags & CarLaneFlags.TurnRight) != 0)
                {
                    return 1;
                }
                if ((flags & (CarLaneFlags.EndOfPath | CarLaneFlags.EndReached)) != 0)
                {
                    break;
                }
                if (EntityManager.HasComponent<Curve>(navLane.m_Lane))
                {
                    float len = EntityManager.GetComponentData<Curve>(navLane.m_Lane).m_Length;
                    distance += len * math.abs(navLane.m_CurvePosition.y - navLane.m_CurvePosition.x);
                }
            }
            return 0;
        }

        public bool TryOvertakeLaneChange(Entity vehicle, ref CarCurrentLane currentLane, uint frame, int turnHint, bool relaxDensity = false, float preferDir = 0f)
        {
            using (ModProfiler.Sample(kProfile, "LaneChange"))
            {
                return TryOvertakeLaneChangeImpl(vehicle, ref currentLane, frame, turnHint, relaxDensity, preferDir);
            }
        }

        private bool TryOvertakeLaneChangeImpl(Entity vehicle, ref CarCurrentLane currentLane, uint frame, int turnHint, bool relaxDensity = false, float preferDir = 0f)
        {
            if (currentLane.m_ChangeLane != Entity.Null ||
                (currentLane.m_LaneFlags & (CarLaneFlags.FixedLane | CarLaneFlags.Roundabout | CarLaneFlags.ParkingSpace | CarLaneFlags.Area | CarLaneFlags.TransformTarget | CarLaneFlags.EndReached | CarLaneFlags.EndOfPath)) != 0)
            {
                return false;
            }
            if (!EntityManager.HasComponent<SlaveLane>(currentLane.m_Lane) ||
                !EntityManager.HasComponent<Owner>(currentLane.m_Lane) ||
                !EntityManager.HasComponent<Curve>(currentLane.m_Lane))
            {
                return false;
            }

            Curve curve = EntityManager.GetComponentData<Curve>(currentLane.m_Lane);
            bool inverted = currentLane.m_CurvePosition.z < currentLane.m_CurvePosition.x;
            if (curve.m_Length * math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x) < kOvertakeMinRemaining)
            {
                return false;
            }

            SlaveLane slaveLane = EntityManager.GetComponentData<SlaveLane>(currentLane.m_Lane);
            Owner owner = EntityManager.GetComponentData<Owner>(currentLane.m_Lane);
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner.m_Owner))
            {
                return false;
            }
            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner.m_Owner, isReadOnly: true);
            int maxIndex = math.min(slaveLane.m_MaxIndex, subLanes.Length - 1);
            int myIndex = -1;
            for (int i = slaveLane.m_MinIndex; i <= maxIndex; i++)
            {
                if (subLanes[i].m_SubLane == currentLane.m_Lane)
                {
                    myIndex = i;
                    break;
                }
            }
            if (myIndex < 0)
            {
                return false;
            }

            bool laneInverted = (EntityManager.GetComponentData<Game.Net.CarLane>(currentLane.m_Lane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            Car car = EntityManager.GetComponentData<Car>(vehicle);
            int currentDensity = m_Geometry.LaneVehiclesAhead(currentLane.m_Lane, currentLane.m_CurvePosition.x, inverted);

            // leftIndex/rightIndex are the physically left/right neighbour sublanes.
            int leftIndex = laneInverted ? myIndex + 1 : myIndex - 1;
            int rightIndex = laneInverted ? myIndex - 1 : myIndex + 1;
            int first, second;
            if (turnHint != 0)
            {
                // A turn is coming up: head for the lane on the turn side first, so the
                // vehicle reaches its turn lane through the queue instead of starving in a
                // straight lane. That lane is "necessary for the goal" - density is ignored.
                first = turnHint < 0 ? leftIndex : rightIndex;
                second = turnHint < 0 ? rightIndex : leftIndex;
            }
            else if (preferDir != 0f)
            {
                // A lane on this side is empty ahead (physical direction, from the corridor
                // build). Head that way first - one lane per attempt, so a free lane two over is
                // reached in steps. Density is relaxed by the caller, not ignored: the point is
                // that the target really is emptier, and the clear-around check still applies.
                first = preferDir < 0f ? leftIndex : rightIndex;
                second = preferDir < 0f ? rightIndex : leftIndex;
            }
            else
            {
                // Otherwise prefer the corridor side (left in right-hand traffic).
                first = m_CityConfigurationSystem.leftHandTraffic ? rightIndex : leftIndex;
                second = m_CityConfigurationSystem.leftHandTraffic ? leftIndex : rightIndex;
            }
            int2 candidateOrder = new int2(first, second);
            for (int c = 0; c < 2; c++)
            {
                int candidateIndex = candidateOrder[c];
                if (candidateIndex < slaveLane.m_MinIndex || candidateIndex > maxIndex)
                {
                    continue;
                }
                // The turn-side lane is required for the route, so it may be entered even if
                // it is not freer; other lanes only when significantly freer.
                bool towardTurn = turnHint != 0 && c == 0;
                Game.Net.SubLane candidate = subLanes[candidateIndex];
                Entity candidateLane = candidate.m_SubLane;
                if ((candidate.m_PathMethods & PathMethod.Road) == 0 ||
                    candidateLane == currentLane.m_Lane ||
                    !EntityManager.HasComponent<Game.Net.CarLane>(candidateLane) ||
                    EntityManager.HasComponent<MasterLane>(candidateLane))
                {
                    continue;
                }
                Game.Net.CarLaneFlags candidateFlags = EntityManager.GetComponentData<Game.Net.CarLane>(candidateLane).m_Flags;
                if ((candidateFlags & Game.Net.CarLaneFlags.Forbidden) != 0)
                {
                    continue;
                }
                if ((candidateFlags & Game.Net.CarLaneFlags.PublicOnly) != 0 && (car.m_Flags & CarFlags.UsePublicTransportLanes) == 0)
                {
                    continue;
                }
                // Only change if this lane is SIGNIFICANTLY freer than the current one, so
                // the maneuver is purposeful and does not oscillate - unless it is the lane
                // the vehicle needs for an upcoming turn. When the responder is genuinely stuck
                // (relaxDensity, set from stuckEscape) the advantage requirement drops: going
                // AROUND into a lane that is merely no-fuller beats sitting boxed in - in a
                // dense jam no lane is ever "kDensityAdvantage freer", which otherwise left the
                // stuck responder with no way out at all. IsLaneClearAround below still guards
                // the immediate vicinity, and the cooldown + FixedLane pin prevent oscillation.
                if (!towardTurn &&
                    m_Geometry.LaneVehiclesAhead(candidateLane, currentLane.m_CurvePosition.x, inverted)
                        + (relaxDensity ? 0 : kDensityAdvantage) > currentDensity)
                {
                    continue;
                }
                // A stuck responder only needs a small opening to commit - the slot-opener
                // widens it after. Otherwise keep the full 30 m rule so ordinary overtakes stay
                // purposeful and do not dart into tight gaps.
                bool clear = relaxDensity
                    ? m_Geometry.IsLaneClearAround(candidateLane, currentLane.m_CurvePosition.x, inverted, kTakeoverClearAhead, kTakeoverClearBehind)
                    : m_Geometry.IsLaneClearAround(candidateLane, currentLane.m_CurvePosition.x, inverted);
                if (!clear)
                {
                    continue;
                }

                currentLane.m_ChangeLane = candidateLane;
                currentLane.m_ChangeProgress = 0f;
                currentLane.m_LaneFlags &= ~(CarLaneFlags.TurnLeft | CarLaneFlags.TurnRight);
                currentLane.m_LaneFlags |= (candidateIndex < myIndex != laneInverted) ? CarLaneFlags.TurnLeft : CarLaneFlags.TurnRight;
                currentLane.m_LaneFlags |= CarLaneFlags.FixedLane;
                m_ForcedChanges[vehicle] = frame;
                return true;
            }
            return false;
        }
    }
}

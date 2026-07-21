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
    /// Where a responder trip actually ENDS, and how it parks once it gets there.
    ///
    /// Distance to a dispatch target is not simply the target transform: for an accident it is
    /// the nearest vehicle still involved in it. Routes that end off the street (a driveway,
    /// parking lot or garage) get extra patience before a street arrival is forced, because
    /// queueing into a drive is slow but usually succeeds.
    ///
    /// Parking is "reach the kerbmost lane": vanilla has no parking search at an incident site,
    /// so the rightmost lane IS the parking. Never on a junction or turn lane - a committed stop
    /// on a curve leaves the vehicle diagonally across the corner.
    /// </summary>
    internal sealed class ArrivalTarget
    {
        private readonly WayClearanceContext modContext;
        private EntityManager EntityManager => modContext.EntityManager;
        private M_LaneGeometry m_Geometry => modContext.Geometry;

        private Dictionary<Entity, uint> m_ForcedChanges => modContext.States.ForcedChanges;
        public ArrivalTarget(WayClearanceContext ctx)
        {
            modContext = ctx;
        }

        /// <summary>
        /// Distance from the vehicle to its dispatch target: the target's Transform, or -
        /// for an AccidentSite target, which has no Transform of its own - the NEAREST
        /// vehicle involved in that site's accident event. float.MaxValue if there is no
        /// usable target position. (Vanilla IsCloseEnough is this &lt;= kArriveAssistRange.)
        /// </summary>
        public float DistanceToDispatchTarget(Entity target, float3 position)
        {
            if (target == Entity.Null || !EntityManager.Exists(target))
            {
                return float.MaxValue;
            }
            if (EntityManager.HasComponent<Transform>(target))
            {
                return math.distance(position,
                    EntityManager.GetComponentData<Transform>(target).m_Position);
            }
            if (EntityManager.HasComponent<Game.Events.AccidentSite>(target))
            {
                Entity accidentEvent = EntityManager.GetComponentData<Game.Events.AccidentSite>(target).m_Event;
                if (accidentEvent != Entity.Null &&
                    EntityManager.HasBuffer<Game.Events.TargetElement>(accidentEvent))
                {
                    DynamicBuffer<Game.Events.TargetElement> involved =
                        EntityManager.GetBuffer<Game.Events.TargetElement>(accidentEvent, isReadOnly: true);
                    float best = float.MaxValue;
                    for (int i = 0; i < involved.Length; i++)
                    {
                        Entity entity = involved[i].m_Entity;
                        if (EntityManager.HasComponent<Game.Events.InvolvedInAccident>(entity) &&
                            EntityManager.HasComponent<Transform>(entity) &&
                            EntityManager.GetComponentData<Game.Events.InvolvedInAccident>(entity).m_Event == accidentEvent)
                        {
                            best = math.min(best, math.distance(position,
                                EntityManager.GetComponentData<Transform>(entity).m_Position));
                        }
                    }
                    return best;
                }
            }
            return float.MaxValue;
        }

        /// <summary>
        /// Does the vehicle's remaining route leave the street - into a driveway
        /// (ConnectionLane), a parking lane/lot or a garage of the target building? Then
        /// the vehicle has a real off-street destination it can reach on its own and a
        /// forced street-side arrival would strand it outside a perfectly good driveway.
        /// Checks the upcoming nav lanes plus the TAIL of the path (the off-street part is
        /// always at the very end).
        /// </summary>
        public bool RouteEndsOffStreet(Entity vehicle)
        {
            if (EntityManager.HasBuffer<CarNavigationLane>(vehicle))
            {
                DynamicBuffer<CarNavigationLane> navLanes = EntityManager.GetBuffer<CarNavigationLane>(vehicle, isReadOnly: true);
                for (int i = 0; i < navLanes.Length; i++)
                {
                    if ((navLanes[i].m_Flags & (CarLaneFlags.ParkingSpace | CarLaneFlags.Area | CarLaneFlags.TransformTarget)) != 0 ||
                        modContext.ArrivalTarget.IsOffStreetLane(navLanes[i].m_Lane))
                    {
                        return true;
                    }
                }
            }
            if (EntityManager.HasComponent<PathOwner>(vehicle) &&
                EntityManager.HasBuffer<PathElement>(vehicle))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
                DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(vehicle, isReadOnly: true);
                for (int i = math.max(pathOwner.m_ElementIndex, path.Length - 8); i < path.Length; i++)
                {
                    if (modContext.ArrivalTarget.IsOffStreetLane(path[i].m_Target))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool IsOffStreetLane(Entity lane)
        {
            return EntityManager.HasComponent<Game.Net.ConnectionLane>(lane) ||
                   EntityManager.HasComponent<Game.Net.ParkingLane>(lane) ||
                   EntityManager.HasComponent<Game.Net.GarageLane>(lane);
        }

        /// <summary>
        /// Forced lane change onto the kerb-side neighbour lane so the arrival stop parks
        /// the vehicle on the OUTERMOST lane instead of straddling a middle lane at an
        /// angle. Same initiation semantics as TryOvertakeLaneChange, but always toward the
        /// kerb, with no density requirement - only a clear-lane safety check. Returns false
        /// when the vehicle is already on the kerbmost lane (single-lane roads have no
        /// SlaveLane at all), the neighbour is unusable, or it is occupied right now (the
        /// caller retries for a while, then parks in place).
        /// </summary>
        public bool TryKerbLaneChange(Entity vehicle, ref CarCurrentLane currentLane, float side, uint frame)
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
            // Enough lane left to physically swing over at creep speed; shorter than the
            // overtake minimum on purpose - this is a low-speed maneuver right at the scene.
            if (curve.m_Length * math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x) < 12f)
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
            // Physically kerb-side neighbour: the right one in right-hand traffic (side>0),
            // the left one in left-hand traffic - mirrored again on an inverted lane.
            int kerbIndex = (side > 0f)
                ? (laneInverted ? myIndex - 1 : myIndex + 1)
                : (laneInverted ? myIndex + 1 : myIndex - 1);
            if (kerbIndex < slaveLane.m_MinIndex || kerbIndex > maxIndex)
            {
                return false; // already the kerbmost lane
            }
            Game.Net.SubLane candidate = subLanes[kerbIndex];
            Entity candidateLane = candidate.m_SubLane;
            if ((candidate.m_PathMethods & PathMethod.Road) == 0 ||
                candidateLane == currentLane.m_Lane ||
                !EntityManager.HasComponent<Game.Net.CarLane>(candidateLane) ||
                EntityManager.HasComponent<MasterLane>(candidateLane))
            {
                return false;
            }
            Game.Net.CarLaneFlags candidateFlags = EntityManager.GetComponentData<Game.Net.CarLane>(candidateLane).m_Flags;
            if ((candidateFlags & Game.Net.CarLaneFlags.Forbidden) != 0)
            {
                return false;
            }
            Car car = EntityManager.GetComponentData<Car>(vehicle);
            if ((candidateFlags & Game.Net.CarLaneFlags.PublicOnly) != 0 && (car.m_Flags & CarFlags.UsePublicTransportLanes) == 0)
            {
                return false;
            }
            if (!m_Geometry.IsLaneClearAround(candidateLane, currentLane.m_CurvePosition.x, inverted))
            {
                return false;
            }

            currentLane.m_ChangeLane = candidateLane;
            currentLane.m_ChangeProgress = 0f;
            currentLane.m_LaneFlags &= ~(CarLaneFlags.TurnLeft | CarLaneFlags.TurnRight);
            currentLane.m_LaneFlags |= (kerbIndex < myIndex != laneInverted) ? CarLaneFlags.TurnLeft : CarLaneFlags.TurnRight;
            currentLane.m_LaneFlags |= CarLaneFlags.FixedLane;
            m_ForcedChanges[vehicle] = frame;
            if (Mod.Setting.VerboseLogging)
            {
                Mod.Log.Info($"[arrive] veh={vehicle.Index} changing onto the kerb lane for the stop");
            }
            return true;
        }
    }
}

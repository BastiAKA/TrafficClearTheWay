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
    /// Two "is anything in the way" tests used to gate the escalation.
    ///
    /// IsEvadeSideBlocked asks whether the side the corridor rule sends the responder to is walled
    /// off (tram median, kerb, no drivable lane). It is an ESCAPE test, not a steering mode -
    /// sitting on the leftmost lane beside the median is the NORMAL corridor situation. It is
    /// evaluated only for genuinely stuck vehicles: checked every tick it misfired on a third of
    /// all responders and let them lane-change through working corridors.
    ///
    /// HasEmergencyAhead gives convoy discipline: with a colleague right ahead on the same lane a
    /// responder queues behind it instead of escalating, otherwise a wave of ambulances all swing
    /// left and fan out across the whole road, blocking both directions and each other.
    /// </summary>
    internal sealed class LaneObstruction
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        public LaneObstruction(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// If the emergency vehicle is crawling behind a car that has already pulled aside
        /// and stopped, mark that blocker as ignorable so the vehicle passes it. The flag
        /// is cleared by the game automatically as soon as the blocker changes.
        /// </summary>
        /// <summary>
        /// True when another emergency vehicle drives or stands within <paramref name="range"/>
        /// metres AHEAD of this one on the same lane - i.e. this vehicle is part of a responder
        /// convoy and should queue behind its colleague instead of escalating around it.
        /// </summary>
        public bool HasEmergencyAhead(Entity vehicle, CarCurrentLane currentLane, float range)
        {
            return HasColleagueAhead(vehicle, currentLane, range, maintenance: false);
        }

        /// <summary>
        /// The recovery-vehicle twin of <see cref="HasEmergencyAhead"/>: another maintenance
        /// vehicle within <paramref name="range"/> metres ahead on the same lane.
        ///
        /// Recovery vehicles need exactly the same discipline and never had it. Each one is a
        /// corridor owner in its own right, so a column of them all escalating at once hugs the
        /// same free lane and pushes the standing colleague in front aside - which PullCarsAside
        /// permits, because its protection only covers a ROLLING maintenance vehicle and in a jam
        /// none of them is rolling. That is the responder fan-out ("~10 responders with
        /// consecutive ids all at lanePos -2.99") with tow trucks instead.
        /// </summary>
        public bool HasMaintenanceAhead(Entity vehicle, CarCurrentLane currentLane, float range)
        {
            return HasColleagueAhead(vehicle, currentLane, range, maintenance: true);
        }

        private bool HasColleagueAhead(Entity vehicle, CarCurrentLane currentLane, float range, bool maintenance)
        {
            if (!Lookups.TryGetBuffer(currentLane.m_Lane, out DynamicBuffer<LaneObject> laneObjects, isReadOnly: true) ||
                !Lookups.TryGetComponent(currentLane.m_Lane, out Curve laneCurve))
            {
                return false;
            }
            float curveLength = math.max(1f, laneCurve.m_Length);
            bool inverted = currentLane.m_CurvePosition.z < currentLane.m_CurvePosition.x;
            for (int i = 0; i < laneObjects.Length; i++)
            {
                // Position-gate FIRST (one multiply, from the buffer's own curve position) and
                // stop once past the window - the buffer is sorted by curve position, so the ~2
                // component lookups below are paid only for the few cars actually in front within
                // range, not for every car on the lane (this runs per slow responder per tick).
                float delta = inverted
                    ? currentLane.m_CurvePosition.x - laneObjects[i].m_CurvePosition.x
                    : laneObjects[i].m_CurvePosition.x - currentLane.m_CurvePosition.x;
                float ahead = delta * curveLength;
                if (inverted)
                {
                    if (ahead > range) continue;   // ahead of the window; closer cars still follow
                    if (ahead <= 0.5f) break;      // reached the vehicle; the rest are behind it
                }
                else
                {
                    if (ahead <= 0.5f) continue;   // behind the vehicle; cars ahead still follow
                    if (ahead > range) break;      // past the window; the rest are further ahead
                }
                Entity other = laneObjects[i].m_LaneObject;
                if (other == vehicle ||
                    !Lookups.HasComponent<Car>(other))
                {
                    continue;
                }
                if (maintenance
                        ? Lookups.HasComponent<Game.Vehicles.MaintenanceVehicle>(other)
                        : (Lookups.GetComponentData<Car>(other).m_Flags & CarFlags.Emergency) != 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when the emergency vehicle's evade side (-side, i.e. the LEFT of travel in
        /// right-hand traffic) has no drivable same-direction road lane next to it: the
        /// vehicle is on the leftmost lane against a tram median / kerb / wall, or the
        /// neighbour there is a tram/bus-only lane, an opposite-direction (oncoming) lane, or
        /// otherwise unusable. In that case the German corridor rule cannot open a gap on the
        /// left and the vehicle should look to the OPEN side instead. Returns false on a
        /// single-lane road (no lane group) so the normal single-lane pass logic is untouched.
        /// </summary>
        public bool IsEvadeSideBlocked(Entity vehicle, CarCurrentLane currentLane, float side)
        {
            Entity lane = currentLane.m_Lane;
            if (!Lookups.TryGetComponent(lane, out SlaveLane slaveLane) ||
                !Lookups.TryGetComponent(lane, out Owner owner) ||
                !Lookups.TryGetComponent(lane, out Game.Net.CarLane laneCarLane))
            {
                return false; // no lane group (single-lane road): leave the normal logic alone
            }
            if (!Lookups.TryGetBuffer(owner.m_Owner, out DynamicBuffer<Game.Net.SubLane> subLanes, isReadOnly: true))
            {
                return false;
            }
            int maxIndex = math.min(slaveLane.m_MaxIndex, subLanes.Length - 1);
            int myIndex = -1;
            for (int i = slaveLane.m_MinIndex; i <= maxIndex; i++)
            {
                if (subLanes[i].m_SubLane == lane)
                {
                    myIndex = i;
                    break;
                }
            }
            if (myIndex < 0)
            {
                return false;
            }
            bool laneInverted = (laneCarLane.m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            // Same index arithmetic as AddCounterEvadeNeighbor: the evade-side neighbour.
            bool towardLower = (side > 0f) != laneInverted;
            int evadeIndex = towardLower ? myIndex - 1 : myIndex + 1;
            if (evadeIndex < slaveLane.m_MinIndex || evadeIndex > maxIndex)
            {
                return true; // leftmost lane - median / kerb / wall on the evade side
            }
            Game.Net.SubLane neighbor = subLanes[evadeIndex];
            Entity neighborLane = neighbor.m_SubLane;
            if ((neighbor.m_PathMethods & PathMethod.Road) == 0 ||
                !Lookups.TryGetComponent(neighborLane, out Game.Net.CarLane neighborLaneCarLane) ||
                Lookups.HasComponent<MasterLane>(neighborLane))
            {
                return true;
            }
            Game.Net.CarLaneFlags neighborFlags = neighborLaneCarLane.m_Flags;
            // Opposite-direction neighbour = the oncoming carriageway (handled separately by
            // the oncoming logic), not a same-side corridor lane.
            if (((neighborFlags & Game.Net.CarLaneFlags.Invert) != 0) != laneInverted)
            {
                return true;
            }
            if ((neighborFlags & Game.Net.CarLaneFlags.Forbidden) != 0)
            {
                return true;
            }
            if ((neighborFlags & Game.Net.CarLaneFlags.PublicOnly) != 0 &&
                (Lookups.GetComponentData<Car>(vehicle).m_Flags & CarFlags.UsePublicTransportLanes) == 0)
            {
                return true; // tram / bus-only lane the responder may not use
            }
            return false;
        }
    }
}

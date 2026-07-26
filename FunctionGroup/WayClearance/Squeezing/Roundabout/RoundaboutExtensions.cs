using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;

namespace ClearTheWay.FunctionGroup.WayClearance.Squeezing.Roundabout
{
    /// <summary>
    /// Everything the corridor does differently on a ROUNDABOUT, both halves of it.
    ///
    /// A ring is the mirror image of the normal rule. On a road the queue parts around a gap and
    /// the responder threads the seam; on a ring there is no left to pull over to, so instead
    /// every ring car is swept toward the CENTRE and the responder keeps the outer edge - which
    /// is also the edge it needs to be on to leave at its exit.
    ///
    /// Both halves belong together: the ring sweep is pointless if the responder does not take
    /// the outer lane, and the outer-edge hug would push it into the ring traffic if the sweep
    /// had not moved it inward first. That is why the hug fires only while the sweep is actually
    /// pushing cars.
    /// </summary>
    internal sealed class RoundaboutExtensions
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        public RoundaboutExtensions(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Does this lane belong to a roundabout ring?</summary>
        public static bool IsRing(CarLaneFlags laneFlags)
        {
            return (laneFlags & CarLaneFlags.Roundabout) != 0;
        }

        /// <summary>
        /// Roundabout corridor: instead of opening a gap in the middle of the ring, sweep
        /// EVERY same-direction ring lane toward the centre (-side) and leave the outermost
        /// lane free for the responder, which hugs the outer edge (+side) itself. Returns
        /// false for single-lane rings so the caller falls back to the normal edge squeeze.
        /// The caller has already verified the lane carries the Roundabout flag.
        /// </summary>
        public bool TryAddRing(Entity lane, float minPos, float side, bool inverted, float startOffset)
        {
            if (!EntityManager.HasComponent<Game.Net.CarLane>(lane) ||
                !EntityManager.HasComponent<Owner>(lane))
            {
                return false;
            }
            Owner owner = EntityManager.GetComponentData<Owner>(lane);
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner.m_Owner))
            {
                return false;
            }
            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner.m_Owner, isReadOnly: true);
            bool laneInverted = (EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;

            // Count the concentric ring car lanes going the same way as the responder. A
            // single-lane ring has nothing to clear inward.
            int ringLanes = 0;
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity sl = subLanes[i].m_SubLane;
                if (!EntityManager.HasComponent<Game.Net.CarLane>(sl) ||
                    EntityManager.HasComponent<MasterLane>(sl))
                {
                    continue;
                }
                if (((EntityManager.GetComponentData<Game.Net.CarLane>(sl).m_Flags & Game.Net.CarLaneFlags.Invert) != 0) != laneInverted)
                {
                    continue;
                }
                ringLanes++;
            }
            if (ringLanes < 2)
            {
                return false;
            }

            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity sl = subLanes[i].m_SubLane;
                if (!EntityManager.HasComponent<Game.Net.CarLane>(sl) ||
                    EntityManager.HasComponent<MasterLane>(sl))
                {
                    continue;
                }
                if (((EntityManager.GetComponentData<Game.Net.CarLane>(sl).m_Flags & Game.Net.CarLaneFlags.Invert) != 0) != laneInverted)
                {
                    continue;
                }
                // Push every ring car toward the centre; the outer edge stays free.
                m_Ctx.Corridor.AddCorridorLane(sl, minPos, -side, inverted, startOffset, onPath: true);
            }
            return true;
        }

        /// <summary>
        /// The steering half: keep the responder on the OUTER edge of the ring (+side, i.e. the
        /// right of travel in right-hand traffic), where <see cref="TryAddRing"/> just made room.
        ///
        /// This needs its own branch next to the normal corridor hug because canManeuver is false
        /// on ring lanes - the maneuver flags are suppressed there - and it only fires while a
        /// multi-lane ring is genuinely being cleared (pushed &gt; 0), never on a single-lane ring
        /// where there is nowhere for the traffic to go.
        /// </summary>
        public void HugOuterEdge(ref EscalationState s, ref CarCurrentLane currentLane, Setting setting, Entity vehicle, float side)
        {
            if (!setting.ClearRoundaboutInner || !IsRing(currentLane.m_LaneFlags) ||
                s.m_OncomingState != 0 || s.m_Pushed <= 0 || s.m_NearArrivalTarget ||
                !EntityManager.HasComponent<Game.Net.CarLane>(currentLane.m_Lane))
            {
                return;
            }
            float rMeters = s.m_Evade >= EvadeStage.Hard ? kEvadeMeters : kEdgeMeters;
            float rUnits = math.min(rMeters, m_Ctx.PrefabGeometry.MaxLateralMeters(vehicle)) / m_Ctx.PrefabGeometry.LateralSlack(vehicle, currentLane.m_Lane);
            float rTarget = side * rUnits; // + = outer edge of the ring
            float rNewPos = math.lerp(currentLane.m_LanePosition, rTarget, s.m_Evade >= EvadeStage.Hard ? kPullRate * 1.5f : kPullRate);
            s.m_LateralSteered = true;
            if (math.abs(rNewPos - currentLane.m_LanePosition) > 0.001f)
            {
                currentLane.m_LanePosition = rNewPos;
                s.m_Changed = true;
            }
        }
    }
}

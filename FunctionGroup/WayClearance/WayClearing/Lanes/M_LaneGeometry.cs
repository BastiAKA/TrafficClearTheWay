using Colossal.Mathematics;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding
{
    /// <summary>
    /// Read-only geometry and occupancy questions about lanes and vehicles: how wide they are,
    /// how much lateral room a vehicle has, how far apart two vehicles actually sit, how busy a
    /// lane is ahead, and where a written m_LanePosition offset lands in world space.
    ///
    /// These are pure queries - nothing here writes to the world - and every corridor, overtake,
    /// oncoming and arrival pass is built on them, which is why they live in their own class.
    ///
    /// Lane and vehicle widths are cached BY PREFAB (a prefab's width never changes), because
    /// GetLateralSlack used to do up to 8 component lookups on every call and the corridor pass
    /// calls it once per pushed car - a big share of the per-tick cost with dozens of cars per
    /// responder.
    /// </summary>
    internal sealed class M_LaneGeometry
    {
        // No kProfile switch here on purpose: these are leaf queries called thousands of
        // times a tick, so timing each call would cost more than it measures. Their cost
        // belongs to - and shows up in - whichever pass is calling them.
        private readonly EntityManager EntityManager;
        private readonly Dictionary<Entity, float> m_LaneWidthCache = new Dictionary<Entity, float>();     // lane prefab -> width (constant, permanent cache)
        private readonly Dictionary<Entity, float> m_VehicleWidthCache = new Dictionary<Entity, float>();  // vehicle prefab -> width (constant, permanent cache)

        public M_LaneGeometry(EntityManager entityManager)
        {
            EntityManager = entityManager;
        }
        /// <summary>
        /// Number of vehicles ahead of the given curve position on a lane within the
        /// density window - a cheap occupancy measure to compare lanes.
        /// </summary>
        /// <param name="blockingBelowSpeed">When &gt; 0, only vehicles SLOWER than this (m/s) are
        /// counted. A lane whose traffic is rolling is not an obstruction - it is a lane you can
        /// join - whereas standing cars are. The "is that lane free?" test needs this: with every
        /// object counted, our own green light made the queue pull away and the count churned, so
        /// the free-lane decision flipped from tick to tick (field case 1901774). Default 0 keeps
        /// the plain head-count for the density callers that want exactly that.</param>
        public int LaneVehiclesAhead(Entity lane, float curvePosition, bool inverted, float blockingBelowSpeed = 0f, float windowMeters = kDensityWindow)
        {
            if (!EntityManager.HasComponent<Curve>(lane) || !EntityManager.HasBuffer<LaneObject>(lane))
            {
                return 0;
            }
            float length = math.max(1f, EntityManager.GetComponentData<Curve>(lane).m_Length);
            DynamicBuffer<LaneObject> laneObjects = EntityManager.GetBuffer<LaneObject>(lane, isReadOnly: true);
            int count = 0;
            for (int i = 0; i < laneObjects.Length; i++)
            {
                float pos = laneObjects[i].m_CurvePosition.x;
                float ahead = (inverted ? curvePosition - pos : pos - curvePosition) * length;
                if (ahead > -3f && ahead < windowMeters)
                {
                    if (blockingBelowSpeed > 0f)
                    {
                        Entity obj = laneObjects[i].m_LaneObject;
                        if (EntityManager.HasComponent<Moving>(obj) &&
                            math.length(EntityManager.GetComponentData<Moving>(obj).m_Velocity) >= blockingBelowSpeed)
                        {
                            continue; // rolling along - joining this lane is fine
                        }
                    }
                    count++;
                }
            }
            return count;
        }

        public bool IsLaneClearAround(Entity lane, float curvePosition, bool inverted,
            float clearAhead = kOvertakeClearAhead, float clearBehind = kOvertakeClearBehind)
        {
            if (!EntityManager.HasComponent<Curve>(lane))
            {
                return false;
            }
            float length = math.max(1f, EntityManager.GetComponentData<Curve>(lane).m_Length);
            if (!EntityManager.HasBuffer<LaneObject>(lane))
            {
                return true;
            }
            DynamicBuffer<LaneObject> laneObjects = EntityManager.GetBuffer<LaneObject>(lane, isReadOnly: true);
            for (int i = 0; i < laneObjects.Length; i++)
            {
                float2 range = laneObjects[i].m_CurvePosition;
                float2 distance = inverted
                    ? (curvePosition - range) * length
                    : (range - curvePosition) * length;
                float min = math.min(distance.x, distance.y);
                float max = math.max(distance.x, distance.y);
                if (max > -clearBehind && min < clearAhead)
                {
                    return false;
                }
            }
            return true;
        }




        /// <summary>
        /// Actual sideways distance (meters) between the two vehicles, measured across the
        /// lane direction from the real world positions - not from the intended lane
        /// offsets, which a boxed-in car may not have reached yet. Uses the blocker's lane
        /// as axis when it has one, otherwise the emergency vehicle's own lane (trailers,
        /// parked and working vehicles).
        /// </summary>
        public float GetLateralSeparation(Entity vehicle, CarCurrentLane currentLane, Entity blockingEntity)
        {
            if (!EntityManager.HasComponent<Transform>(vehicle) ||
                !EntityManager.HasComponent<Transform>(blockingEntity))
            {
                return 0f;
            }
            Entity axisLane = currentLane.m_Lane;
            float axisPosition = currentLane.m_CurvePosition.x;
            if (EntityManager.HasComponent<CarCurrentLane>(blockingEntity))
            {
                CarCurrentLane blockerLane = EntityManager.GetComponentData<CarCurrentLane>(blockingEntity);
                if (EntityManager.HasComponent<Curve>(blockerLane.m_Lane))
                {
                    axisLane = blockerLane.m_Lane;
                    axisPosition = blockerLane.m_CurvePosition.x;
                }
            }
            if (!EntityManager.HasComponent<Curve>(axisLane))
            {
                return 0f;
            }
            Curve curve = EntityManager.GetComponentData<Curve>(axisLane);
            float2 tangent = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, axisPosition).xz);
            float2 right = MathUtils.Right(tangent);
            float3 vehiclePos = EntityManager.GetComponentData<Transform>(vehicle).m_Position;
            float3 blockerPos = EntityManager.GetComponentData<Transform>(blockingEntity).m_Position;
            return math.abs(math.dot(blockerPos.xz - vehiclePos.xz, right));
        }


        /// <summary>
        /// Lateral separation (m) between the emergency vehicle and its current Blocker, or
        /// a large value when there is no blocker.
        /// </summary>
        public float GetBlockerSeparation(Entity vehicle, CarCurrentLane currentLane)
        {
            Blocker blocker = EntityManager.GetComponentData<Blocker>(vehicle);
            if (blocker.m_Blocker == Entity.Null || !EntityManager.Exists(blocker.m_Blocker) ||
                !EntityManager.HasComponent<Transform>(blocker.m_Blocker))
            {
                return float.MaxValue;
            }
            return GetLateralSeparation(vehicle, currentLane, blocker.m_Blocker);
        }
    }
}

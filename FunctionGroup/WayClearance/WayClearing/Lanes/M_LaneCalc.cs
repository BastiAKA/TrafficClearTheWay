using System.Collections.Generic;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes.Geometry;
using Colossal.Mathematics;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding
{
    /// <summary>
    /// Everything about CarCurrentLane.m_LanePosition - the lateral offset that is this mod's
    /// main steering lever.
    ///
    /// The value is NOT metres. It is a fraction of the lane's lateral slack (lane width minus
    /// vehicle width), positive to the RIGHT of travel, and the game does not clamp it - beyond
    /// about +-0.5 a vehicle is physically on the pavement or the oncoming side. Every pass that
    /// wants to move a vehicle "1.7 m aside" has to convert through the slack here, which is why
    /// the conversion lives in one place.
    ///
    /// The measurements it converts through - lane width, vehicle width, and the slack between
    /// them - are taken and cached by <see cref="GeometryProvider"/>; this class is only about
    /// what m_LanePosition MEANS.
    /// </summary>
    internal sealed class M_LanePosition
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private GeometryProvider m_Geometry => m_Ctx.PrefabGeometry;

        public M_LanePosition(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// World-space point that realizes the vehicle's written m_LanePosition offset a
        /// few meters ahead on its lane, used to actively steer stuck vehicles sideways.
        /// </summary>
        public bool TryGetLateralTarget(Entity entity, CarCurrentLane lane, float forwardMeters, out float3 target, out quaternion rotation)
        {
            target = default;
            rotation = quaternion.identity;
            if (!EntityManager.HasComponent<Curve>(lane.m_Lane) ||
                !EntityManager.HasComponent<Transform>(entity))
            {
                return false;
            }
            Curve curve = EntityManager.GetComponentData<Curve>(lane.m_Lane);
            float length = math.max(1f, curve.m_Length);
            bool inverted = lane.m_CurvePosition.z < lane.m_CurvePosition.x;
            float t = math.saturate(lane.m_CurvePosition.x + math.select(forwardMeters, -forwardMeters, inverted) / length);
            float3 center = MathUtils.Position(curve.m_Bezier, t);
            float2 tangent = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, t).xz);
            float2 right = MathUtils.Right(tangent);
            // m_LanePosition is stored in the driving frame; flip into curve frame when
            // the lane is traversed backwards (same convention as the game's MoveTarget).
            float curveFrameOffset = math.select(lane.m_LanePosition, 0f - lane.m_LanePosition, inverted) *
                                     m_Geometry.LateralSlack(entity, lane.m_Lane);
            target = center;
            target.xz += right * curveFrameOffset;
            float3 current = EntityManager.GetComponentData<Transform>(entity).m_Position;
            float2 direction = math.normalizesafe(target.xz - current.xz);
            if (math.lengthsq(direction) < 0.01f)
            {
                return false;
            }
            // Clamp the bearing to <=35° off the lane's forward direction. A mostly-lateral
            // target makes the car steer broadside across the road to reach it (the diagonally
            // wedged vans between tram and queue); clamped, it noses forward-diagonally into
            // its offset and converges over the next ticks instead. Distance is capped so a
            // held car never gets a multi-metre forward run out of one override.
            float2 forward = math.select(tangent, -tangent, inverted);
            const float cos35 = 0.8192f;
            const float sin35 = 0.5736f;
            if (math.dot(forward, direction) < cos35)
            {
                float side35 = (forward.x * direction.y - forward.y * direction.x) >= 0f ? sin35 : -sin35;
                direction = new float2(
                    forward.x * cos35 - forward.y * side35,
                    forward.x * side35 + forward.y * cos35);
                float dist = math.min(math.distance(target.xz, current.xz), 3f);
                target.xz = current.xz + direction * dist;
            }
            rotation = quaternion.LookRotationSafe(new float3(direction.x, 0f, direction.y), math.up());
            return true;
        }
    }
}

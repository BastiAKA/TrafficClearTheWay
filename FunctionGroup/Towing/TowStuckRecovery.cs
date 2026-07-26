using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>Where a hauling truck last was, and since when it has not moved.</summary>
    internal struct TruckRest
    {
        public float3 m_Pos;
        public uint m_Since;
    }

    /// <summary>
    /// Getting a tow truck that has driven itself somewhere impossible back onto a road.
    ///
    /// This is the price of the path shield. Clearing PathFlags.Failed stops vanilla deleting a
    /// loaded truck, but a vehicle without a route drives dead straight - and one of them drove
    /// straight into a multi-storey car park and parked itself inside the building, still holding
    /// its wreck. Nothing could reach it there:
    ///  - the release-on-failed-path timer never started, because a vehicle with no path at all
    ///    does not necessarily carry the Failed flag,
    ///  - the orphan sweep deliberately skips any wreck whose controller is a LIVE recovery truck,
    ///    so its 10-minute backstop was disabled by the very truck that was stuck,
    ///  - and repathing cannot help: there is no route from inside a building.
    /// The truck was, in effect, immortal and unreachable.
    ///
    /// So the test here is MOVEMENT, not path state - the same reasoning TowOrphanFinder uses. A
    /// truck that is hauling but has not moved a metre in kTowStuckFrames is not towing, whatever
    /// its components say. It is put back on the nearest road (Sebastian: vanilla does this too),
    /// and only deleted if there is no road anywhere near - better one lost truck than a permanent
    /// monument inside a car park.
    /// </summary>
    internal sealed class TowStuckRecovery
    {
        private readonly TowingContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private Dictionary<Entity, TruckRest> m_Rest => m_Ctx.TruckRest;

        public TowStuckRecovery(TowingContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// True when this hauling truck has been motionless long enough to count as wedged. Call
        /// once per tick per towing truck; it keeps the movement history itself.
        /// </summary>
        public bool IsWedged(Entity truck, float3 pos, uint frame)
        {
            if (!m_Rest.TryGetValue(truck, out TruckRest rest) ||
                math.distancesq(rest.m_Pos.xz, pos.xz) > kTowStuckMeters * kTowStuckMeters)
            {
                m_Rest[truck] = new TruckRest { m_Pos = pos, m_Since = frame };
                return false;
            }
            return frame - rest.m_Since >= kTowStuckFrames;
        }

        public void Forget(Entity truck)
        {
            m_Rest.Remove(truck);
        }

        /// <summary>
        /// Put the truck back on the nearest road and give it a fresh route. Returns false when
        /// there is no usable road in range, which is the caller's signal to give up on it.
        ///
        /// Repositioning a VEHICLE is more than a teleport: unlike a settled wreck it carries a
        /// lane assignment and a path, and moving only its Transform would leave it believing it
        /// is still where it was - it would drive off the new spot exactly as blindly. So the lane
        /// is reassigned with the position, the lateral offset is zeroed, any half-finished lane
        /// change is dropped, and the path is invalidated so the game plots a new one from here.
        /// </summary>
        public bool PutBackOnRoad(Entity truck, float3 pos, NativeQuadTree<Entity, QuadTreeBoundsXZ> netTree,
            Setting setting)
        {
            Entity bestLane = Entity.Null;
            float bestDist = float.MaxValue;
            float bestT = 0f;
            NativeList<Entity> nets = new NativeList<Entity>(Allocator.Temp);
            try
            {
                AreaIterator iterator = new AreaIterator
                {
                    m_Bounds = new Bounds3(pos - kTowReturnSearchRadius, pos + kTowReturnSearchRadius),
                    m_Results = nets
                };
                netTree.Iterate(ref iterator);
                for (int i = 0; i < nets.Length; i++)
                {
                    Entity edge = nets[i];
                    if (!EntityManager.HasComponent<Game.Net.Edge>(edge) ||
                        !EntityManager.HasComponent<Game.Net.Road>(edge) ||
                        !EntityManager.HasBuffer<Game.Net.SubLane>(edge))
                    {
                        continue;
                    }
                    DynamicBuffer<Game.Net.SubLane> subLanes =
                        EntityManager.GetBuffer<Game.Net.SubLane>(edge, isReadOnly: true);
                    for (int j = 0; j < subLanes.Length; j++)
                    {
                        Entity lane = subLanes[j].m_SubLane;
                        // A real driving lane only - not a parking aisle (that is how it got in
                        // here), not a footway, not the master lane of a group.
                        if (!EntityManager.HasComponent<Game.Net.CarLane>(lane) ||
                            EntityManager.HasComponent<Game.Net.MasterLane>(lane) ||
                            !EntityManager.HasComponent<Curve>(lane))
                        {
                            continue;
                        }
                        Curve laneCurve = EntityManager.GetComponentData<Curve>(lane);
                        float dist = MathUtils.Distance(laneCurve.m_Bezier, pos, out float t);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestLane = lane;
                            bestT = t;
                        }
                    }
                }
            }
            finally
            {
                nets.Dispose();
            }
            if (bestLane == Entity.Null)
            {
                return false;
            }

            Curve curve = EntityManager.GetComponentData<Curve>(bestLane);
            float3 target = MathUtils.Position(curve.m_Bezier, bestT);
            float3 tangent = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, bestT));
            Transform transform = EntityManager.GetComponentData<Transform>(truck);
            transform.m_Position = target;
            if (math.lengthsq(tangent) > 0.001f)
            {
                transform.m_Rotation = quaternion.LookRotationSafe(tangent, math.up());
            }
            EntityManager.SetComponentData(truck, transform);

            if (EntityManager.HasComponent<Game.Vehicles.CarCurrentLane>(truck))
            {
                Game.Vehicles.CarCurrentLane lane = EntityManager.GetComponentData<Game.Vehicles.CarCurrentLane>(truck);
                lane.m_Lane = bestLane;
                lane.m_CurvePosition = new float3(bestT, bestT, 1f);
                lane.m_ChangeLane = Entity.Null;
                lane.m_ChangeProgress = 0f;
                lane.m_LanePosition = 0f;
                lane.m_LaneFlags &= ~(CarLaneFlags.EndOfPath | CarLaneFlags.EndReached |
                    CarLaneFlags.FixedLane | CarLaneFlags.IgnoreBlocker);
                EntityManager.SetComponentData(truck, lane);
            }
            if (EntityManager.HasComponent<PathOwner>(truck))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(truck);
                pathOwner.m_State &= ~(PathFlags.Failed | PathFlags.Stuck);
                pathOwner.m_State |= PathFlags.Obsolete; // plot a route from where it actually is now
                EntityManager.SetComponentData(truck, pathOwner);
            }
            EntityManager.AddComponent<Updated>(truck);
            m_Rest.Remove(truck);
            if (setting.VerboseLogging)
            {
                Mod.Log.Info($"[towstuck] truck={truck.Index} was wedged at ({pos.x:F0},{pos.z:F0}) - " +
                    $"put back on the road at ({target.x:F0},{target.z:F0}), {bestDist:F0}m away");
            }
            return true;
        }
    }
}

using System.Collections.Generic;
using Colossal.Collections;
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

namespace ClearTheWay.FunctionGroup.GeneralImprovements
{
    /// <summary>
    /// Physically consolidating a settled wreck onto the outermost lane of its side.
    ///
    /// This is the one measure that addresses the root cause rather than a symptom: the
    /// pathfinder treats a blocked lane stretch as HARD impassable, so a wreck across every lane
    /// makes each destination behind it unreachable and the whole queue evaporates. Moving the
    /// wreck aside keeps at least one lane passable - which is also just what real accident
    /// clearing looks like.
    ///
    /// It only fires on a FULLY blocked carriageway: a wreck on one lane of several is left alone
    /// so traffic simply passes it. Trailers of a crashed rig are dragged along, or they are left
    /// behind still blocking the lane.
    /// </summary>
    internal sealed class WreckClearing
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        public WreckClearing(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Moves a settled wreck onto the outermost car lane of its own side of the road
        /// (aligned with the lane) so the inner lane(s) become pathfind-passable again.
        /// Adds Updated/BatchesUpdated so LaneBlockSystem/LaneObjectSystem/LaneDataSystem
        /// recompute the blockage, and pokes PathfindUpdated on the previously blocked lanes.
        /// </summary>
        public bool TryClearWreckAside(Entity wreck, float3 wreckPos, NativeQuadTree<Entity, QuadTreeBoundsXZ> netTree)
        {
            // Nearest road edge under/next to the wreck.
            NativeList<Entity> nets = new NativeList<Entity>(16, Allocator.Temp);
            Entity bestEdge = Entity.Null;
            float bestDist = 16f;
            float bestT = 0f;
            try
            {
                AreaIterator netIterator = new AreaIterator
                {
                    m_Bounds = new Bounds3(wreckPos - kWreckClearSearchRadius, wreckPos + kWreckClearSearchRadius),
                    m_Results = nets
                };
                netTree.Iterate(ref netIterator);
                for (int i = 0; i < nets.Length; i++)
                {
                    Entity candidate = nets[i];
                    if (!EntityManager.HasComponent<Game.Net.Edge>(candidate) ||
                        !EntityManager.HasComponent<Game.Net.Road>(candidate) ||
                        !EntityManager.HasComponent<Curve>(candidate) ||
                        !EntityManager.HasBuffer<Game.Net.SubLane>(candidate))
                    {
                        continue;
                    }
                    Curve curve = EntityManager.GetComponentData<Curve>(candidate);
                    float dist = MathUtils.Distance(curve.m_Bezier, wreckPos, out float t);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestEdge = candidate;
                        bestT = t;
                    }
                }
            }
            finally
            {
                nets.Dispose();
            }
            if (bestEdge == Entity.Null)
            {
                return false;
            }

            Curve edgeCurve = EntityManager.GetComponentData<Curve>(bestEdge);
            float3 edgePos = MathUtils.Position(edgeCurve.m_Bezier, bestT);
            float2 tangent = math.normalizesafe(MathUtils.Tangent(edgeCurve.m_Bezier, bestT).xz);
            float2 rightDir = MathUtils.Right(tangent);
            // Keep the wreck on its own side of the road (never drag it across the median).
            float side = math.dot(wreckPos.xz - edgePos.xz, rightDir) >= 0f ? 1f : -1f;

            // Outermost proper car lane on that side; also tally how many of that
            // carriageway's car lanes are currently blocked (see the full-block gate below).
            const float kSideLaneMinOffset = 0.2f; // ignore the opposite carriageway / center
            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(bestEdge, isReadOnly: true);
            Entity bestLane = Entity.Null;
            float bestOffset = kSideLaneMinOffset;
            float3 targetPos = default;
            float2 targetTangent = tangent;
            int sideLaneCount = 0;
            int sideLaneBlocked = 0;
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;
                if ((subLanes[i].m_PathMethods & PathMethod.Road) == 0 ||
                    !EntityManager.HasComponent<Game.Net.CarLane>(lane) ||
                    EntityManager.HasComponent<MasterLane>(lane) ||
                    !EntityManager.HasComponent<Curve>(lane))
                {
                    continue;
                }
                Curve laneCurve = EntityManager.GetComponentData<Curve>(lane);
                MathUtils.Distance(laneCurve.m_Bezier, wreckPos, out float laneT);
                float3 lanePos = MathUtils.Position(laneCurve.m_Bezier, laneT);
                float offset = math.dot(lanePos.xz - edgePos.xz, rightDir) * side;
                if (offset <= kSideLaneMinOffset)
                {
                    continue; // other side of the road - a wreck here doesn't stop this carriageway
                }
                sideLaneCount++;
                Game.Net.CarLane carLane = EntityManager.GetComponentData<Game.Net.CarLane>(lane);
                // LaneDataSystem resets an unblocked lane to (start=255, end=0) and otherwise
                // fills a real blockage range from its lane objects (incl. accident wrecks).
                if (!(carLane.m_BlockageStart == byte.MaxValue && carLane.m_BlockageEnd == 0))
                {
                    sideLaneBlocked++;
                }
                if (offset > bestOffset)
                {
                    bestOffset = offset;
                    bestLane = lane;
                    targetPos = lanePos;
                    targetTangent = math.normalizesafe(MathUtils.Tangent(laneCurve.m_Bezier, laneT).xz);
                }
            }
            if (bestLane == Entity.Null)
            {
                return false;
            }
            // Full-block gate: only consolidate when the ENTIRE carriageway is blocked. As long
            // as one car lane is still free, traffic can pass and dragging the wreck aside just
            // churns it around for nothing (Sebastian's complaint - it fired at every accident).
            if (sideLaneCount == 0 || sideLaneBlocked < sideLaneCount)
            {
                return false;
            }
            // Already sitting on the outer lane? Count it as cleared, nothing to move.
            if (math.distance(wreckPos.xz, targetPos.xz) < 1f)
            {
                return true;
            }

            // Remember the previously blocked lanes so we can poke their pathfind data.
            Transform wreckTransform = EntityManager.GetComponentData<Transform>(wreck);
            float3 forward = new float3(targetTangent.x, 0f, targetTangent.y);
            float3 currentForward = math.forward(wreckTransform.m_Rotation);
            if (math.dot(currentForward.xz, targetTangent) < 0f)
            {
                forward = -forward; // keep whichever way it was pointing - it's a wreck
            }
            wreckTransform.m_Position = targetPos;
            wreckTransform.m_Rotation = quaternion.LookRotationSafe(forward, math.up());
            EntityManager.SetComponentData(wreck, wreckTransform);
            EntityManager.AddComponent<Updated>(wreck);
            EntityManager.AddComponent<BatchesUpdated>(wreck);
            m_Ctx.WreckClearing.PokeBlockedLanes(wreck);

            // A crashed RIG: the lead keeps its LayoutElement buffer, so its trailer(s) must
            // move aside too - otherwise the trailer stays where it crashed and keeps the
            // lane blocked ("Aufräumtool hat den Hänger vergessen"). Line each one up behind
            // the moved wreck on the outer lane, keeping its original hitch distance.
            if (EntityManager.HasBuffer<LayoutElement>(wreck))
            {
                DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(wreck, isReadOnly: true);
                float3 newForward = math.forward(wreckTransform.m_Rotation);
                for (int i = 0; i < layout.Length; i++)
                {
                    Entity member = layout[i].m_Vehicle;
                    if (member == wreck || member == Entity.Null || !EntityManager.Exists(member) ||
                        EntityManager.HasComponent<Deleted>(member) ||
                        !EntityManager.HasComponent<Transform>(member))
                    {
                        continue;
                    }
                    Transform memberTransform = EntityManager.GetComponentData<Transform>(member);
                    float hitch = math.max(3f, math.distance(memberTransform.m_Position.xz, wreckPos.xz));
                    memberTransform.m_Position = targetPos - newForward * hitch;
                    memberTransform.m_Position.y = targetPos.y;
                    memberTransform.m_Rotation = wreckTransform.m_Rotation;
                    EntityManager.SetComponentData(member, memberTransform);
                    EntityManager.AddComponent<Updated>(member);
                    EntityManager.AddComponent<BatchesUpdated>(member);
                    m_Ctx.WreckClearing.PokeBlockedLanes(member);
                    if (Mod.Setting.VerboseLogging)
                    {
                        Mod.Log.Info($"[wreckaside] wreck={wreck.Index} moved trailer={member.Index} along");
                    }
                }
            }
            return true;
        }

        /// <summary>Force the lane-blockage/pathfind data of every lane an object blocked to
        /// be recomputed after it was teleported aside.</summary>
        public void PokeBlockedLanes(Entity obj)
        {
            if (!EntityManager.HasBuffer<Game.Objects.BlockedLane>(obj))
            {
                return;
            }
            DynamicBuffer<Game.Objects.BlockedLane> oldLanes =
                EntityManager.GetBuffer<Game.Objects.BlockedLane>(obj, isReadOnly: true);
            for (int i = 0; i < oldLanes.Length; i++)
            {
                Entity lane = oldLanes[i].m_Lane;
                if (EntityManager.HasComponent<Game.Net.CarLane>(lane) &&
                    !EntityManager.HasComponent<PathfindUpdated>(lane))
                {
                    EntityManager.AddComponent<PathfindUpdated>(lane);
                }
            }
        }

        /// <summary>True once a recovery/tow vehicle has been dispatched to this wreck: its
        /// maintenance request carries a live Dispatched handler. Used to pause the wreck's
        /// despawn timer so it is never deleted out from under an approaching tow truck.</summary>
        public bool IsRecoveryClaimed(Entity wreck)
        {
            return RecoveryClaim.IsClaimed(EntityManager, wreck);
        }
    }
}

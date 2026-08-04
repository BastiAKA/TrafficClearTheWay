using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Simulation;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding;
using ClearTheWay.FunctionGroup.WayClearance.Squeezing.Roundabout;
// Game.Net has a LaneGeometry of its own - ours wins here, like the CarLaneFlags alias above.

namespace ClearTheWay
{
    /// <summary>
    /// Works out WHICH lanes make up the corridor around a responder and which way the cars on
    /// each of them should clear. It only plans; the actual pushing is <see cref="TrafficShaper"/>.
    ///
    /// Two shapes, picked per responder:
    ///  - CLASSIC single seam: one gap pinned to the corridor side, cars on the responder's lane
    ///    and its neighbour clear opposite ways. Right for ordinary roads.
    ///  - CENTRAL CHANNEL, for wide carriageways (>= kChannelMinLanes same-direction lanes): the
    ///    road parts around a seam in the MIDDLE, lanes left of it clearing left and lanes right
    ///    of it clearing right, each only a little, with the offset growing toward the edges. A
    ///    single far-left seam asks everyone on a wide road to pile into the right-hand lanes,
    ///    which on a packed road means nothing can move at all.
    ///
    /// The channel plan is computed from the SEGMENT, not from the responder's own lane, so a
    /// convoy of ambulances all thread the same gap instead of each opening its own.
    ///
    /// Plans are cached per (segment, direction) because they only change when the road is
    /// rebuilt. Without the cache the full SubLane scan ran per responder per tick and cost
    /// ~5 ms/tick at ~170 active sirens.
    /// </summary>
    internal sealed class CorridorBuilder
    {
        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = false;

        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private readonly List<CorridorLane> m_CorridorLanes = new List<CorridorLane>(32);
        private readonly Dictionary<Entity, int> m_CorridorLaneIndex = new Dictionary<Entity, int>(32);

        // Set by BuildCorridor while the responder is on a wide road running the central
        // channel: the direction (physical +1 right / -1 left) it hugs to thread the middle
        // seam. 0 => classic single-seam mode, responder hugs the corridor side (-side).

        /// <summary>This tick's corridor for the responder last passed to BuildCorridor.</summary>
        public List<CorridorLane> CorridorLanes => m_CorridorLanes;

        /// <summary>Which way the responder itself should hug; 0 = classic seam (hug -side).</summary>
        public float ChannelHugDir => m_Ctx.Channel.ChannelHugDir;

        /// <summary>Physical direction toward an empty same-direction lane, 0 = none. Set by this
        /// tick's corridor build; the steering stage uses it to actually change lane.</summary>
        public float FreeLaneDir => m_Ctx.Channel.FreeLaneDir;

        public CorridorBuilder(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }
        /// <summary>
        /// Collects the lanes the emergency vehicle is going to use within the configured
        /// corridor distance: its current lane plus upcoming navigation lanes. Master lanes
        /// (undecided lane groups on multi-lane roads) are expanded to all their sub-lanes
        /// when ClearParallelLanes is enabled.
        /// </summary>
        public void BuildCorridor(Entity vehicle, ref CarCurrentLane currentLane, Setting setting, float side, float emergencySpeed, bool desperate)
        {
            using (ModProfiler.Sample(kProfile, "CorridorBuilder"))
            {
                BuildCorridorImpl(vehicle, ref currentLane, setting, side, emergencySpeed, desperate);
            }
        }

        private void BuildCorridorImpl(Entity vehicle, ref CarCurrentLane currentLane, Setting setting, float side, float emergencySpeed, bool desperate)
        {
            m_CorridorLanes.Clear();
            m_CorridorLaneIndex.Clear();
            m_Ctx.Channel.BeginBuild(vehicle); // clears the steering outputs, re-arms a latched free lane

            // In a multi-lane roundabout the rule flips: sweep every ring lane toward the centre
            // and keep the outermost lane free for the responder, instead of opening a gap in the
            // middle of the ring. Neither wide-road shape applies there, so the shape decision
            // needs to know about it.
            bool currentIsRoundabout = RoundaboutExtensions.IsRing(currentLane.m_LaneFlags);
            // ONE decision, held: classic seam / central channel / heading for a free lane. See
            // ChannelPlanner.ChooseShape for why it is a committed choice rather than three
            // independent per-tick tests (which is what it used to be, and they disagreed).
            CorridorShape shape = m_Ctx.Channel.ChooseShape(vehicle, currentLane, setting,
                emergencySpeed, currentIsRoundabout, desperate);
            bool useChannel = shape == CorridorShape.Channel;
            // WHICH WAY the traffic on the responder's own lane clears. Normally the corridor rule
            // (+side, so the gap opens on -side). With a committed FREE LANE it is inverted: the
            // classic seam would clear the queue TOWARD that lane and fill the very asphalt the
            // responder is heading for - it was parting the road for a gap it had already decided
            // to abandon. Parting away from it instead widens the target lane, and the neighbour
            // on the free-lane side is pushed further out rather than into the way.
            float shapeSide = shape == CorridorShape.FreeLane && m_Ctx.Channel.FreeLaneDir != 0f
                ? -m_Ctx.Channel.FreeLaneDir
                : side;

            float maxDistance = setting.CorridorDistance;
            float distance = 0f;

            if (EntityManager.HasComponent<Game.Net.CarLane>(currentLane.m_Lane) &&
                EntityManager.HasComponent<Curve>(currentLane.m_Lane))
            {
                bool inverted = currentLane.m_CurvePosition.z < currentLane.m_CurvePosition.x;
                if (setting.ClearRoundaboutInner && currentIsRoundabout &&
                    m_Ctx.Roundabout.TryAddRing(currentLane.m_Lane, currentLane.m_CurvePosition.x, shapeSide, inverted, startOffset: 0f))
                {
                    // handled by the roundabout ring sweep
                }
                // Wide road: part the whole carriageway around a central seam (shared by every
                // responder on the segment). Falls through to the classic left seam otherwise.
                else if (useChannel &&
                         m_Ctx.Channel.AddCentralChannel(currentLane.m_Lane, currentLane.m_CurvePosition.x, inverted, startOffset: 0f, computeHug: true))
                {
                    // handled by the central channel
                }
                else
                {
                    m_Ctx.Corridor.AddCorridorLane(currentLane.m_Lane, currentLane.m_CurvePosition.x, shapeSide, inverted, startOffset: 0f, onPath: true);
                    // German ClearTheWay rule: the corridor-side neighbor (the lane to the
                    // LEFT of the emergency in right-hand traffic) always clears further LEFT,
                    // away from the emergency - even onto the median/oncoming side. The gap
                    // opens between it and the emergency's lane. (Opposite-direction neighbours
                    // are held, not pushed - handled inside AddCounterEvadeNeighbor.)
                    m_Ctx.Corridor.AddCounterEvadeNeighbor(currentLane.m_Lane, currentLane.m_CurvePosition.x, shapeSide, inverted, startOffset: 0f, pushDirection: -shapeSide);
                }
                Curve curve = EntityManager.GetComponentData<Curve>(currentLane.m_Lane);
                distance += curve.m_Length * math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x);

                // While changing lanes, also clear the target lane so the change can finish.
                if (currentLane.m_ChangeLane != Entity.Null &&
                    EntityManager.Exists(currentLane.m_ChangeLane) &&
                    EntityManager.HasComponent<Game.Net.CarLane>(currentLane.m_ChangeLane))
                {
                    m_Ctx.Corridor.AddCorridorLane(currentLane.m_ChangeLane, currentLane.m_CurvePosition.x, shapeSide, inverted, startOffset: 0f, onPath: true);
                }
            }

            if (!EntityManager.HasBuffer<CarNavigationLane>(vehicle))
            {
                return;
            }

            DynamicBuffer<CarNavigationLane> navLanes = EntityManager.GetBuffer<CarNavigationLane>(vehicle, isReadOnly: true);
            for (int i = 0; i < navLanes.Length && distance < maxDistance; i++)
            {
                CarNavigationLane navLane = navLanes[i];
                if (!EntityManager.Exists(navLane.m_Lane))
                {
                    break;
                }
                // Stop at parking maneuvers, waypoints and off-road segments.
                if ((navLane.m_Flags & (CarLaneFlags.ParkingSpace | CarLaneFlags.Waypoint | CarLaneFlags.TransformTarget | CarLaneFlags.Area)) != 0)
                {
                    break;
                }

                bool inverted = navLane.m_CurvePosition.y < navLane.m_CurvePosition.x;

                if (EntityManager.HasComponent<MasterLane>(navLane.m_Lane))
                {
                    if (setting.ClearParallelLanes)
                    {
                        m_Ctx.LaneGroups.AddMasterLaneGroup(navLane.m_Lane, shapeSide, inverted, navLane.m_CurvePosition.x, distance);
                    }
                }
                else if (EntityManager.HasComponent<Game.Net.CarLane>(navLane.m_Lane))
                {
                    bool navIsRoundabout = RoundaboutExtensions.IsRing(navLane.m_Flags);
                    if (setting.ClearRoundaboutInner && navIsRoundabout &&
                        m_Ctx.Roundabout.TryAddRing(navLane.m_Lane, navLane.m_CurvePosition.x, shapeSide, inverted, startOffset: distance))
                    {
                        // handled by the roundabout ring sweep
                    }
                    // Wide road, NEAR field only: part the whole carriageway. Beyond
                    // kChannelMaxDistance the light classic seam is enough (and far cheaper) -
                    // cars that far ahead have seconds to move before the responder arrives.
                    // computeHug is false; the seam is only steered on the current lane.
                    else if (useChannel && distance < kChannelMaxDistance &&
                             m_Ctx.Channel.AddCentralChannel(navLane.m_Lane, navLane.m_CurvePosition.x, inverted, startOffset: distance, computeHug: false))
                    {
                        // handled by the central channel
                    }
                    else
                    {
                        m_Ctx.Corridor.AddCorridorLane(navLane.m_Lane, navLane.m_CurvePosition.x, shapeSide, inverted, startOffset: distance, onPath: true);
                        m_Ctx.Corridor.AddCounterEvadeNeighbor(navLane.m_Lane, navLane.m_CurvePosition.x, shapeSide, inverted, startOffset: distance, pushDirection: -shapeSide);
                    }
                }

                if (EntityManager.HasComponent<Curve>(navLane.m_Lane))
                {
                    Curve curve = EntityManager.GetComponentData<Curve>(navLane.m_Lane);
                    distance += curve.m_Length * math.abs(navLane.m_CurvePosition.y - navLane.m_CurvePosition.x);
                }

                if ((navLane.m_Flags & CarLaneFlags.EndOfPath) != 0)
                {
                    break;
                }
            }
        }

        public void AddCorridorLane(Entity lane, float minPos, float pushDirection, bool inverted, float startOffset, bool onPath, float pushMeters = 0f)
        {
            if (m_CorridorLaneIndex.TryGetValue(lane, out int existing))
            {
                // Lanes on the vehicle's own path override a counter-evade neighbor entry.
                if (onPath && !m_CorridorLanes[existing].m_OnPath)
                {
                    CorridorLane updated = m_CorridorLanes[existing];
                    updated.m_MinPos = minPos;
                    updated.m_PushDirection = pushDirection;
                    updated.m_StartOffset = startOffset;
                    updated.m_OnPath = true;
                    updated.m_PushMeters = pushMeters;
                    m_CorridorLanes[existing] = updated;
                }
                return;
            }
            // Hard bound on how much road one responder parts. Lanes are added roughly
            // nearest-first (own lane and its channel/neighbour, then the navigation lanes in
            // path order), so the cap drops the FURTHEST ones - the traffic that has the most
            // time to move anyway. The ordinary corridor is 2-6 lanes; the cap only ever bites
            // where the count runs away, and it does: veh 2350532 built 27 and then 62 lanes
            // while pushing nothing at all (a junction whose master lane groups each expand to
            // their full set). Every one of those costs a full LaneObject scan per responder per
            // tick, and shoves cars that are nowhere near the responder's actual path.
            if (m_CorridorLanes.Count >= kMaxCorridorLanes)
            {
                return;
            }
            m_CorridorLaneIndex[lane] = m_CorridorLanes.Count;
            m_CorridorLanes.Add(new CorridorLane
            {
                m_Lane = lane,
                m_MinPos = minPos,
                m_PushDirection = pushDirection,
                m_StartOffset = startOffset,
                m_Inverted = inverted,
                m_OnPath = onPath,
                m_PushMeters = pushMeters
            });
        }

        /// <summary>
        /// The neighbor lane on the corridor side (left of the path lane in right-hand
        /// traffic) evades AWAY from the corridor, so the gap opens between two rows of
        /// cars like a real ClearTheWay instead of everyone piling to the same side.
        /// </summary>
        public void AddCounterEvadeNeighbor(Entity lane, float minPos, float side, bool inverted, float startOffset, float pushDirection)
        {
            if (!EntityManager.HasComponent<SlaveLane>(lane) ||
                !EntityManager.HasComponent<Owner>(lane) ||
                !EntityManager.HasComponent<Game.Net.CarLane>(lane))
            {
                return;
            }
            SlaveLane slaveLane = EntityManager.GetComponentData<SlaveLane>(lane);
            Owner owner = EntityManager.GetComponentData<Owner>(lane);
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner.m_Owner))
            {
                return;
            }
            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner.m_Owner, isReadOnly: true);
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
                return;
            }
            bool laneInverted = (EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            // Corridor side: left in right-hand traffic (side=1), right in left-hand traffic.
            bool towardLower = (side > 0f) != laneInverted;
            int neighborIndex = towardLower ? myIndex - 1 : myIndex + 1;
            if (neighborIndex < slaveLane.m_MinIndex || neighborIndex > maxIndex)
            {
                return;
            }
            Entity neighbor = subLanes[neighborIndex].m_SubLane;
            if (!EntityManager.HasComponent<Game.Net.CarLane>(neighbor) ||
                EntityManager.HasComponent<MasterLane>(neighbor))
            {
                return;
            }
            // Only push a SAME-direction neighbor aside. An opposite-direction neighbour is
            // the oncoming carriageway - its cars are made to yield elsewhere, never shoved
            // sideways into their own traffic.
            bool neighborInverted = (EntityManager.GetComponentData<Game.Net.CarLane>(neighbor).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            if (neighborInverted != laneInverted)
            {
                return;
            }
            m_Ctx.Corridor.AddCorridorLane(neighbor, minPos, pushDirection, inverted, startOffset, onPath: false);
        }






        /// <summary>Slow-cadence cleanup; the cached channel plans belong to ChannelPlanner.</summary>
        public void Prune(uint frame)
        {
            m_Ctx.Channel.Prune(frame);
        }
    }
}

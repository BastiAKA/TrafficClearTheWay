using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>How much extra lateral room a lane has, and when that answer was computed.</summary>
    internal struct LateralRoomEntry
    {
        public float m_Left;
        public float m_Right;
        public uint m_Frame;
    }

    /// <summary>
    /// How far beyond its own lane edge a vehicle may be pushed on a given side, because what lies
    /// there is CROSSABLE - a tram bed, a grass strip, a parking shoulder - rather than a kerb, a
    /// platform or another driving lane.
    ///
    /// This is a pure QUERY: it answers "how many metres are there", never moves anything and never
    /// decides whether to use them. That is deliberate, and the reason it is its own class. Today
    /// four call sites each roll their own <c>min(meters / LateralSlack, kMaxPushUnits)</c> -
    /// PushVehicles, EscalationSteering, RecoveryAssist and RoundaboutExtensions - so every new rule
    /// had to be retro-fitted four times. With the room behind one call, the tow truck inherits
    /// every improvement the emergency corridor gets, for free.
    ///
    /// HOW IT LOOKS SIDEWAYS. It walks the edge's SubLane buffer outward from the vehicle's own
    /// lane, exactly the way <see cref="ChannelPlanner.ChannelShoulderBonus"/> already does (same
    /// index arithmetic, deliberately - that mapping between physical side and sublane index was
    /// fiddly to get right and must not be re-derived differently here). It stops at the first
    /// thing a vehicle must not end up on.
    ///
    /// THE TRAM RULE. A tram bed is crossable only while no tram is on it - being pushed in front
    /// of one would be worse than the jam we are clearing. Occupancy is read from the track lane's
    /// LaneObject buffer, and the whole answer is CACHED per lane for kLateralRoomRefreshFrames
    /// (Sebastian: "Query ggf. sanft alle paar Frames"). Per-tick lane scans for every pushed car
    /// are exactly the main-thread stall that caused the post-accident lag hunt.
    /// </summary>
    internal sealed class LateralRoom
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        private readonly Dictionary<Entity, LateralRoomEntry> m_Cache = new Dictionary<Entity, LateralRoomEntry>();
        private readonly List<Entity> m_Prune = new List<Entity>();
        private uint m_LastPrune;

        public LateralRoom(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Extra metres available beyond the lane edge on the side <paramref name="pushDirection"/>
        /// points to (+1 = right of travel, -1 = left). 0 when there is nothing crossable there, so
        /// a caller that simply adds this value keeps its current behaviour on ordinary roads.
        /// </summary>
        public float CrossableMeters(Entity lane, float pushDirection, uint frame)
        {
            if (!Mod.Setting.CrossMedians || lane == Entity.Null || !EntityManager.Exists(lane))
            {
                return 0f;
            }
            if (m_Cache.TryGetValue(lane, out LateralRoomEntry entry) &&
                frame - entry.m_Frame < kLateralRoomRefreshFrames)
            {
                return pushDirection < 0f ? entry.m_Left : entry.m_Right;
            }
            entry = new LateralRoomEntry
            {
                m_Left = Measure(lane, -1f),
                m_Right = Measure(lane, 1f),
                m_Frame = frame
            };
            m_Cache[lane] = entry;
            // The travel-frame -> edge-frame sign mapping in SurfaceMeters is DERIVED, not measured:
            // get it wrong and the room is reported for the WRONG side, which would invite cars onto
            // the oncoming carriageway instead of onto the median. So print both sides for the rare
            // lanes that actually have room, and check it against the road in-game before trusting
            // it. Only fires when something was found, so it cannot flood the log.
            if (Mod.Setting.VerboseLogging && (entry.m_Left > 0.01f || entry.m_Right > 0.01f))
            {
                // inv= is the decisive column for the travel->edge frame conversion in
                // SurfaceMeters: it is DERIVED, and a wrong sign would be wrong only on INVERTED
                // lanes while looking perfectly correct on the others. Two lanes of the same road
                // with opposite inv= must report the room on the same physical side; if they
                // mirror each other instead, the laneInverted factor is the wrong way round.
                bool inv = EntityManager.HasComponent<Game.Net.CarLane>(lane) &&
                    (EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
                Mod.Log.Info($"[room] lane={lane.Index} left={entry.m_Left:F1}m right={entry.m_Right:F1}m inv={(inv ? 1 : 0)}");
            }
            PruneStale(frame);
            return pushDirection < 0f ? entry.m_Left : entry.m_Right;
        }

        /// <summary>
        /// Walk outward one sublane at a time and add up what may be driven on. Everything is
        /// judged by what the lane IS, never by its name or prefab, so custom roads (Road Builder
        /// assembles them from the same vanilla sections) behave like built-in ones.
        /// </summary>
        private float Measure(Entity lane, float pushDirection)
        {
            if (!EntityManager.HasComponent<Game.Common.Owner>(lane))
            {
                return 0f;
            }
            Entity owner = EntityManager.GetComponentData<Game.Common.Owner>(lane).m_Owner;
            if (owner == Entity.Null || !EntityManager.Exists(owner) ||
                !EntityManager.HasBuffer<Game.Net.SubLane>(owner) ||
                !EntityManager.HasComponent<Game.Net.CarLane>(lane))
            {
                return 0f;
            }
            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner, isReadOnly: true);
            int ownIdx = -1;
            for (int i = 0; i < subLanes.Length; i++)
            {
                if (subLanes[i].m_SubLane == lane) { ownIdx = i; break; }
            }
            if (ownIdx < 0)
            {
                return 0f;
            }
            // Same mapping ChannelShoulderBonus uses: physical-left is the LOWER sublane index
            // exactly when the lane is not inverted, and lane-position is signed in travel terms
            // (+ = right of travel), so both flip together under Invert.
            bool laneInverted =
                (EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            bool physicalLeftSide = (pushDirection < 0f) != laneInverted;
            bool leftIsLower = !laneInverted;
            int step = (physicalLeftSide == leftIsLower) ? -1 : 1;

            float room = 0f;
            bool footwayAhead = false;
            for (int i = ownIdx + step; i >= 0 && i < subLanes.Length; i += step)
            {
                Entity sl = subLanes[i].m_SubLane;
                if (sl == Entity.Null || !EntityManager.Exists(sl))
                {
                    continue;
                }
                // Another driving lane: whatever lies beyond it is none of our business, and
                // hopping across it would drop the car into moving traffic.
                if (EntityManager.HasComponent<Game.Net.CarLane>(sl) &&
                    !EntityManager.HasComponent<Game.Net.MasterLane>(sl))
                {
                    break;
                }
                // Tram bed - crossable, but only while empty.
                if (EntityManager.HasComponent<Game.Net.TrackLane>(sl))
                {
                    if (IsOccupied(sl))
                    {
                        break;
                    }
                    room += m_Ctx.PrefabGeometry.LaneWidth(sl);
                    continue;
                }
                // Parking / shoulder: half its width, the same allowance the channel planner makes
                // (enough to reach onto it without parking the car on the far kerb).
                if (EntityManager.HasComponent<Game.Net.ParkingLane>(sl))
                {
                    room += m_Ctx.PrefabGeometry.LaneWidth(sl) * 0.5f;
                    continue;
                }
                // A footway is where the pedestrians are - the hard-evade already reaches onto it
                // deliberately and in a controlled way, so this pass stops here rather than
                // handing out the whole pavement as free room.
                if (EntityManager.HasComponent<Game.Net.PedestrianLane>(sl))
                {
                    footwayAhead = true;
                    break;
                }
                // Anything else with no lane semantics we understand: stop rather than guess.
                break;
            }
            // A grass strip or median is NOT a lane, so the walk above cannot see it at all - it
            // exists only as pieces of the road's composition. Take whichever source offers more,
            // EXCEPT past a footway: the walk stops at a PedestrianLane on purpose, and a footway
            // that happens to be flush with the road has no BlockTraffic piece to stop the surface
            // measurement, so it would quietly overrule that guard and park cars where the
            // pedestrian pass is busy keeping them out.
            if (footwayAhead)
            {
                return math.min(room, kCrossableRoomCap);
            }
            return math.min(math.max(room, SurfaceMeters(lane, owner, pushDirection, laneInverted)),
                kCrossableRoomCap);
        }

        /// <summary>
        /// Room on the non-lane surface beside the carriageway - the green strip, the median, the
        /// paved gap a tram bed sits in. These are <see cref="Game.Prefabs.NetCompositionPiece"/>s of the road's
        /// composition, not lanes, so nothing in the SubLane walk above can find them.
        ///
        /// The rule is the game's own: a piece carrying <c>Game.Prefabs.NetPieceFlags.BlockTraffic</c> is one a
        /// vehicle must not end up on (kerb, platform, barrier - LaneSystem uses exactly this flag
        /// to decide whether a U-turn between carriageways is possible). So we measure how far
        /// outward the surface runs before the first blocking piece, and hand back that much.
        /// Author-set per piece via NetDividerPiece.m_BlockTraffic, which is why it works the same
        /// on Road Builder roads - those are assembled from the same vanilla sections.
        /// </summary>
        private float SurfaceMeters(Entity lane, Entity owner, float pushDirection, bool laneInverted)
        {
            if (!EntityManager.HasComponent<Composition>(owner) ||
                !EntityManager.HasComponent<Curve>(owner) ||
                !EntityManager.HasComponent<Curve>(lane))
            {
                return 0f;
            }
            Entity composition = EntityManager.GetComponentData<Composition>(owner).m_Edge;
            if (composition == Entity.Null || !EntityManager.Exists(composition) ||
                !EntityManager.HasBuffer<Game.Prefabs.NetCompositionPiece>(composition))
            {
                return 0f;
            }
            // Where our lane sits across the road, in the EDGE's own frame (+ = edge-right). Same
            // idiom WreckClearing uses to keep a wreck on its own side of the road.
            Curve edgeCurve = EntityManager.GetComponentData<Curve>(owner);
            Curve laneCurve = EntityManager.GetComponentData<Curve>(lane);
            float3 edgePos = MathUtils.Position(edgeCurve.m_Bezier, 0.5f);
            float2 tangent = math.normalizesafe(MathUtils.Tangent(edgeCurve.m_Bezier, 0.5f).xz);
            float2 rightDir = MathUtils.Right(tangent);
            float3 lanePos = MathUtils.Position(laneCurve.m_Bezier, 0.5f);
            float ourX = math.dot(lanePos.xz - edgePos.xz, rightDir);

            // pushDirection is signed in TRAVEL terms (+ = right of travel); the piece offsets are
            // in edge terms. The two frames differ exactly when the lane runs against the edge.
            float sideSign = (pushDirection >= 0f ? 1f : -1f) * (laneInverted ? -1f : 1f);
            // Work in an "outward" coordinate so both sides read the same: bigger = further out.
            float from = ourX * sideSign + m_Ctx.PrefabGeometry.LaneWidth(lane) * 0.5f;

            DynamicBuffer<Game.Prefabs.NetCompositionPiece> pieces =
                EntityManager.GetBuffer<Game.Prefabs.NetCompositionPiece>(composition, isReadOnly: true);
            float blockedAt = float.MaxValue;
            float surfaceEnd = from;
            for (int i = 0; i < pieces.Length; i++)
            {
                Game.Prefabs.NetCompositionPiece piece = pieces[i];
                float centre = piece.m_Offset.x * sideSign;
                float half = math.abs(piece.m_Size.x) * 0.5f;
                float inner = centre - half;
                float outer = centre + half;
                if (outer <= from)
                {
                    continue; // entirely inboard of us - not something we could move onto
                }
                // A kerb, platform or barrier: the surface ends at its near edge, full stop.
                if ((piece.m_PieceFlags & Game.Prefabs.NetPieceFlags.BlockTraffic) != 0 ||
                    piece.m_Size.y > kMaxCrossableHeight)
                {
                    blockedAt = math.min(blockedAt, math.max(inner, from));
                    continue;
                }
                surfaceEnd = math.max(surfaceEnd, outer);
            }
            return math.max(0f, math.min(surfaceEnd, blockedAt) - from);
        }

        /// <summary>
        /// Is something standing on / running over this lane right now? The LaneObject buffer is
        /// the game's own record of what occupies a lane, so this covers a tram sitting at a stop
        /// as well as one passing through.
        /// </summary>
        private bool IsOccupied(Entity lane)
        {
            return EntityManager.HasBuffer<LaneObject>(lane) &&
                   EntityManager.GetBuffer<LaneObject>(lane, isReadOnly: true).Length != 0;
        }

        /// <summary>Drop entries for lanes nobody has asked about in a while, so a long session
        /// cannot grow the cache without bound. Rare on purpose - the cache is small and cheap.</summary>
        private void PruneStale(uint frame)
        {
            if (frame - m_LastPrune < kLateralRoomPruneFrames)
            {
                return;
            }
            m_LastPrune = frame;
            m_Prune.Clear();
            foreach (KeyValuePair<Entity, LateralRoomEntry> kv in m_Cache)
            {
                if (frame - kv.Value.m_Frame > kLateralRoomPruneFrames || !EntityManager.Exists(kv.Key))
                {
                    m_Prune.Add(kv.Key);
                }
            }
            for (int i = 0; i < m_Prune.Count; i++)
            {
                m_Cache.Remove(m_Prune[i]);
            }
            m_Prune.Clear();
        }
    }
}

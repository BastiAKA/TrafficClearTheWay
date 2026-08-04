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

namespace ClearTheWay.FunctionGroup.WayClearance
{
    /// <summary>
    /// The CENTRAL CHANNEL corridor for wide carriageways.
    ///
    /// A single seam pinned to the far left asks everyone on a wide road to pile into the
    /// right-hand lanes - on a packed road that means nothing can move at all. Instead the road
    /// parts around a seam in the MIDDLE: lanes left of it clear left, lanes right of it clear
    /// right, each only a little, with the offset growing toward the edges so the seam actually
    /// opens.
    ///
    /// The plan is computed from the SEGMENT rather than from a responder lane, so a convoy of
    /// ambulances all thread the same gap instead of each opening its own. It is cached per
    /// (segment, direction) because it only changes when the road is rebuilt - without the cache
    /// the full sub-lane scan ran per responder per tick and cost several ms at ~170 sirens.
    /// </summary>
    internal sealed class ChannelPlanner
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private SimulationSystem m_SimulationSystem => m_Ctx.Simulation;



        /// <summary>Per-(segment, direction) plan cache - see the class summary for why.</summary>
        private readonly Dictionary<(Entity, bool), ChannelPlan> m_ChannelCache = new();
        private readonly List<(Entity, bool)> m_ChannelCachePrune = new();
        private const uint kChannelCacheTtl = 256u; // recompute a segment plan at most this often (~4s)

        /// <summary>Set while the responder is on a wide road running the central channel: the
        /// direction (physical +1 right / -1 left) it hugs to thread the middle seam. 0 means
        /// classic single-seam mode, where the responder hugs the corridor side instead.</summary>
        private float m_ChannelHugDir;
        public float ChannelHugDir => m_ChannelHugDir;

        /// <summary>Physical direction (+1 right / -1 left) toward a same-direction lane that is
        /// EMPTY ahead of the responder, 0 when there is none. See FindFreeLaneDir.</summary>
        private float m_FreeLaneDir;
        public float FreeLaneDir => m_FreeLaneDir;

        /// <summary>
        /// Start of one responder's corridor build: clear the per-responder steering outputs, then
        /// RE-ARM a still-valid free-lane commitment from the latch.
        ///
        /// The re-arm is the point. The choice lives in the latch, not in whether this particular
        /// tick happened to run the scan - and the scan is skipped on plenty of ticks (the
        /// responder is momentarily too fast for the wide-road shapes, or its current lane sits in
        /// a junction and has no lane group to scan). Without the re-arm the committed direction
        /// silently became 0 on exactly those ticks, so the free lane stopped being preferred in
        /// the very moment the responder started rolling toward it, and the steering fell back to
        /// the seam it had already decided to abandon.
        /// </summary>
        public void BeginBuild(Entity vehicle)
        {
            m_ChannelHugDir = 0f;
            m_FreeLaneDir = 0f;
            m_Ctx.States.Stuck.TryGetValue(vehicle, out StuckState st);
            if (st.m_FreeLaneDir != 0f && m_SimulationSystem.frameIndex < st.m_FreeLaneUntilFrame)
            {
                m_FreeLaneDir = st.m_FreeLaneDir;
            }
        }

        /// <summary>
        /// THE corridor decision for one responder this tick: classic seam, central channel, or
        /// heading for a free lane. Call once per corridor build, after <see cref="BeginBuild"/>.
        ///
        /// Everything about it is built to make the choice STICK:
        ///  - a working corridor is never re-planned (the dwell is refreshed while cars are being
        ///    pushed for it), because swapping the shape under a formed Rettungsgasse tears up the
        ///    gap the queue just opened and hands the traffic contradictory push directions;
        ///  - the speed gate has a hysteresis band instead of a bare threshold;
        ///  - a free-lane choice survives ticks on which it cannot be re-scanned;
        ///  - and a desperate responder unlocks all of it again, because there the corridor has
        ///    demonstrably failed and every escape has to stay reachable.
        /// </summary>
        public CorridorShape ChooseShape(Entity vehicle, CarCurrentLane currentLane, Setting setting,
            float emergencySpeed, bool onRing, bool desperate)
        {
            uint frame = m_SimulationSystem.frameIndex;
            m_Ctx.States.Stuck.TryGetValue(vehicle, out StuckState st);
            CorridorShape held = (CorridorShape)st.m_Shape;
            // Only the WIDE-ROAD shapes are defended. Classic is the fallback - it is what every
            // cruising responder and every narrow road ends up with - and locking it would be a
            // trap: a responder that formed a classic corridor at speed keeps pushing cars while
            // it slows into the jam, so the lock would be refreshed forever and the carriageway
            // would never part around the central channel at all. Classic -> Channel is an
            // ESCALATION and must always stay available; the reverse is held by the speed
            // hysteresis below, not by this lock.
            bool locked = (held == CorridorShape.Channel || held == CorridorShape.FreeLane) &&
                          frame < st.m_ShapeUntilFrame && !desperate;

            // Neither wide-road shape applies on a roundabout ring (the ring sweep has its own,
            // inverted rule) or when the player has not asked for parallel lanes to be cleared.
            if (onRing || !setting.ClearParallelLanes)
            {
                ReleaseFreeLane(vehicle);
                return CommitShape(vehicle, CorridorShape.Classic, frame);
            }

            // SPEED GATE WITH HYSTERESIS: entering a wide-road shape needs kChannelMaxSpeed,
            // leaving one needs kChannelReleaseSpeed. See kChannelReleaseSpeed for what a bare
            // threshold did at the boundary.
            float leaveSpeed = held == CorridorShape.Classic || held == CorridorShape.None
                ? kChannelMaxSpeed
                : kChannelReleaseSpeed;
            if (emergencySpeed >= leaveSpeed)
            {
                ReleaseFreeLane(vehicle);
                return CommitShape(vehicle, CorridorShape.Classic, frame);
            }

            // A corridor that is actually working keeps its shape, full stop. Only a committed
            // FREE LANE keeps being scanned while locked - the scan is what refreshes its latch
            // and what gives the choice up once that lane fills.
            if (locked && held != CorridorShape.FreeLane)
            {
                // Invariant: only a FreeLane plan may steer toward a free lane. Otherwise a stale
                // latch would have the steering stage overrule a working corridor (EscalationSteering
                // treats a free lane as the one reason to leave one) while this build was parting
                // the road for a completely different gap.
                m_FreeLaneDir = 0f;
                return held;
            }

            // A genuinely free lane always wins over prying the middle open: the channel seam is
            // placed geometrically, so it would steer the responder into the packed middle right
            // next to open asphalt - and then hold it there, because a pushing channel counts as a
            // working corridor and that suppresses the very lane change that would have used the
            // free lane. FindFreeLaneDir sets, keeps or releases m_FreeLaneDir.
            FindFreeLaneDir(vehicle, currentLane.m_Lane, currentLane.m_CurvePosition.x,
                currentLane.m_CurvePosition.z < currentLane.m_CurvePosition.x);
            if (m_FreeLaneDir != 0f)
            {
                // Lean toward it while the change is still pending: the hug reuses the channel's
                // steering path, so pointing it at the free lane has the responder easing over
                // instead of into the middle of the queue.
                m_ChannelHugDir = m_FreeLaneDir;
                return CommitShape(vehicle, CorridorShape.FreeLane, frame);
            }
            // Either nothing was committed, or the lane a committed plan was heading for has
            // filled up. Both mean: plan afresh, and on a wide road that is the channel.
            return CommitShape(vehicle, CorridorShape.Channel, frame);
        }

        /// <summary>Records the chosen shape and (re)starts its dwell. Re-reads the state rather
        /// than taking a copy, because FindFreeLaneDir may have written the free-lane latch in
        /// between.</summary>
        private CorridorShape CommitShape(Entity vehicle, CorridorShape shape, uint frame)
        {
            m_Ctx.States.Stuck.TryGetValue(vehicle, out StuckState st);
            st.m_Shape = (byte)shape;
            st.m_ShapeUntilFrame = frame + kShapeCommitFrames;
            st.m_LastSeenFrame = frame;
            m_Ctx.States.Stuck[vehicle] = st;
            return shape;
        }

        /// <summary>
        /// Keeps the current shape decision alive for another dwell. Called every tick the
        /// corridor is actually moving traffic, which is what turns the dwell from a countdown
        /// into "held for as long as the Rettungsgasse is being maintained".
        /// Works on the caller's state copy so the later latch writes of the same tick (hug,
        /// overtake cooldown) carry the refresh along instead of overwriting it.
        /// </summary>
        public void RefreshShapeCommitment(Entity vehicle, ref StuckState st, uint frame)
        {
            if (st.m_Shape == 0)
            {
                return;
            }
            st.m_ShapeUntilFrame = frame + kShapeCommitFrames;
            m_Ctx.States.Stuck[vehicle] = st;
        }

        /// <summary>Gives up a free-lane commitment outright: the responder is no longer in a
        /// state where that plan applies (too fast, on a ring, parallel clearing off).</summary>
        private void ReleaseFreeLane(Entity vehicle)
        {
            m_FreeLaneDir = 0f;
            if (!m_Ctx.States.Stuck.TryGetValue(vehicle, out StuckState st) || st.m_FreeLaneDir == 0f)
            {
                return;
            }
            st.m_FreeLaneDir = 0f;
            st.m_FreeLaneUntilFrame = 0u;
            m_Ctx.States.Stuck[vehicle] = st;
        }

        /// <summary>
        /// Is one of the segment's same-direction lanes simply EMPTY ahead of the responder? Then
        /// that lane beats any corridor: driving into free asphalt is always better than prying a
        /// seam open in the queue next to it.
        ///
        /// This exists because the channel seam is placed geometrically - in the middle of the
        /// carriageway, from the lane COUNT alone - with no notion of where the traffic actually
        /// is. On a road whose outer lane happens to be empty that steered the responder INTO the
        /// packed middle and then kept it there: the channel pushes cars every tick, so the
        /// corridor counts as "working", and a working corridor suppresses the very lane change
        /// that would have used the free lane (see EscalationSteering).
        ///
        /// Returns the physical direction toward the NEAREST such lane, preferring the kerb side
        /// on a tie (that is where an empty lane usually is, and it keeps the responder away from
        /// oncoming traffic). Lanes the vehicle may not use - forbidden, or public-transport only -
        /// do not count, so it is never lured onto a bus lane.
        ///
        /// Only called for slow responders on channel-width roads (the caller's useChannel gate),
        /// so the per-lane occupancy scan stays off the many cruising sirens.
        /// </summary>

        /// <summary>
        /// Is there a parking strip immediately beside this lane? Such a lane is permanently lined
        /// with standing cars, so "no moving traffic ahead" badly overstates how usable it is.
        /// Walks outward from the lane in the SubLane buffer and stops at the first real thing,
        /// the same shape ChannelShoulderBonus uses.
        /// </summary>
        private bool HasParkingBeside(Entity lane, Entity owner)
        {
            // Cached per lane: whether a parking strip lies alongside cannot change while the road
            // stands, but the walk below runs inside FindFreeLaneDir - once per responder per
            // corridor build, every tick. Same reasoning as the lane-width and vehicle-geometry
            // caches. Keyed by LANE (not prefab) because it is a property of this stretch of road;
            // entries for lanes that no longer exist are dropped with the channel plans.
            if (m_ParkingBeside.TryGetValue(lane, out bool cached))
            {
                return cached;
            }
            bool result = ComputeParkingBeside(lane, owner);
            m_ParkingBeside[lane] = result;
            return result;
        }

        private readonly Dictionary<Entity, bool> m_ParkingBeside = new Dictionary<Entity, bool>();

        private bool ComputeParkingBeside(Entity lane, Entity owner)
        {
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner))
            {
                return false;
            }
            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner, isReadOnly: true);
            int idx = -1;
            for (int i = 0; i < subLanes.Length; i++)
            {
                if (subLanes[i].m_SubLane == lane) { idx = i; break; }
            }
            if (idx < 0)
            {
                return false;
            }
            for (int step = -1; step <= 1; step += 2)
            {
                for (int i = idx + step; i >= 0 && i < subLanes.Length; i += step)
                {
                    Entity sl = subLanes[i].m_SubLane;
                    if (sl == Entity.Null || !EntityManager.Exists(sl))
                    {
                        continue;
                    }
                    if (EntityManager.HasComponent<Game.Net.ParkingLane>(sl))
                    {
                        return true;
                    }
                    // Another driving lane between us and any parking strip: that strip is no
                    // longer "beside" this lane and does not affect it.
                    if (EntityManager.HasComponent<Game.Net.CarLane>(sl) &&
                        !EntityManager.HasComponent<Game.Net.MasterLane>(sl))
                    {
                        break;
                    }
                }
            }
            return false;
        }

        /// <remarks>
        /// Does NOT clear <see cref="FreeLaneDir"/> up front - <see cref="BeginBuild"/> owns the
        /// reset and the re-arm. But the early exits below are NOT all alike, and treating them as
        /// one is a trap I fell into once already:
        ///
        ///  - "no lane group" / "not a channel road" is a DEFINITIVE answer. A road with fewer than
        ///    kChannelMinLanes lanes in this direction has no free lane to go to, full stop. Keeping
        ///    a commitment across such a segment carried a free-lane plan from a wide road onto a
        ///    SINGLE-LANE one (Sebastian, veh 2350571), where CorridorBuilder inverts the corridor
        ///    push for it - so the traffic on a single carriageway was parted the wrong way for a
        ///    lane that cannot exist. These release.
        ///  - "I cannot locate myself in the plan" (refPos &lt; 0, typically a junction lane) really
        ///    does mean "cannot judge from here". Releasing there made a responder abandon a lane it
        ///    had committed to every time its path crossed an intersection. This one keeps.
        ///
        /// Otherwise only a scan that actually RAN over the candidates and found nothing usable
        /// gives the choice up.
        /// </remarks>
        public float FindFreeLaneDir(Entity vehicle, Entity refLane, float curvePosition, bool inverted)
        {
            if (!EntityManager.HasComponent<Owner>(refLane) ||
                !EntityManager.HasComponent<Game.Net.CarLane>(refLane))
            {
                ReleaseFreeLane(vehicle); // not a road lane we can reason about - no free lane here
                return 0f;
            }
            Entity owner = EntityManager.GetComponentData<Owner>(refLane).m_Owner;
            bool laneInverted = (EntityManager.GetComponentData<Game.Net.CarLane>(refLane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            ChannelPlan plan = GetChannelPlan(owner, laneInverted);
            // m_Lanes is only ordered physical left->right once the plan is a real channel plan.
            // Definitive, not "cannot judge": this stretch of road has no second lane in our
            // direction, so there is nothing to commit to - see the remarks above.
            if (plan == null || !plan.m_IsChannel)
            {
                ReleaseFreeLane(vehicle);
                return 0f;
            }
            // ...whereas this one IS "cannot judge": the road HAS a lane group, we just are not in
            // its cached list (a junction lane, or the plan predates a rebuild). Leave a latched
            // choice alone rather than dropping it at every intersection.
            int refPos = plan.m_Lanes.IndexOf(refLane);
            if (refPos < 0)
            {
                return 0f;
            }
            // Hold a choice already made. Re-deciding from scratch every pass is what made the
            // responder oscillate; while the latch runs, the same direction is kept as long as the
            // lane stays merely USABLE (a car or two), not strictly empty.
            m_Ctx.States.Stuck.TryGetValue(vehicle, out StuckState freeLatch);
            bool latched = freeLatch.m_FreeLaneDir != 0f &&
                m_SimulationSystem.frameIndex < freeLatch.m_FreeLaneUntilFrame;
            CarFlags carFlags = EntityManager.HasComponent<Car>(vehicle)
                ? EntityManager.GetComponentData<Car>(vehicle).m_Flags
                : default;
            // SCAN ORDER IS THE COMMITMENT. Relaxing the acceptance threshold for the held
            // direction (below) does nothing on its own if a lane on the OTHER side is simply
            // reached first - and under the plain nearest-first order it usually is. Field case
            // veh 2350532: committed free=-1, then a kerb lane briefly emptied and the very next
            // pass returned +1; the corridor hug jumped with it and the responder swung from
            // lanePos -5.8 to +4.9 within seconds while going nowhere. So while a choice is
            // latched, that DIRECTION gets first refusal across the whole segment, and only if no
            // lane on it is usable at all is the other side looked at.
            // Unlatched the old rule stands: nearest first, kerb side (+1 physical right in
            // right-hand traffic) on a tie - that is where an empty lane usually is, and it keeps
            // the responder away from oncoming traffic.
            float kerbDir = m_Ctx.CityConfiguration.leftHandTraffic ? -1f : 1f;
            int reach = plan.m_Lanes.Count - 1;
            for (int c = 0; c < reach * 2; c++)
            {
                float dir;
                int d;
                if (latched)
                {
                    bool heldSide = c < reach;
                    dir = heldSide ? freeLatch.m_FreeLaneDir : -freeLatch.m_FreeLaneDir;
                    d = (heldSide ? c : c - reach) + 1;
                }
                else
                {
                    d = c / 2 + 1;
                    dir = (c % 2 == 0) ? kerbDir : -kerbDir;
                }
                int candidatePos = refPos + (int)(dir * d);
                if (candidatePos < 0 || candidatePos >= plan.m_Lanes.Count)
                {
                    continue;
                }
                Entity candidate = plan.m_Lanes[candidatePos];
                if (!EntityManager.Exists(candidate) ||
                    !EntityManager.HasComponent<Game.Net.CarLane>(candidate))
                {
                    continue;
                }
                Game.Net.CarLaneFlags flags = EntityManager.GetComponentData<Game.Net.CarLane>(candidate).m_Flags;
                if ((flags & Game.Net.CarLaneFlags.Forbidden) != 0 ||
                    ((flags & Game.Net.CarLaneFlags.PublicOnly) != 0 && (carFlags & CarFlags.UsePublicTransportLanes) == 0))
                {
                    continue;
                }
                // Committing needs an empty lane; KEEPING the latched one tolerates a little
                // traffic, so the decision cannot flip with every car that enters it.
                // A lane with a PARKING strip alongside is not the escape it looks like:
                // parked cars stand there permanently, and the responder ends up threading
                // between them and the queue instead of getting past (Sebastian). Vehicles
                // that are merely ROLLING do not count as blocking - see kFreeLaneFlowSpeed.
                if (HasParkingBeside(candidate, owner))
                {
                    continue;
                }
                int ahead = m_Ctx.Geometry.LaneVehiclesAhead(candidate, curvePosition, inverted, kFreeLaneFlowSpeed, kFreeLaneClearMeters);
                bool usable = latched && dir == freeLatch.m_FreeLaneDir
                    ? ahead <= kFreeLaneReleaseVehicles
                    : ahead == 0;
                if (usable)
                {
                    freeLatch.m_FreeLaneDir = dir;
                    freeLatch.m_FreeLaneUntilFrame = m_SimulationSystem.frameIndex + kFreeLaneLatchFrames;
                    m_Ctx.States.Stuck[vehicle] = freeLatch;
                    m_FreeLaneDir = dir;
                    // Lean toward the free lane while the change is still pending: the hug
                    // reuses the channel's own steering path, so pointing it at the free lane
                    // has the responder easing over instead of into the middle of the queue.
                    m_ChannelHugDir = dir;
                    return dir;
                }
            }
            // The scan ran over every candidate lane of this segment and none of them is usable -
            // not even under the relaxed rule a latched choice gets. THAT is what releases the
            // commitment, and the only thing that does.
            m_FreeLaneDir = 0f;
            if (freeLatch.m_FreeLaneDir != 0f)
            {
                freeLatch.m_FreeLaneDir = 0f;
                freeLatch.m_FreeLaneUntilFrame = 0u;
                m_Ctx.States.Stuck[vehicle] = freeLatch;
            }
            return 0f;
        }

        /// <summary>Drops cached plans for segments no responder has driven for a while. The TTL
        /// inside GetChannelPlan handles road edits; this just bounds the cache.</summary>
        public void Prune(uint frame)
        {
            if (m_ChannelCache.Count == 0)
            {
                return;
            }
            m_ChannelCachePrune.Clear();
            foreach (KeyValuePair<(Entity, bool), ChannelPlan> entry in m_ChannelCache)
            {
                if (frame - entry.Value.m_Frame > 2048u)
                {
                    m_ChannelCachePrune.Add(entry.Key);
                }
            }
            for (int i = 0; i < m_ChannelCachePrune.Count; i++)
            {
                m_ChannelCache.Remove(m_ChannelCachePrune[i]);
            }
            m_ChannelCachePrune.Clear();
            // The parking-strip answers are keyed by lane and have no TTL of their own - a lane
            // that was rebuilt or deleted would otherwise sit here for the rest of the session.
            EntityMapPrune.PruneDead(EntityManager, m_ParkingBeside, m_ParkingPrune);
        }

        private readonly List<Entity> m_ParkingPrune = new List<Entity>();
        public ChannelPlanner(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Central-channel corridor for wide roads (>= kChannelMinLanes same-direction lanes):
        /// the road parts around a CENTRAL seam instead of one pinned to the far-left edge.
        /// Lanes left of the seam clear physically left, lanes right clear physically right,
        /// with the offset growing toward the edges (graduated cascade) so the middle seam
        /// opens even when the road is packed solid. The seam is derived from the SEGMENT (its
        /// same-direction lane count), not from the responder's own lane, so every responder on
        /// the segment threads the identical seam - a cluster of ambulances shares one corridor.
        /// Returns false for narrow roads so the caller falls back to the classic left seam.
        /// When computeHug is true (the responder's CURRENT lane), sets m_ChannelHugDir so it
        /// hugs toward the seam.
        /// </summary>
        public bool AddCentralChannel(Entity refLane, float minPos, bool inverted, float startOffset, bool computeHug)
        {
            if (!EntityManager.HasComponent<Owner>(refLane) ||
                !EntityManager.HasComponent<Game.Net.CarLane>(refLane))
            {
                return false;
            }
            Entity owner = EntityManager.GetComponentData<Owner>(refLane).m_Owner;
            bool laneInverted = (EntityManager.GetComponentData<Game.Net.CarLane>(refLane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            ChannelPlan plan = m_Ctx.Channel.GetChannelPlan(owner, laneInverted);
            if (plan == null || !plan.m_IsChannel)
            {
                return false; // narrow road (or no lane group) - caller uses the classic left seam
            }
            for (int p = 0; p < plan.m_Lanes.Count; p++)
            {
                Entity lane = plan.m_Lanes[p];
                if (!EntityManager.Exists(lane))
                {
                    continue; // stale cached lane (road rebuilt) - skip until the TTL refresh
                }
                m_Ctx.Corridor.AddCorridorLane(lane, minPos, plan.m_PushDir[p], inverted, startOffset, onPath: true, pushMeters: plan.m_PushMeters[p]);
            }
            if (computeHug)
            {
                int refPos = plan.m_Lanes.IndexOf(refLane);
                if (refPos >= 0)
                {
                    // Thread the seam: hug toward it - physical right if left of the seam, else left.
                    m_ChannelHugDir = refPos <= plan.m_Seam ? 1f : -1f;
                }
            }
            return true;
        }

        /// <summary>Cached per-(segment, direction) channel plan; recomputed at most once per
        /// kChannelCacheTtl frames. Shared by every responder on the segment, which is what makes
        /// the wide-road channel affordable (the SubLane scan used to run per responder per tick).</summary>
        public ChannelPlan GetChannelPlan(Entity owner, bool laneInverted)
        {
            uint frame = m_SimulationSystem.frameIndex;
            (Entity, bool) key = (owner, laneInverted);
            if (m_ChannelCache.TryGetValue(key, out ChannelPlan plan) && frame - plan.m_Frame < kChannelCacheTtl)
            {
                return plan;
            }
            if (plan == null)
            {
                plan = new ChannelPlan();
                m_ChannelCache[key] = plan;
            }
            m_Ctx.Channel.ComputeChannelPlan(owner, laneInverted, plan, frame);
            return plan;
        }

        /// <summary>Fill (or refresh) a segment's channel plan: order the same-direction car
        /// lanes physical left->right, place the central seam, and assign each lane a graduated
        /// outward push (+ shoulder bonus on the edges). Segment-invariant, so it runs rarely.</summary>
        public void ComputeChannelPlan(Entity owner, bool laneInverted, ChannelPlan plan, uint frame)
        {
            plan.m_Frame = frame;
            plan.m_IsChannel = false;
            plan.m_Lanes.Clear();
            plan.m_PushDir.Clear();
            plan.m_PushMeters.Clear();
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner))
            {
                return;
            }
            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner, isReadOnly: true);
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity sl = subLanes[i].m_SubLane;
                if (!EntityManager.HasComponent<Game.Net.CarLane>(sl) || EntityManager.HasComponent<MasterLane>(sl))
                {
                    continue;
                }
                if (((EntityManager.GetComponentData<Game.Net.CarLane>(sl).m_Flags & Game.Net.CarLaneFlags.Invert) != 0) != laneInverted)
                {
                    continue;
                }
                plan.m_Lanes.Add(sl);
            }
            int n = plan.m_Lanes.Count;
            if (n < kChannelMinLanes)
            {
                return; // narrow road
            }
            // Physical left->right order. Derived from AddCounterEvadeNeighbor: physical-left is
            // the LOWER sublane index exactly when the lane is not inverted; when inverted the
            // sublane index runs right->left, so reverse to make index 0 = physical left.
            if (laneInverted)
            {
                plan.m_Lanes.Reverse();
            }
            int seam = (n - 1) / 2; // gap opens between physical lanes [seam] and [seam+1]
            plan.m_Seam = seam;
            for (int p = 0; p < n; p++)
            {
                bool leftOfSeam = p <= seam;
                float pushDir = leftOfSeam ? -1f : 1f; // physical left / right (positive lanePos = right)
                int d = leftOfSeam ? seam - p : p - (seam + 1);
                float meters = kChannelBaseMeters + kChannelStepMeters * d;
                // The outermost lane on each side may reach onto a shoulder/parking strip when
                // one is there - the AI never treats a parked car as a hard blocker, so the
                // extra room is safe to use and spreads the pack further.
                if (p == 0 || p == n - 1)
                {
                    meters += m_Ctx.Channel.ChannelShoulderBonus(subLanes, plan.m_Lanes[p], laneInverted, leftOfSeam);
                }
                plan.m_PushDir.Add(pushDir);
                plan.m_PushMeters.Add(meters);
            }
            plan.m_IsChannel = true;
        }

        /// <summary>
        /// Extra offset (m) the outermost channel lane may take by reaching onto an adjacent
        /// shoulder / parking strip, if one exists on that physical side. Uses the parking
        /// lane's own width as the available room (capped). Returns 0 when there is no shoulder
        /// there, so a car is never nudged off a road that has none.
        /// </summary>
        public float ChannelShoulderBonus(DynamicBuffer<Game.Net.SubLane> subLanes, Entity outerCarLane, bool laneInverted, bool travelLeftSide)
        {
            int outerIdx = -1;
            for (int i = 0; i < subLanes.Length; i++)
            {
                if (subLanes[i].m_SubLane == outerCarLane) { outerIdx = i; break; }
            }
            if (outerIdx < 0)
            {
                return 0f;
            }
            // travelLeftSide is signed in TRAVEL terms; sublane indices run in the EDGE frame with
            // physical-left at the lower index. Converting between the two is the !laneInverted
            // term here, and it must appear exactly ONCE. Do NOT copy these two lines to a caller
            // that has already converted its side to physical - LateralRoom.Measure did, the two
            // Invert factors cancelled, and the walk ran to the wrong side of every inverted lane.
            bool leftIsLower = !laneInverted;
            int step = (travelLeftSide == leftIsLower) ? -1 : 1;
            for (int i = outerIdx + step; i >= 0 && i < subLanes.Length; i += step)
            {
                Entity sl = subLanes[i].m_SubLane;
                if (!EntityManager.Exists(sl))
                {
                    continue;
                }
                if (EntityManager.HasComponent<Game.Net.ParkingLane>(sl) && EntityManager.HasComponent<Curve>(sl))
                {
                    // Use half the strip's width - enough to visibly reach onto it without
                    // planting the car on the far kerb - capped.
                    return math.min(m_Ctx.PrefabGeometry.LaneWidth(sl) * 0.5f, kChannelShoulderCap);
                }
                // Stop at the first same-direction car lane: the shoulder must be immediately
                // outside the outermost car lane, not across another driving lane.
                if (EntityManager.HasComponent<Game.Net.CarLane>(sl) && !EntityManager.HasComponent<MasterLane>(sl))
                {
                    break;
                }
            }
            return 0f;
        }
    }
}

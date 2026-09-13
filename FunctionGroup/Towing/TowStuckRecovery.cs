using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>Where a hauling truck last was, since when it has not moved, and how often it has been judged wedged there.</summary>
    internal struct TruckRest
    {
        public float3 m_Pos;
        public uint m_Since;
        public uint m_Strikes;
    }

    /// <summary>
    /// Deciding what to do with a tow truck that has stopped moving while hauling.
    ///
    /// This is the price of the path shield. Clearing PathFlags.Failed stops vanilla deleting a
    /// loaded truck, but a vehicle without a route drives dead straight - and one of them drove
    /// straight into a multi-storey car park and parked itself inside the building, still holding
    /// its wreck. Nothing could reach it there: the release-on-failed-path timer never started
    /// (a vehicle with no path at all does not necessarily carry the Failed flag), the orphan sweep
    /// deliberately skips any wreck whose controller is a LIVE recovery truck, and repathing cannot
    /// help because there is no route from inside a building.
    ///
    /// So the test is MOVEMENT, not path state. But movement alone cannot tell "wedged inside a
    /// building" from "queueing in a jam", and that distinction turned out to matter a lot:
    ///
    /// This class used to REPOSITION such a truck - write its Transform, its CarCurrentLane
    /// (m_Lane, m_CurvePosition) and null its m_ChangeLane. Two things were wrong with that. The
    /// small one: a truck merely stuck in traffic was "recovered" onto the lane it was already on,
    /// logged as "0m away", every 900 frames forever (914 recoveries in one 100-minute session,
    /// 904 of them no-ops). The serious one: writing those lane fields by hand bypasses the game's
    /// lane registry - the vehicle stays listed in its old lane's LaneObject buffer - and that is
    /// exactly what the defreeze note in EmergencyEscalation warns about, because it hard-crashes
    /// a Burst job. That session ended in a null-pointer access violation inside the game's
    /// Burst-compiled job library.
    ///
    /// So there is no repositioning any more. A wedged truck standing ON a road is left completely
    /// alone (it is queueing; the deadlock machinery owns that case), and one that is genuinely off
    /// the network - or one that has been motionless through kTowWedgeStrikes checks - has its
    /// wreck handed back to the recovery pipeline and is deleted. Sebastian's call, and the right
    /// trade: losing a truck costs one vehicle, corrupting the lane registry costs the session.
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
        /// once per tick per towing truck; it keeps the movement history itself. Any real movement
        /// also clears the strike count - a truck that is making progress starts from scratch.
        /// </summary>
        public bool IsWedged(Entity truck, float3 pos, uint frame)
        {
            if (!m_Rest.TryGetValue(truck, out TruckRest rest) ||
                math.distancesq(rest.m_Pos.xz, pos.xz) > kTowStuckMeters * kTowStuckMeters)
            {
                m_Rest[truck] = new TruckRest { m_Pos = pos, m_Since = frame, m_Strikes = 0u };
                return false;
            }
            return frame - rest.m_Since >= kTowStuckFrames;
        }

        public void Forget(Entity truck)
        {
            m_Rest.Remove(truck);
        }

        /// <summary>
        /// Is there a real driving lane right where the truck stands? Then it is not wedged in the
        /// geometry, it is waiting in traffic - and nothing here should touch it.
        ///
        /// Read-only on purpose: this asks a question about the network, it does not change the
        /// vehicle. The search box is small because the question is "am I on a road", not "where is
        /// the nearest road"; the distance test is 3D, so a truck sitting a storey above a street
        /// still comes out off-road.
        /// </summary>
        public bool StandsOnRoad(float3 pos, NativeQuadTree<Entity, QuadTreeBoundsXZ> netTree)
        {
            float searchRadius = kTowOnRoadMeters * 4f;
            NativeList<Entity> nets = new NativeList<Entity>(Allocator.Temp);
            try
            {
                AreaIterator iterator = new AreaIterator
                {
                    m_Bounds = new Bounds3(pos - searchRadius, pos + searchRadius),
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
                        if (MathUtils.Distance(laneCurve.m_Bezier, pos, out float _) <= kTowOnRoadMeters)
                        {
                            return true;
                        }
                    }
                }
            }
            finally
            {
                nets.Dispose();
            }
            return false;
        }

        /// <summary>
        /// Records that the truck was found wedged but standing on a road, and re-arms the movement
        /// timer so the next check is another kTowStuckFrames away (that throttle is the only thing
        /// keeping this off the per-tick path).
        ///
        /// Returns true once it has been counted kTowWedgeStrikes times, i.e. it has stood still for
        /// minutes with a road under its wheels: at that point it is not queueing either and the
        /// caller should let it go, rather than leave a loaded truck as a permanent monument that
        /// also blocks every cleanup net behind it.
        /// </summary>
        /// <summary>
        /// The same wedge check for recovery vehicles that are running EMPTY - on their way to a
        /// wreck rather than hauling one.
        ///
        /// Until 0.1.13 nothing covered them: <see cref="IsWedged"/> is called from the follow
        /// pass, and the follow pass only ever iterates TOWED wrecks. A truck that wedged itself
        /// before reaching its wreck therefore stood forever - and it took the wreck with it,
        /// because a claimed wreck waits on kWreckClaimedGiveUpAge (~30 min) for a truck that is
        /// never coming.
        ///
        /// Two deliberate differences from the loaded case:
        ///  - BOTH conditions must hold before an empty truck is removed: off the network AND the
        ///    full kTowWedgeStrikes. Neither is conclusive alone. Off the road is innocent for an
        ///    empty vehicle - a depot yard reads as off-road, since StandsOnRoad deliberately
        ///    ignores parking aisles - and standing ON a road is not being wedged either, it is
        ///    queueing, which is why the loaded case stopped deleting for it in this same release.
        ///    Removing a queueing truck unassigns its wreck, the dispatcher sends the next one
        ///    into the identical jam, and the round repeats; when the jam is the wreck's own
        ///    tailback each round makes the next worse.
        ///  - there is no wreck to release, so nothing is handed back; the wreck simply loses its
        ///    claim when the truck goes and the dispatcher sends someone else - which is exactly
        ///    why that must stay rare.
        /// </summary>
        public void SweepEmptyTrucks(EntityQuery truckQuery, uint frame, Setting setting)
        {
            NativeArray<Entity> trucks = truckQuery.ToEntityArray(Allocator.Temp);
            NativeQuadTree<Entity, QuadTreeBoundsXZ> netTree = default;
            bool treeFetched = false;
            try
            {
                for (int i = 0; i < trucks.Length; i++)
                {
                    Entity truck = trucks[i];
                    if (m_Ctx.TrucksWithLoad.Contains(truck))
                    {
                        continue; // hauling - the follow pass owns this one
                    }
                    // Only a truck actually EN ROUTE to a wreck. One going home or parked is
                    // allowed to stand still for as long as it likes.
                    Game.Vehicles.MaintenanceVehicle maintenance =
                        EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(truck);
                    if ((maintenance.m_State & MaintenanceVehicleFlags.Returning) != 0 ||
                        EntityManager.HasComponent<Game.Vehicles.ParkedCar>(truck))
                    {
                        continue;
                    }
                    Entity target = EntityManager.GetComponentData<Target>(truck).m_Target;
                    if (target == Entity.Null || !EntityManager.Exists(target) ||
                        (!EntityManager.HasComponent<Damaged>(target) &&
                         !EntityManager.HasComponent<Game.Events.InvolvedInAccident>(target)))
                    {
                        continue;
                    }
                    float3 pos = EntityManager.GetComponentData<Transform>(truck).m_Position;
                    if (!IsWedged(truck, pos, frame))
                    {
                        continue;
                    }
                    if (!treeFetched && m_Ctx.NetSearch != null)
                    {
                        netTree = m_Ctx.NetSearch.GetNetSearchTree(readOnly: true, out JobHandle netDeps);
                        netDeps.Complete();
                        treeFetched = true;
                    }
                    bool onRoad = treeFetched && StandsOnRoad(pos, netTree);
                    if (setting.VerboseLogging && !onRoad)
                    {
                        Mod.Log.Info($"[towstuck] empty truck={truck.Index} motionless OFF the road at " +
                            $"({pos.x:F0},{pos.z:F0}) heading for wreck={target.Index} - counting strikes");
                    }
                    // Strikes are counted either way - they are a useful signal that something
                    // is wrong at this spot - but ONLY an off-network truck is ever removed.
                    //
                    // Standing on a road does not end an empty truck's run any more, for exactly
                    // the reason it stopped ending a loaded one in this same release: it is not
                    // wedged in the geometry, it is queueing. Deleting it unassigns the wreck,
                    // the dispatcher sends the next truck into the identical jam, and that one is
                    // removed in turn - "it did not resolve a single blockage; it fed trucks into
                    // one", now with the extra twist that the jam is often the WRECK'S OWN
                    // tailback, so each round makes the next one worse.
                    //
                    // Measured 2026-09-13 on a blocked main roundabout: six deletions inside
                    // twelve minutes and forty-six recovery vehicles standing in one contiguous
                    // id block, six of them already at the full 20 strikes.
                    //
                    // The wedged-in-geometry case that this pass exists for is unaffected: a
                    // truck genuinely off the network still earns the full kTowWedgeStrikes and
                    // is then removed. That combination - off the road AND motionless for ~5
                    // minutes - is not something a truck waiting in a depot yard reaches.
                    bool earnedGiveUp = NoteOnRoadStrike(truck, frame, setting, target);
                    if (earnedGiveUp && !onRoad)
                    {
                        if (setting.VerboseLogging)
                        {
                            Mod.Log.Info($"[towstuck] empty truck={truck.Index} never reached wreck={target.Index} " +
                                "and is OFF the network - deleting it so the wreck can be dispatched again");
                        }
                        m_Ctx.SafeDelete(truck);
                    }
                }
            }
            finally
            {
                trucks.Dispose();
                if (treeFetched)
                {
                    m_Ctx.NetSearch.AddNetSearchTreeReader(default(JobHandle));
                }
            }
        }

        /// <summary>
        /// Count one strike against a motionless truck and say whether it has earned the give-up.
        ///
        /// <paramref name="job"/> is diagnostic only and may be Entity.Null: without it the line
        /// names a truck and nothing else, and a stuck truck cannot be tied back to the wreck it
        /// was sent to - which is exactly where the 316664 case ran out of evidence. With it, one
        /// grep answers "whose job was it, how far short did it stop, and where".
        /// </summary>
        public bool NoteOnRoadStrike(Entity truck, uint frame, Setting setting, Entity job = default)
        {
            m_Rest.TryGetValue(truck, out TruckRest rest);
            rest.m_Since = frame;
            rest.m_Strikes++;
            m_Rest[truck] = rest;
            bool giveUp = rest.m_Strikes >= kTowWedgeStrikes;
            if (setting.VerboseLogging)
            {
                string where = string.Empty;
                if (EntityManager.HasComponent<Transform>(truck))
                {
                    float3 p = EntityManager.GetComponentData<Transform>(truck).m_Position;
                    where = $" pos=({p.x:F0},{p.z:F0})";
                    if (job != Entity.Null && EntityManager.Exists(job) &&
                        EntityManager.HasComponent<Transform>(job))
                    {
                        float3 t = EntityManager.GetComponentData<Transform>(job).m_Position;
                        where += $" wreck={job.Index}@({t.x:F0},{t.z:F0}) dist={math.distance(p.xz, t.xz):F0}";
                    }
                    else if (job != Entity.Null)
                    {
                        where += $" wreck={job.Index}";
                    }
                }
                Mod.Log.Info($"[towstuck] truck={truck.Index} motionless on a road (strike " +
                    $"{rest.m_Strikes}/{kTowWedgeStrikes}){where} - " +
                    (giveUp ? "giving up on it" : "left alone, it is queueing"));
            }
            return giveUp;
        }
    }
}

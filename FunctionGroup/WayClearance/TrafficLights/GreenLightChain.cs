using System.Collections.Generic;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;
using Game.Vehicles;
using static ClearTheWay.Tuning;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;

namespace ClearTheWay.FunctionGroup.WayClearance.TrafficLights
{
    /// <summary>
    /// Every traffic-light write this mod makes, in one place: the chain of signals along a
    /// vehicle's route is turned green ahead of it.
    ///
    /// Two separate mechanisms, and the difference matters:
    ///  - The PETITION (LaneSignal.m_Priority/m_Petitioner) is the game's own request channel.
    ///    TrafficLightSystem serves the highest-priority petitioner, but only at the NEXT phase
    ///    switch - so a petition never overrode a green already running for the cross direction
    ///    (the "no effect on green phases" symptom).
    ///  - The PREEMPTION hard-sets the lane to Go, bypassing the phase logic. It is re-applied
    ///    every tick because the light systems recompute the signal, and it only works when the
    ///    write lands AFTER them.
    ///
    /// That last point is why the decisions here are QUEUED rather than written straight away.
    /// The corridor pass runs before the car navigation (its lane writes must be read in the same
    /// tick), but a light write from there is overwritten by TrafficLightSystem again. So the pass
    /// records what it wants and <see cref="GreenLightSystem"/> flushes the queue after the light
    /// system has had its say. Before this split, the mod solved the same problem by registering
    /// the whole corridor system a SECOND time - which made it run twice per tick, with the two
    /// runs contradicting each other (see the double-registration hunt, 2026-07-21).
    ///
    /// The priority the caller passes decides who wins: emergency corridor (kSignalPriority 108)
    /// beats a recovery vehicle (kAssistSignalPriority 106), which in turn beats normal civilian
    /// traffic (100). Only the emergency side ever preempts.
    ///
    /// Vanilla petitions on its own, but only as far as the (stopped =&gt; tiny) braking distance
    /// reaches, so an emergency vehicle waiting behind a queue never got its light switched at
    /// all - which is why this walks the whole corridor / the upcoming navigation lanes instead.
    /// </summary>
    internal sealed class GreenLightChain
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        /// <summary>One queued light write: who asked, for which lane, how hard.</summary>
        private struct Request
        {
            public Entity m_Vehicle;
            public Entity m_Lane;
            public sbyte m_Priority;
            public bool m_Preempt;
        }

        /// <summary>This tick's requests, flushed (and cleared) by GreenLightSystem. Only lanes
        /// that actually carry a LaneSignal ever land here, so on a city without traffic lights
        /// along the corridors this stays empty.</summary>
        private readonly List<Request> m_Pending = new List<Request>(64);

        public GreenLightChain(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Emergency handling for one corridor lane: petition it at emergency priority and, when
        /// the responder is actually being held up at the junction (<paramref name="preempt"/>),
        /// hard-set it to Go as well. Lanes without a signal are ignored.
        /// </summary>
        public void ClearCorridorLane(Entity vehicle, Entity lane, bool preempt)
        {
            Enqueue(vehicle, lane, kSignalPriority, preempt);
        }

        /// <summary>Petition one lane's light at the given priority - no preemption, so it takes
        /// effect at the junction's next phase switch.</summary>
        public void Petition(Entity vehicle, Entity lane, sbyte priority)
        {
            Enqueue(vehicle, lane, priority, preempt: false);
        }

        /// <summary>
        /// Petition the lane the vehicle is on plus the next few lanes of its route. Covers both
        /// the red light whose phase a jam is starving and the crossing ahead it never dared to
        /// enter - the two places a recovery vehicle gets stranded.
        /// </summary>
        public void PetitionRouteAhead(Entity vehicle, Entity currentLane, sbyte priority, int upcomingLanes)
        {
            Petition(vehicle, currentLane, priority);
            if (!EntityManager.HasBuffer<CarNavigationLane>(vehicle))
            {
                return;
            }
            DynamicBuffer<CarNavigationLane> navLanes = EntityManager.GetBuffer<CarNavigationLane>(vehicle, isReadOnly: true);
            int upcoming = math.min(upcomingLanes, navLanes.Length);
            for (int i = 0; i < upcoming; i++)
            {
                Petition(vehicle, navLanes[i].m_Lane, priority);
            }
        }

        private void Enqueue(Entity vehicle, Entity lane, sbyte priority, bool preempt)
        {
            if (lane == Entity.Null || !EntityManager.HasComponent<LaneSignal>(lane))
            {
                return;
            }
            m_Pending.Add(new Request
            {
                m_Vehicle = vehicle,
                m_Lane = lane,
                m_Priority = priority,
                m_Preempt = preempt
            });
        }

        /// <summary>
        /// Applies everything the passes asked for this tick and empties the queue. Called from
        /// <see cref="GreenLightSystem"/>, i.e. after the light systems have recomputed their
        /// signals - a write from the corridor pass itself would simply be overwritten again.
        ///
        /// Requests are applied in the order they were made; the priority comparison decides who
        /// keeps the lane, so several responders converging on one junction do not fight over it.
        /// </summary>
        public void Flush()
        {
            for (int i = 0; i < m_Pending.Count; i++)
            {
                Request request = m_Pending[i];
                if (!EntityManager.Exists(request.m_Lane) ||
                    !EntityManager.HasComponent<LaneSignal>(request.m_Lane))
                {
                    continue; // lane went away between the decision and this write
                }
                LaneSignal signal = EntityManager.GetComponentData<LaneSignal>(request.m_Lane);
                bool write = false;
                if (signal.m_Priority < request.m_Priority)
                {
                    signal.m_Priority = request.m_Priority;
                    signal.m_Petitioner = request.m_Vehicle;
                    write = true;
                }
                if (request.m_Preempt && signal.m_Signal != LaneSignalType.Go)
                {
                    signal.m_Signal = LaneSignalType.Go;
                    write = true;
                }
                if (write)
                {
                    EntityManager.SetComponentData(request.m_Lane, signal);
                }
            }
            m_Pending.Clear();
        }
    }
}

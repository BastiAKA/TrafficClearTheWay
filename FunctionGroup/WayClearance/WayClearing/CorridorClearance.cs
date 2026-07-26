using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using ClearTheWay.FunctionGroup.WayClearance.TrafficLights;

namespace ClearTheWay
{
    /// <summary>
    /// Walks this tick's corridor lanes and acts on each one: pull its cars aside, petition (and
    /// where needed hard-force) its traffic light, and push its queue through a dead junction.
    ///
    /// The light writes themselves live in GreenLightChain; what this pass decides is WHICH lanes
    /// get them and when a mere petition is not enough. And after ~3 minutes stuck at a signalised
    /// junction the light is treated as broken outright: the queue in front is given a forward
    /// budget so a dead junction cannot trap the responder and everyone behind it indefinitely.
    ///
    /// Also grants the oncoming pass speed, which belongs here because it depends on how clear
    /// the oncoming lane was found to be, not on anything the steering stage decides.
    /// </summary>
    internal sealed class EscalationCorridor
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private VehicleControl m_Control => m_Ctx.VehicleControl;
        private CorridorBuilder m_Corridor => m_Ctx.Corridor;

        public EscalationCorridor(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Runs the per-corridor-lane actions for one responder this tick.</summary>
        public void Apply(ref EscalationState s, ref CarCurrentLane currentLane, Setting setting, Entity vehicle, uint frame)
        {
            s.m_Pushed = 0;
            for (int i = 0; i < m_Corridor.CorridorLanes.Count; i++)
            {
                CorridorLane corridorLane = m_Corridor.CorridorLanes[i];
                // While fully on the oncoming carriageway, leave the own-direction queue
                // undisturbed - it just zips past. While merging back in, form the corridor
                // but do NOT hold the front cars (they need to move to open the gap). Green
                // lights are requested along the whole path in every state.
                if (!s.m_FullCrossover)
                {
                    s.m_Pushed += m_Ctx.Push.PullCarsAside(vehicle, corridorLane, frame, s.m_Evade >= EvadeStage.Hard, allowHold: !s.m_Merging, drainAhead: s.m_DrainAhead, shakeStuck: s.m_Desperate, evadeMeters: s.m_Evade == EvadeStage.Deep ? kDeepEvadeMeters : kEvadeMeters);
                }

                // Turn the traffic lights along the corridor green (see GreenLightChain). The
                // lane is preempted outright - not merely petitioned - while the responder is
                // held up at the junction, because a petition alone waits for the next phase
                // switch.
                if (setting.ForceGreenLights && corridorLane.m_OnPath)
                {
                    m_Ctx.Lights.ClearCorridorLane(vehicle, corridorLane.m_Lane,
                        preempt: s.m_LightOverride || s.m_EmergencySpeed < kOvertakeSlowSpeed);
                }

                // ...and physically push the queue in front through the (now green) junction:
                // grant the cars ahead on this on-path lane a forward speed budget so they roll
                // on instead of sitting at a light the game thinks is still red.
                if (s.m_LightOverride && corridorLane.m_OnPath)
                {
                    m_Ctx.Hold.PushQueueForward(vehicle, corridorLane);
                }
            }

            // While zipping past on the oncoming lane the vehicle is logically still behind
            // its own queue, so navigation would brake it. Grant a brisk speed only when the
            // oncoming lane is genuinely clear ahead; if a (held) oncoming car is close, ease
            // up to it at a crawl instead of lurching forward at pass speed and aborting.
            if (s.m_FullCrossover)
            {
                if (s.m_OncomingClearAhead >= kOncomingRunClear)
                {
                    // Near the dispatch target no speed floor: vanilla navigation is braking
                    // toward the path end there, and a granted floor pushes the vehicle PAST
                    // it - missed arrival, repath, and another lap around the block.
                    if (s.m_EmergencySpeed < kOncomingPassSpeed && !s.m_NearArrivalTarget)
                    {
                        m_Control.SetFloor(vehicle, kOncomingPassSpeed, hasTarget: false, default, default);
                    }
                }
                else
                {
                    m_Control.SetCeiling(vehicle, kOncomingCrawlSpeed);
                }
            }
            // Merging back in: nudge the few cars just ahead of the merge point forward so a
            // gap opens for the emergency vehicle to slot into (the corridor already moved
            // them aside; this moves them along). Let it merge at its own navigated speed.
            else if (s.m_Merging)
            {
                m_Ctx.Hold.NudgeMergeGap(vehicle, currentLane);
            }
        }
    }
}

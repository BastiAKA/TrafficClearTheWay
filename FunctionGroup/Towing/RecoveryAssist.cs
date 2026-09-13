using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding;
using ClearTheWay.FunctionGroup.WayClearance.TrafficLights;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes;
// Game.Net has a LaneGeometry of its own - ours wins here, like the CarLaneFlags alias above.
using LaneGeometry = ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding.M_LaneGeometry;

namespace ClearTheWay
{
    /// <summary>
    /// Make-way handling for RECOVERY vehicles (tow trucks and maintenance vans on their way to
    /// a wreck) - deliberately a much gentler thing than the emergency corridor.
    ///
    /// Normally they just get cars ahead easing aside, and only while they are actually slow: an
    /// earlier version built the corridor for the whole trip, which disturbed traffic along the
    /// entire route and made trucks in convoy shove EACH OTHER onto the kerb.
    ///
    /// The full emergency toolkit (hard evade, squeeze, forced green) is unlocked only when a
    /// truck is genuinely wedged NEAR its wreck. Two lessons are baked into that gate:
    ///  - "Stuck" is measured as lack of PROGRESS toward the wreck, not as low speed. In
    ///    stop-and-go traffic every creep forward reset a speed-based timer, so the escalation
    ///    never fired once.
    ///  - It is also range-limited, because straight-line distance is no progress measure across
    ///    a city: a route that curves away increases it, so trucks 1.7 km out counted as
    ///    permanently stuck and shoved traffic aside along their entire route.
    /// And once even escalation buys nothing for ~90 s the recovery is in a vanilla gridlock the
    /// toolkit cannot break, so it stops entirely rather than churn traffic for nothing.
    /// </summary>
    internal sealed class RecoveryAssist
    {
        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = true;

        private readonly EntityManager EntityManager;
        private readonly LaneGeometry m_Geometry;
        private readonly VehicleControl m_Control;
        private readonly CorridorBuilder m_Corridor;
        private readonly WayClearanceContext m_Ctx;

        /// <summary>Per-truck progress watch toward its wreck; drives the escalation and give-up.</summary>
        private readonly Dictionary<Entity, AssistProgress> m_AssistProgress = new Dictionary<Entity, AssistProgress>();
        private readonly List<Entity> m_PruneScratch = new List<Entity>();

        public RecoveryAssist(EntityManager entityManager, LaneGeometry geometry, VehicleControl control,
            CorridorBuilder corridor, WayClearanceContext ctx)
        {
            EntityManager = entityManager;
            m_Geometry = geometry;
            m_Control = control;
            m_Corridor = corridor;
            m_Ctx = ctx;
        }

        /// <summary>Drops progress watches for trucks that finished or vanished.</summary>
        public void Prune(uint frame)
        {
            if (m_AssistProgress.Count == 0)
            {
                return;
            }
            m_PruneScratch.Clear();
            foreach (KeyValuePair<Entity, AssistProgress> entry in m_AssistProgress)
            {
                // NO age-based prune any more, and that is the whole point of this method.
                //
                // It used to drop records older than 8192 frames, with a comment saying the
                // threshold was chosen generously so a wedged truck could still reach its give-up.
                // The numbers said otherwise: the give-up is kAssistGiveUpFrames = 5400, so the
                // record died 2792 frames LATER - and dropping it resets m_SinceFrame, which
                // restarts the whole clock from zero. A permanently wedged recovery vehicle
                // therefore escalated (evade + squeeze + forced greens) for 5400 frames, went
                // quiet for 2792, and then started over, forever. Measured 2026-08-09 on truck
                // 53038: "STOPPED ESCALATING - no progress for 133s" and 50 seconds later
                // "no progress for 32s - escalating" on the same standstill, cycling for minutes
                // while churning the traffic around it.
                //
                // So the record now lives exactly as long as the run it belongs to. It is dropped
                // when the vehicle is gone, or when it is no longer on a wreck run at all - which
                // is the natural end and cannot reset anything, because there is nothing left to
                // reset.
                if (!EntityManager.Exists(entry.Key) || !IsOnWreckRun(entry.Key))
                {
                    m_PruneScratch.Add(entry.Key);
                }
            }
            for (int i = 0; i < m_PruneScratch.Count; i++)
            {
                m_AssistProgress.Remove(m_PruneScratch[i]);
            }
            m_PruneScratch.Clear();
        }

        /// <summary>
        /// Is this recovery vehicle still on a run to a wreck it could actually recover?
        ///
        /// Shared by the prune above and by <see cref="ProcessAssistVehicleImpl"/>, deliberately:
        /// the two used to disagree, and the disagreement is what produced the 53038 case. The
        /// assist pass accepted "Damaged OR InvolvedInAccident" as a target, but a wreck keeps
        /// Damaged after a tow truck has hooked it - it only loses InvolvedInAccident. So a SECOND
        /// truck sent to the same wreck never noticed the job was gone: TowCoupling refused it
        /// ("skipped: not a valid target - invAcc=0", which is correct), while this pass went on
        /// escorting it, parked it 20 m short and escalated there for minutes.
        ///
        /// The decisive test is therefore the hook, not the damage: a wreck whose Controller is a
        /// live recovery vehicle other than this one is somebody else's job and this vehicle has
        /// nothing left to do.
        /// </summary>
        private bool IsOnWreckRun(Entity vehicle)
        {
            if (!EntityManager.HasComponent<Target>(vehicle) ||
                !EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(vehicle))
            {
                return false;
            }
            if ((EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(vehicle).m_State &
                 MaintenanceVehicleFlags.Returning) != 0)
            {
                return false;
            }
            Entity target = EntityManager.GetComponentData<Target>(vehicle).m_Target;
            if (target == Entity.Null || !EntityManager.Exists(target) ||
                (!EntityManager.HasComponent<Damaged>(target) &&
                 !EntityManager.HasComponent<Game.Events.InvolvedInAccident>(target)))
            {
                return false;
            }
            // Already on somebody's hook? Then this vehicle is the redundant second truck.
            if (EntityManager.HasComponent<Controller>(target))
            {
                Entity carrier = EntityManager.GetComponentData<Controller>(target).m_Controller;
                if (carrier != Entity.Null && carrier != target && carrier != vehicle &&
                    EntityManager.Exists(carrier) && !EntityManager.HasComponent<Deleted>(carrier) &&
                    EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(carrier))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Gentle corridor for a recovery/tow vehicle heading to a crash: cars ahead pull
        /// aside (no squeezing, oncoming use, overtaking or forced greens - that stays
        /// reserved for real emergency vehicles). Only vehicles that can recover a damaged
        /// car and are actually on the way to one qualify.
        /// </summary>
        public void ProcessAssistVehicle(Entity vehicle, float side, uint frame)
        {
            using (ModProfiler.Sample(kProfile, "RecoveryAssist"))
            {
                ProcessAssistVehicleImpl(vehicle, side, frame);
            }
        }

        private void ProcessAssistVehicleImpl(Entity vehicle, float side, uint frame)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            if (!EntityManager.HasComponent<MaintenanceVehicleData>(prefab) ||
                (EntityManager.GetComponentData<MaintenanceVehicleData>(prefab).m_MaintenanceType & MaintenanceType.Vehicle) == 0)
            {
                return;
            }
            // One predicate for "is this vehicle on a wreck run", shared with the prune - including
            // the test that was missing here: a wreck already on ANOTHER recovery vehicle's hook is
            // no longer this one's job. Without it the redundant second truck kept being escorted
            // to a wreck that TowCoupling had long since refused, and escalated where it stood.
            if (!IsOnWreckRun(vehicle))
            {
                // This exit was completely silent, and that is why a truck excluded here looked
                // identical in the log to one the assist simply never helped: no [assist] line
                // either way. Throttled per tick-block rather than per vehicle - the interesting
                // signal is "a convoy of trucks is being skipped at this wreck", not each one.
                if (Mod.Setting.VerboseLogging && frame % 300u == 0u &&
                    EntityManager.HasComponent<Target>(vehicle))
                {
                    Entity skipped = EntityManager.GetComponentData<Target>(vehicle).m_Target;
                    Mod.Log.Info($"[assist] veh={vehicle.Index} skipped: not on a wreck run " +
                        $"(target={skipped.Index}) - redundant second truck, returning, or target no longer a wreck");
                }
                m_AssistProgress.Remove(vehicle);
                return;
            }
            Entity target = EntityManager.GetComponentData<Target>(vehicle).m_Target;

            CarCurrentLane currentLane = EntityManager.GetComponentData<CarCurrentLane>(vehicle);
            if (!EntityManager.Exists(currentLane.m_Lane))
            {
                return;
            }
            // A recovery vehicle on a wreck run gets the same despawn protection as the
            // emergency vehicles - our corridor slows it down, the game must not delete it.
            m_Control.ClearStuck(vehicle);

            // ... and the same arrival assist, which it never had. The last few metres are exactly
            // where a recovery run stalls: the path end is the lane the WRECK is standing on, so
            // it is occupied by definition, and nothing existed to end the approach. Truck 2851374
            // covered 86 m in 42 s and then sat at dist=10 for over two minutes, motionless, with
            // the full emergency escalation running and not one [arrive] line - because
            // TryArrivalAssist was only ever called from the responder pass.
            //
            // Forcing the arrival is what lets MaintenanceVehicleAISystem run its PathEndReached
            // branch and start the recovery. The returning guard added inside TryArrivalAssist is
            // what keeps that safe; if recovery vehicles are ever seen vanishing near a wreck,
            // this call is the first thing to take back out.
            if (m_Ctx.Arrival.TryArrivalAssist(vehicle, ref currentLane, side, frame, out _))
            {
                return;
            }

            // Release the FixedLane pin of a finished or abandoned overtake (below), the same
            // way the emergency pass does. Nothing else would ever clear it for a recovery
            // vehicle, and a permanently pinned lane is worse than never changing lane at all.
            Dictionary<Entity, uint> forcedChanges = m_Ctx.States.ForcedChanges;
            if (forcedChanges.TryGetValue(vehicle, out uint changeStartFrame) &&
                (currentLane.m_ChangeLane == Entity.Null || frame - changeStartFrame > kForcedChangeTimeout))
            {
                currentLane.m_LaneFlags &= ~CarLaneFlags.FixedLane;
                forcedChanges.Remove(vehicle);
                EntityManager.SetComponentData(vehicle, currentLane);
            }

            // Progress bookkeeping first: once it has failed to get meaningfully CLOSER to
            // its wreck for ~30 s, escalate from the gentle corridor to the emergency
            // toolbox (hard evade + squeeze + green lights) - a full accident jam never
            // opens up by politeness alone. Measured by distance, not by speed: in a
            // stop-and-go jam it inches forward constantly, so a speed-based timer reset
            // on every creep and the escalation never fired.
            bool stuckLong = false;
            float3 assistPos = EntityManager.GetComponentData<Transform>(vehicle).m_Position;
            float targetDistance = math.distance(
                assistPos.xz,
                EntityManager.GetComponentData<Transform>(target).m_Position.xz);
            if (!m_AssistProgress.TryGetValue(vehicle, out AssistProgress progress) ||
                targetDistance < progress.m_BestDistance - kAssistProgressMeters)
            {
                progress.m_SinceFrame = frame;                 // real progress - start over
                progress.m_BestDistance = targetDistance;
                progress.m_NoProgressLogged = false;               // no longer wedged - re-arm the diagnostic
            }
            else
            {
                progress.m_BestDistance = math.min(progress.m_BestDistance, targetDistance);
                // Only near the wreck does "no progress" actually mean stuck in ITS jam. Out
                // on the route it just means the road bends away from the target, so the
                // escalation must never fire there (it did: trucks 1.7 km out escalating
                // non-stop and pushing traffic aside across the whole city).
                stuckLong = frame - progress.m_SinceFrame >= kAssistStuckFrames &&
                    targetDistance <= kAssistEscalateRange;
            }
            // Physical movement watchdog, deliberately independent of the wreck's position and
            // therefore valid at any distance from it. This is what catches the truck wedged out
            // on the route, which the distance-gated watch above structurally cannot see.
            if (progress.m_MovedSinceFrame == 0u ||
                math.distance(assistPos.xz, progress.m_LastPos.xz) >= kAssistMotionlessMeters)
            {
                progress.m_LastPos = assistPos;
                progress.m_MovedSinceFrame = frame;
            }
            bool motionless = frame - progress.m_MovedSinceFrame >= kAssistMotionlessFrames;
            // ... and a vehicle that has physically not moved is stuck WHEREVER it stands, so it
            // gets the full toolbox, not a reduced one. The assumption behind kAssistEscalateRange
            // - "out on the route the jam is not the wreck's jam" - does not hold when the wreck
            // lands on a main junction: a blocked roundabout backs traffic up for the better part
            // of a kilometre, and every metre of that tailback IS the wreck's doing. Sebastian
            // watched exactly that (2026-09-13, one of the city's main roundabouts).
            //
            // This does NOT bring back the failure kAssistEscalateRange was added for. That one
            // was driven by the DISTANCE metric and fired for trucks that were driving perfectly
            // well 1.7 km out, merely along a road that bent away from the target. A truck moving
            // at all covers kAssistMotionlessMeters within kAssistMotionlessFrames and never
            // reaches this branch; only one that has genuinely stood still for half a minute does.
            stuckLong |= motionless;

            float assistSpeed = math.length(EntityManager.GetComponentData<Moving>(vehicle).m_Velocity);

            // Rolling watch: how long has it been DRIVING without a stall. Any drop below
            // kAssistRollingSpeed re-arms it, so a car length of stop-and-go creep never counts.
            if (assistSpeed >= kAssistRollingSpeed)
            {
                if (progress.m_RollingSinceFrame == 0u)
                {
                    progress.m_RollingSinceFrame = frame;
                }
            }
            else
            {
                progress.m_RollingSinceFrame = 0u;
            }
            bool rollingFreely = progress.m_RollingSinceFrame != 0u &&
                frame - progress.m_RollingSinceFrame >= kAssistRightsHoldFrames;

            // How long it has ACTUALLY been stuck. m_SinceFrame is the DISTANCE timer and keeps
            // running while the vehicle drives perfectly well along a road that bends away from
            // the wreck, so on its own it can hand a truck a half-spent - or already expired -
            // clock the moment it finally does get stuck. The shorter of the two is the honest one.
            uint stuckFor = motionless
                ? math.min(frame - progress.m_SinceFrame, frame - progress.m_MovedSinceFrame)
                : frame - progress.m_SinceFrame;

            // Escalation latch. Every stage below used to hang straight off timers that reset on
            // the first metre of progress, so in a stop-and-go queue the vehicle earned its
            // rights, rolled one car length, lost them and stopped again - and the corridor it had
            // just built was torn up on each flip. Once earned, the escalation is therefore HELD
            // until the vehicle has driven freely for kAssistRightsHoldFrames. The tier is latched
            // with it, so the emergency rights do not silently drop back to the polite ones either.
            if (stuckLong)
            {
                progress.m_Latched = true;
                if (stuckFor >= kAssistFullRightsFrames)
                {
                    progress.m_LatchedFull = true;
                }
            }
            else if (progress.m_Latched)
            {
                if (rollingFreely)
                {
                    progress.m_Latched = false;
                    progress.m_LatchedFull = false;
                }
                else
                {
                    stuckLong = true;   // held: it is moving, but not yet convincingly
                }
            }
            bool fullRights = progress.m_LatchedFull;

            // Convoy discipline, the recovery-vehicle twin of the responder rule in
            // EmergencyEscalation - which the assist path never had. Every recovery vehicle is a
            // corridor owner in its own right, so a column of them all escalating at once hugs
            // the same free lane and shoves the standing colleague in front aside. PullCarsAside
            // allows exactly that: its maintenance-vehicle protection only covers a ROLLING one,
            // and in the jam this pass exists for, none of them is rolling. The result is the
            // responder fan-out with tow trucks - consecutive ids, all crowding the same offset.
            //
            // Latched for the same reason the responder one is: the raw sighting flips as the
            // column closes up and pulls apart, and every flip would swing the whole behaviour.
            //
            // The exception mirrors the responder rule too (there: !s.m_Desperate): once a member
            // has earned the emergency tier, the front is demonstrably not passing anything
            // either, and freezing the column then deadlocks it. So fullRights overrides.
            bool colleagueAhead = assistSpeed < kMaxEvadeSpeed &&
                m_Ctx.Obstruction.HasMaintenanceAhead(vehicle, currentLane, kConvoyAheadRange);
            if (colleagueAhead)
            {
                progress.m_ColleagueUntilFrame = frame + kConvoyHoldFrames;
            }
            bool behindColleague = !fullRights &&
                (colleagueAhead || frame < progress.m_ColleagueUntilFrame);
            m_AssistProgress[vehicle] = progress;

            // Give-up cap: past ~90 s of zero progress within escalate range the recovery is
            // wedged in an unwinnable deadlock. What is pointless there is CHURNING TRAFFIC for
            // it - corridor, hard evade, forced greens - so all of that stops. Getting itself out
            // of the lane does not churn anything and is exactly what it still needs: an earlier
            // version returned outright here and thereby switched off the one escape that could
            // have freed it (field log: four trucks, all "GAVE UP", not a single lane change
            // attempted afterwards). Diagnostic once, then every 10 s.
            if (stuckLong && stuckFor >= kAssistGiveUpFrames)
            {
                if (Mod.Setting.VerboseLogging && (!progress.m_NoProgressLogged || frame % 600u == 0u))
                {
                    LogAssistNoProgress(vehicle, target, currentLane, targetDistance, progress, frame);
                    progress.m_NoProgressLogged = true;
                    m_AssistProgress[vehicle] = progress;
                }
                TryGetAround(vehicle, ref currentLane, assistSpeed, targetDistance, frame);
                TryUnblock(vehicle, assistSpeed, targetDistance, frame);
                return;
            }

            // The corridor only when the vehicle actually needs help: rolling slowly or out
            // of progress. A cruising truck gets NO special treatment ("Rettungsgassenmodus
            // die ganze Zeit an") - that disturbed traffic along the whole route for
            // nothing, and tow trucks in convoy kept shoving each other aside.
            // A recovery vehicle queues like everybody else until it is genuinely STUCK.
            //
            // The gate used to be speed-based (slower than kAssistHelpSpeed => build the corridor),
            // which meant every maintenance van crawling through ordinary traffic shoved the cars
            // around it aside on sight. Sebastian: they should just drive along normally as long as
            // they are not stuck. Being slow in a queue is not being stuck - the progress watch
            // above (kAssistStuckFrames, distance-gated by kAssistEscalateRange) is what decides
            // that, and it keeps running for every assist vehicle regardless of this return.
            if (!stuckLong)
            {
                return;
            }

            // desperate: true - we only get here at all when the truck is genuinely stuck
            // (stuckLong above), and that is exactly the state in which the corridor shape must
            // stay re-plannable so every escape keeps being reachable. The speed hysteresis and
            // the free-lane latch inside ChooseShape still apply; only the "a working corridor
            // keeps its shape" lock is waived, which for a wedged truck is what we want.
            // Queued behind a colleague: no corridor of its own. The one at the front does the
            // passing, this one keeps a normal line - and the tick costs nothing either, which
            // matters when a whole column is stuck at once.
            if (!behindColleague)
            {
                m_Corridor.BuildCorridor(vehicle, ref currentLane, Mod.Setting, side, assistSpeed, desperate: true);
                for (int i = 0; i < m_Corridor.CorridorLanes.Count; i++)
                {
                    CorridorLane corridorLane = m_Corridor.CorridorLanes[i];
                    // Deep evade once promoted: cars ahead clear FULLY onto the kerb
                    // (kDeepEvadeMeters) instead of the ordinary offset. On a narrow street
                    // kEvadeMeters leaves a car's body across the gap, which is precisely the
                    // state a wedged truck cannot get out of - the same reason the responder
                    // path has a Deep stage at all.
                    m_Ctx.Push.PullCarsAside(vehicle, corridorLane, frame, hardEvade: true,
                        evadeMeters: fullRights ? kDeepEvadeMeters : kEvadeMeters);

                    if (Mod.Setting.ForceGreenLights && corridorLane.m_OnPath)
                    {
                        // Petition either way; preempt the phase outright only once promoted.
                        m_Ctx.Lights.ClearCorridorLane(vehicle, corridorLane.m_Lane, preempt: fullRights);
                    }

                    // ... and physically roll the queue in front through the now-green junction.
                    // Without this the cars ahead sit at a light the game still believes is red,
                    // so the green buys the truck behind them nothing at all.
                    if (fullRights && corridorLane.m_OnPath)
                    {
                        m_Ctx.Hold.PushQueueForward(vehicle, corridorLane);
                    }
            }
            }

            // Last lever, and the one with a real cost to oncoming traffic: cross onto the
            // opposing carriageway to get round the block. Only once promoted, and
            // TryOncomingDisplacement applies its own sight-distance rules on top
            // (kOncomingDesperateCommitSight), so it commits only when the gap is genuinely
            // there. desperate: true - a truck that has stood three minutes beside a wreck is
            // in exactly the state the flag describes.
            if (fullRights && Mod.Setting.UseOncomingLane)
            {
                m_Ctx.Desperate.TryOncomingDisplacement(vehicle, ref currentLane, side, desperate: true, frame,
                    out _, out _, out _);
                EntityManager.SetComponentData(vehicle, currentLane);
            }

            // Move ITSELF over. This was missing entirely, and it is why nothing else worked: a
            // recovery vehicle built the corridor, pushed everyone else - and stood dead centre
            // in its own lane (every wedged truck in the log reported lanePos 0,0). An emergency
            // vehicle hugs the corridor seam, and that hug is half of the lateral clearance a
            // squeeze needs; without it the separation gate can never open, no matter how far the
            // blocker moves. Only while stuck, and toward the free lane when there is one.
            float hugDir = m_Corridor.FreeLaneDir != 0f ? m_Corridor.FreeLaneDir
                : (m_Corridor.ChannelHugDir != 0f ? m_Corridor.ChannelHugDir : -side);
            // The same crossable room the emergency corridor gets: a recovery truck wedged on a
            // narrow street may hug onto the tram bed or green strip beside it too. Inherited by
            // simply asking LateralRoom - nothing tow-specific to keep in step here. Only in the
            // stuckLong branch, so a truck making normal progress still keeps its lane.
            float hugMeters = kEdgeMeters + m_Ctx.Room.CrossableMeters(currentLane.m_Lane, hugDir, frame);
            float hugUnits = math.min(hugMeters, m_Ctx.PrefabGeometry.MaxLateralMeters(vehicle)) / m_Ctx.PrefabGeometry.LateralSlack(vehicle, currentLane.m_Lane);
            float hugPos = math.lerp(currentLane.m_LanePosition, hugDir * hugUnits, kPullRate);
            // ... but not while queued behind a colleague: the hug toward the free lane IS the
            // fan-out. The column keeps its line and only the front vehicle swings out.
            if (!behindColleague && math.abs(hugPos - currentLane.m_LanePosition) > 0.001f)
            {
                currentLane.m_LanePosition = hugPos;
                EntityManager.SetComponentData(vehicle, currentLane);
            }

            // Squeeze past a pulled-aside/boxed-in blocker exactly like an emergency
            // vehicle (separation-gated, IgnoreBlocker) ...
            if (EntityManager.HasComponent<Blocker>(vehicle) &&
                Mod.Setting.SqueezePastBlockers &&
                m_Ctx.Squeeze.TrySqueezePastBlocker(vehicle, ref currentLane, frame))
            {
                EntityManager.SetComponentData(vehicle, currentLane);
            }
            // ... and, when the way past is physically impossible, go AROUND.
            TryGetAround(vehicle, ref currentLane, assistSpeed, targetDistance, frame);

            // ... and petition the lights green. Two tiers, because what they cost everyone
            // else at that junction differs by an order of magnitude.
            //
            // Tier 1 (up to kAssistFullRightsFrames): recovery priority (106). Civilian petitions
            // (100) lose, emergency petitions (108) still win, and there is no preemption - it
            // takes effect at the junction's next phase switch. A tow truck is not normally worth
            // cutting a running green short for.
            //
            // Tier 2 (past it): the same rights an ambulance has - priority 108 AND preemption, so
            // the lane it sits on is hard-set to Go rather than waiting for the phase. A recovery
            // vehicle that has made no progress for three minutes next to a wreck is not in a
            // queue that is about to clear; it is in the jam the wreck itself created, and the
            // starved junction ahead is usually what is holding it.
            if (fullRights)
            {
                m_Ctx.Lights.ClearCorridorLane(vehicle, currentLane.m_Lane, preempt: true);
                m_Ctx.Lights.PetitionRouteAhead(vehicle, currentLane.m_Lane, kSignalPriority, upcomingLanes: 2);
            }
            else
            {
                m_Ctx.Lights.PetitionRouteAhead(vehicle, currentLane.m_Lane, kAssistSignalPriority, upcomingLanes: 2);
            }
            if (Mod.Setting.VerboseLogging && frame % 180u == 0u)
            {
                Mod.Log.Info($"[assist] veh={vehicle.Index} no progress for {(frame - progress.m_SinceFrame) / 60u}s " +
                    $"(dist={targetDistance:F0} best={progress.m_BestDistance:F0}" +
                    (motionless ? $" motionless={(frame - progress.m_MovedSinceFrame) / 60u}s" : string.Empty) +
                    ") - escalating " +
                    (fullRights ? "(evade+squeeze+GREEN-PREEMPT, emergency rights)" : "(evade+squeeze+green)"));
            }
        }

        /// <summary>
        /// Change lane to get around whatever is in the way. The one escape the rest of the
        /// toolkit cannot provide: a recovery vehicle queued behind ANOTHER one. Our corridor
        /// only eases a standing colleague aside, and two vehicles nose to tail in one lane may
        /// still leave no gap - then going around is all that is left. Density relaxed and the
        /// free lane preferred, exactly like a stuck responder: a recovery run is supposed to be
        /// let through a jam, not to queue in it. Kept running even after the give-up, because
        /// this costs the surrounding traffic nothing.
        /// </summary>
        private void TryGetAround(Entity vehicle, ref CarCurrentLane currentLane, float assistSpeed,
            float targetDistance, uint frame)
        {
            if (!Mod.Setting.OvertakeStuckTraffic ||
                assistSpeed >= kOvertakeSlowSpeed ||
                currentLane.m_ChangeLane != Entity.Null ||
                m_Ctx.States.ForcedChanges.ContainsKey(vehicle))
            {
                return;
            }
            Dictionary<Entity, StuckState> stuckStates = m_Ctx.States.Stuck;
            stuckStates.TryGetValue(vehicle, out StuckState assistStuck);
            if (frame < assistStuck.m_NextOvertakeFrame ||
                !m_Ctx.LaneChange.TryOvertakeLaneChange(vehicle, ref currentLane, frame, turnHint: 0,
                    relaxDensity: true, preferDir: m_Corridor.FreeLaneDir))
            {
                return;
            }
            assistStuck.m_NextOvertakeFrame = frame + kOvertakeCooldown;
            assistStuck.m_LastSeenFrame = frame;
            stuckStates[vehicle] = assistStuck;
            EntityManager.SetComponentData(vehicle, currentLane);
            if (Mod.Setting.VerboseLogging)
            {
                Mod.Log.Info($"[assist] veh={vehicle.Index} changing lane to get around " +
                    $"(dist={targetDistance:F0} free={m_Corridor.FreeLaneDir:F0})");
            }
        }

        /// <summary>
        /// The very last resort: clear the ONE vehicle standing directly in front of a recovery
        /// vehicle that has been deadlocked past its give-up.
        ///
        /// Sebastian verified by hand in-game that removing a single car ahead of the leading tow
        /// truck is enough: it triggers the route recompute that gives every vehicle in the column
        /// its options back, and the whole chain starts moving. That is why this deliberately does
        /// NOT touch the vehicles around the crash (their repath is what the path-end guard exists
        /// to PREVENT: at a full block the pathfinder hands out a disposal path and the queue
        /// evaporates) - only the single blocker, well away from the wreck.
        ///
        /// Two stages, gentlest first:
        ///  1. SOFT - flag the rig Obsolete (the same lever as the emergency lead-release): the
        ///     game re-localizes and re-paths it, and if its route is genuinely dead it despawns.
        ///     No structural change, so none of the trailer-delete crash surface. Preferred.
        ///  2. HARD - only if that exact rig is STILL standing in the way after
        ///     kSacrificeShieldWindow (the soft flag bought nothing), fall back to the proven
        ///     deletion. A rig is removed as a WHOLE - tractor plus every trailer in its layout -
        ///     because deleting only the part in the way leaves a dangling LayoutElement, the exact
        ///     shape of the crash we spent an evening on.
        ///
        /// Never an emergency vehicle, never another recovery vehicle (it is on a mission of its
        /// own), never a crashed one (that is the accident, not an obstacle), and rate-limited by
        /// kUnblockRetryFrames so a truck cannot chew through a whole queue.
        /// </summary>
        private void TryUnblock(Entity vehicle, float assistSpeed, float targetDistance, uint frame)
        {
            if (!Mod.Setting.UnblockRecoveryVehicles ||
                assistSpeed >= kUnblockMaxSpeed ||
                !EntityManager.HasComponent<Blocker>(vehicle))
            {
                return;
            }
            m_AssistProgress.TryGetValue(vehicle, out AssistProgress progress);

            Entity blocker = EntityManager.GetComponentData<Blocker>(vehicle).m_Blocker;
            if (blocker == Entity.Null || !EntityManager.Exists(blocker))
            {
                return;
            }
            // A trailer in the way means the RIG is in the way - work on its tractor.
            Entity head = VehicleTrailerExt.ResolveHead(EntityManager, blocker);
            if (!EntityManager.Exists(head) || !EntityManager.HasComponent<Car>(head) ||
                EntityManager.HasComponent<Deleted>(head))
            {
                return;
            }
            if ((EntityManager.GetComponentData<Car>(head).m_Flags & CarFlags.Emergency) != 0 ||
                EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(head) ||
                EntityManager.HasComponent<Game.Events.InvolvedInAccident>(head) ||
                EntityManager.HasComponent<Damaged>(head))
            {
                return;
            }
            // Only a blocker that is truly standing - a rolling one will clear on its own.
            if (EntityManager.HasComponent<Moving>(head) &&
                math.lengthsq(EntityManager.GetComponentData<Moving>(head).m_Velocity) > kUnblockMaxSpeed * kUnblockMaxSpeed)
            {
                return;
            }

            // The blocker must have held its position for a while before we act on it. Two cars
            // standing abreast make the game's Blocker flip between them every few seconds -
            // IgnoreBlocker is cleared automatically whenever the blocker changes, so neither is
            // ever pushed past and the pair deadlocks. Field case 2026-08-09, truck 53038: within
            // four minutes the blocker read 691036, 681259, 171302, 171232, 1718616, 676882, and
            // the soft flag landed on 681259 purely because it happened to be current that tick.
            // Flagging one member of a rotating cast achieves nothing and costs a stranger's car,
            // so require the SAME entity for kUnblockStableFrames first.
            if (head != progress.m_BlockerCandidate)
            {
                progress.m_BlockerCandidate = head;
                progress.m_BlockerSince = frame;
                m_AssistProgress[vehicle] = progress;
                return;
            }
            if (frame - progress.m_BlockerSince < kUnblockStableFrames)
            {
                return;
            }

            // Stage 1 (soft) unless THIS exact rig was already flagged and is still in the way.
            bool alreadySoftFlagged = head == progress.m_SoftFlaggedHead && progress.m_SoftFlagFrame != 0u;
            if (!alreadySoftFlagged)
            {
                // Rate-limit soft attempts the same way as the removal, so a truck cannot flag a
                // whole queue in one burst.
                if (progress.m_LastUnblockFrame != 0u && frame - progress.m_LastUnblockFrame < kUnblockRetryFrames)
                {
                    return;
                }
                // Skip a rig mid lane-change: forcing Obsolete on a foreign car while m_ChangeLane
                // is set risks dangling it in the change lane's LaneObject buffer (see
                // EmergencyEscalation.TryReleaseLeadBlocker for the full reasoning).
                if (!EntityManager.HasComponent<CarCurrentLane>(head))
                {
                    return;
                }
                CarCurrentLane headLane = EntityManager.GetComponentData<CarCurrentLane>(head);
                if (headLane.m_ChangeLane != Entity.Null)
                {
                    return;
                }
                headLane.m_LaneFlags |= CarLaneFlags.Obsolete;
                EntityManager.SetComponentData(head, headLane);
                m_Ctx.States.Sacrifice[head] = frame + kSacrificeShieldWindow;
                progress.m_SoftFlaggedHead = head;
                progress.m_SoftFlagFrame = frame;
                progress.m_LastUnblockFrame = frame;
                m_AssistProgress[vehicle] = progress;
                if (Mod.Setting.VerboseLogging)
                {
                    Mod.Log.Info($"[assist] veh={vehicle.Index} deadlocked at dist={targetDistance:F0} - " +
                        $"flagged blocker={head.Index} Obsolete (soft) - will remove it if it stays put");
                }
                return;
            }

            // Stage 2 (hard): the soft flag has had its window and the same rig is still standing.
            if (frame - progress.m_SoftFlagFrame < kSacrificeShieldWindow)
            {
                return; // still inside the grace window - give the gentle path time to work
            }

            int parts = 0;
            if (EntityManager.HasBuffer<LayoutElement>(head))
            {
                DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(head, isReadOnly: true);
                for (int i = 0; i < layout.Length; i++)
                {
                    Entity part = layout[i].m_Vehicle;
                    if (part != Entity.Null && EntityManager.Exists(part) && part != head)
                    {
                        SafeDelete(part);
                        parts++;
                    }
                }
            }
            SafeDelete(head);
            progress.m_LastUnblockFrame = frame;
            progress.m_SoftFlaggedHead = Entity.Null;
            progress.m_SoftFlagFrame = 0u;
            m_AssistProgress[vehicle] = progress;
            if (Mod.Setting.VerboseLogging)
            {
                Mod.Log.Info($"[assist] veh={vehicle.Index} deadlocked at dist={targetDistance:F0} - " +
                    $"removed blocker={head.Index} (+{parts} trailer part(s)) after the soft flag failed");
            }
        }

        /// <summary>Deletes a vehicle the way the towing passes do: a Moving-less entity that still
        /// carries a Game.Simulation.UpdateFrame null-derefs UpdateGroupSystem, so strip it first.</summary>
        private void SafeDelete(Entity entity)
        {
            if (EntityManager.HasComponent<Game.Simulation.UpdateFrame>(entity))
            {
                EntityManager.RemoveComponent<Game.Simulation.UpdateFrame>(entity);
            }
            EntityManager.AddComponent<Deleted>(entity);
        }

        /// <summary>
        /// Full diagnostic of a recovery vehicle wedged in an unwinnable deadlock (the give-up cap
        /// in ProcessAssistVehicle). Dumps how long it has been stuck, truck/wreck positions and
        /// distance, the truck's lane + IgnoreBlocker state, and WHO is directly blocking it plus
        /// that blocker's own state (is it another wreck? an emergency vehicle? a maintenance
        /// truck? and is IT moving?). This is the data needed to later actually diagnose and break
        /// these gridlocks instead of abandoning the recovery to the wreck's give-up despawn.
        /// </summary>
        private void LogAssistNoProgress(Entity vehicle, Entity wreck, CarCurrentLane lane,
            float targetDistance, AssistProgress progress, uint frame)
        {
            float speed = EntityManager.HasComponent<Moving>(vehicle)
                ? math.length(EntityManager.GetComponentData<Moving>(vehicle).m_Velocity) : 0f;
            float3 vpos = EntityManager.GetComponentData<Transform>(vehicle).m_Position;
            float3 wpos = EntityManager.HasComponent<Transform>(wreck)
                ? EntityManager.GetComponentData<Transform>(wreck).m_Position : default;
            bool ignore = (lane.m_LaneFlags & CarLaneFlags.IgnoreBlocker) != 0;

            string blockerInfo = "none";
            if (EntityManager.HasComponent<Blocker>(vehicle))
            {
                Blocker b = EntityManager.GetComponentData<Blocker>(vehicle);
                Entity be = b.m_Blocker;
                if (be != Entity.Null && EntityManager.Exists(be))
                {
                    float bspeed = EntityManager.HasComponent<Moving>(be)
                        ? math.length(EntityManager.GetComponentData<Moving>(be).m_Velocity) : 0f;
                    bool bEmerg = EntityManager.HasComponent<Car>(be) &&
                        (EntityManager.GetComponentData<Car>(be).m_Flags & CarFlags.Emergency) != 0;
                    bool bWreck = EntityManager.HasComponent<Game.Events.InvolvedInAccident>(be);
                    bool bMaint = EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(be);
                    blockerInfo = $"{be.Index} type={b.m_Type} spd={bspeed:F1} emerg={(bEmerg ? 1 : 0)} " +
                        $"wreck={(bWreck ? 1 : 0)} maint={(bMaint ? 1 : 0)}";
                }
                else
                {
                    blockerInfo = $"{be.Index}(gone)";
                }
            }

            // NOT "GAVE UP", which is what this line said until 0.1.13 and which cost real
            // debugging time: it reads as "this truck is finished", but the vehicle keeps driving,
            // keeps trying to get around and routinely couples afterwards (field log 2026-08-04,
            // truck 73363: two of these lines, then a successful hookup). What has actually
            // stopped is the ESCALATION - the corridor, the hard evade, the forced greens, i.e.
            // everything that churns surrounding traffic. And the trigger is straight-line
            // distance to the wreck, which a truck taking a curving route legitimately fails to
            // reduce while moving perfectly well, so a moving truck can land here without being
            // stuck at all. spd= is the field that tells the two apart.
            Mod.Log.Info($"[assist] veh={vehicle.Index} STOPPED ESCALATING - no progress for " +
                $"{(frame - progress.m_SinceFrame) / 60u}s (it may still be driving; check spd), " +
                $"dist={targetDistance:F0} best={progress.m_BestDistance:F0} spd={speed:F1} " +
                $"pos=({vpos.x:F0},{vpos.z:F0}) wreck={wreck.Index}@({wpos.x:F0},{wpos.z:F0}) " +
                $"lane={lane.m_Lane.Index} lanePos={lane.m_LanePosition:F1} ignoreBlocker={(ignore ? 1 : 0)} " +
                $"blockedBy=[{blockerInfo}]");
        }

    }
}

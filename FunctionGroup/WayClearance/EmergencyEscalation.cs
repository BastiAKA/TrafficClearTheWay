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
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding;

namespace ClearTheWay
{
    /// <summary>
    /// The escalation chain applied to ONE responder each tick, in order of increasing
    /// intervention:
    ///
    ///   clear the stuck flag -> assist its arrival if it is at its target -> build the corridor
    ///   -> try the oncoming carriageway -> force the lights green and push the queue through
    ///   -> pull cars aside -> change lane around the blockage -> hug the seam -> squeeze past
    ///   -> manage its own speed -> return its lane offset to centre.
    ///
    /// Each stage is gated so it only engages when the cheaper ones have not worked, which is why
    /// so much of this method is boolean gates rather than actions: nearly every one of them was
    /// added to stop an earlier stage firing where it did harm.
    /// </summary>
    internal sealed class EmergencyEscalation
    {
        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = false;

        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private M_LaneGeometry m_Geometry => m_Ctx.Geometry;
        private VehicleControl m_Control => m_Ctx.VehicleControl;
        private CorridorBuilder m_Corridor => m_Ctx.Corridor;
        private Dictionary<Entity, StuckState> m_StuckStates => m_Ctx.States.Stuck;
        private Dictionary<Entity, uint> m_ForcedChanges => m_Ctx.States.ForcedChanges;

        public EmergencyEscalation(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        public void ProcessEmergencyVehicle(Entity vehicle, Setting setting, float side, uint frame)
        {
            using (ModProfiler.Sample(kProfile, "EmergencyEscalation"))
            {
                ProcessEmergencyVehicleImpl(vehicle, setting, side, frame);
            }
        }

        private void ProcessEmergencyVehicleImpl(Entity vehicle, Setting setting, float side, uint frame)
        {
            CarCurrentLane currentLane = EntityManager.GetComponentData<CarCurrentLane>(vehicle);
            if (!EntityManager.Exists(currentLane.m_Lane))
            {
                return;
            }

            // One struct carries this responder's state through the stages below; see
            // EscalationState for why it is a struct rather than a parameter list.
            EscalationState s = default;
            s.m_Vehicle = vehicle;
            s.m_Side = side;
            s.m_Frame = frame;

            // Our artificial slowdown (crawling / squeezing / oncoming pass) makes the game's
            // StuckMovingObjectSystem flag the emergency vehicle as stuck - and a stuck
            // police car / ambulance / fire engine whose repath then fails gets DELETED by
            // its AI (PoliceCarAISystem etc.). We run before those AIs each frame, so clear
            // the flag here: the vehicle is being actively helped through, not truly stuck.
            m_Control.ClearStuck(vehicle);

            // Arrival assist: emergency vehicles that get very close to their dispatch target
            // but can never reach the EXACT path end (end lane occupied by the wreck/queue,
            // repath moved it) otherwise orbit the block forever. Force the vanilla
            // close-arrival once the near-counter trips; the vehicle then runs its own
            // arrival branch next tick, so the rest of this pass is moot.
            if (m_Ctx.Arrival.TryArrivalAssist(vehicle, ref currentLane, side, frame, out s.m_NearArrivalTarget))
            {
                EntityManager.SetComponentData(vehicle, currentLane);
                return;
            }

            s.m_Changed = false;

            // Release the FixedLane pin of a forced overtake once the change is done
            // (or abandoned), so the vanilla lane selection can merge the vehicle back.
            if (m_ForcedChanges.TryGetValue(vehicle, out uint changeStartFrame))
            {
                if (currentLane.m_ChangeLane == Entity.Null || frame - changeStartFrame > kForcedChangeTimeout)
                {
                    currentLane.m_LaneFlags &= ~CarLaneFlags.FixedLane;
                    m_ForcedChanges.Remove(vehicle);
                    s.m_Changed = true;
                }
            }

            // Own speed feeds the escalation gates below AND decides whether to part the whole
            // carriageway (the central channel is for slow clusters, not a free-flowing
            // responder), so compute it before BuildCorridor.
            s.m_EmergencySpeed = math.length(EntityManager.GetComponentData<Moving>(vehicle).m_Velocity);
            m_Corridor.BuildCorridor(vehicle, ref currentLane, setting, side, s.m_EmergencySpeed);

            // While the emergency vehicle has been stuck behind the same blocker for a
            // while (state from the previous tick), cars right in front of it clear out
            // harder - onto the sidewalk/parking strip if needed. All of this only while
            // actually crawling - rolling traffic gets the normal corridor, nothing more.
            m_StuckStates.TryGetValue(vehicle, out s.m_PreviousStuck);
            bool hardEvade = s.m_EmergencySpeed < kMaxEvadeSpeed &&
                             (frame < s.m_PreviousStuck.m_SqueezeUntilFrame ||
                              (s.m_PreviousStuck.m_Blocker != Entity.Null &&
                               frame - s.m_PreviousStuck.m_BlockerSinceFrame >= kEvadeAfterFrames));
            // Third stage. Half a minute behind the SAME blocker means the ordinary corridor has
            // demonstrably failed - a partial offset still leaves the blocker's body across the
            // gap, so the responder never gets through and the whole street stands still (field
            // report on a narrow custom road). Then cars ahead go fully onto the pavement.
            bool deepEvade = hardEvade &&
                             s.m_PreviousStuck.m_Blocker != Entity.Null &&
                             frame - s.m_PreviousStuck.m_BlockerSinceFrame >= kDeepEvadeAfterFrames;
            s.m_Evade = deepEvade ? EvadeStage.Deep : (hardEvade ? EvadeStage.Hard : EvadeStage.Soft);
            s.m_Desperate = s.m_PreviousStuck.m_Blocker != Entity.Null &&
                             frame - s.m_PreviousStuck.m_BlockerSinceFrame >= kDesperateFrames;

            // Convoy discipline: when ANOTHER emergency vehicle is right ahead on the same
            // lane, this one queues behind it instead of escalating. Without this, a wave of
            // ambulances to the same block each swings to the far left, and together they
            // fan out across the entire road - blocking both directions AND each other
            // (logged: ~10 responders with consecutive ids all at lanePos -2.99 in one
            // street). The colleague at the front does the passing; the queue keeps a
            // normal corridor line. Only checked while slow - a rolling convoy needs nothing.
            s.m_BehindColleague = s.m_EmergencySpeed < kMaxEvadeSpeed &&
                m_Ctx.Obstruction.HasEmergencyAhead(vehicle, currentLane, kConvoyAheadRange);
            // ...but only while the convoy is actually PROGRESSING. Once a member has been stuck
            // for ~10 s (desperate) the front is not passing anything either - freezing the whole
            // column then deadlocks it (observed: a member stuck 5+ min at sep 0.91, one blocker
            // short of a squeeze, because hardEvade=false also disabled the oncoming escape). A
            // desperate member regains hardEvade (=> oncoming pass / harder evade), while the hug
            // stays capped below (behindColleague is still true) so it does not fan out.
            if (s.m_BehindColleague && !s.m_Desperate)
            {
                s.m_Evade = EvadeStage.Soft; // a queueing convoy member must not fan out at all
            }

            // Safe defreeze - the same recompute a manual traffic-light toggle triggers. An
            // emergency vehicle stuck mid lane-change (m_ChangeLane set) that has been stopped
            // behind the same blocker for a long time is frozen OUT of canManeuver (see below)
            // and can never recover on its own: the change cannot finish (target lane jammed)
            // and the game only clears it on completion. We must NOT null m_ChangeLane ourselves
            // - that leaves the vehicle dangling in the change lane's LaneObject buffer and
            // hard-crashes a Burst job (proven from CarNavigationHelpers.CurrentLaneCache, which
            // is the ONLY registry-safe way to move a car between lane buffers). Instead flag the
            // current lane Obsolete: next tick CarNavigationSystem.UpdateStopped sees the flag,
            // calls TryFindCurrentLane (which re-localizes the car from its position, clears
            // m_ChangeLane through its own CurrentLaneCache + LaneObjectCommandBuffer so the
            // registry stays consistent) and repaths it. Exactly the game's own recovery path
            // for an invalid lane. Throttled per vehicle so it does not repath every tick.
            if (currentLane.m_ChangeLane != Entity.Null && !s.m_NearArrivalTarget &&
                s.m_EmergencySpeed < 0.5f && s.m_PreviousStuck.m_Blocker != Entity.Null &&
                frame - s.m_PreviousStuck.m_BlockerSinceFrame >= kDefreezeStuckFrames &&
                (s.m_PreviousStuck.m_LastDefreezeFrame == 0u ||
                 frame - s.m_PreviousStuck.m_LastDefreezeFrame >= kDefreezeCooldown))
            {
                currentLane.m_LaneFlags |= CarLaneFlags.Obsolete;
                s.m_Changed = true;
                s.m_PreviousStuck.m_LastDefreezeFrame = frame;
                m_StuckStates[vehicle] = s.m_PreviousStuck;
                if (setting.VerboseLogging)
                {
                    Mod.Log.Info($"[defreeze] veh={vehicle.Index} stuck mid-change for " +
                        $"{frame - s.m_PreviousStuck.m_BlockerSinceFrame}f - flagged lane Obsolete for a clean recompute");
                }
            }

            s.m_CanManeuver = currentLane.m_ChangeLane == Entity.Null &&
                (currentLane.m_LaneFlags & (CarLaneFlags.ParkingSpace | CarLaneFlags.Area | CarLaneFlags.TransformTarget | CarLaneFlags.EndReached | CarLaneFlags.Roundabout)) == 0 &&
                EntityManager.HasComponent<Game.Net.CarLane>(currentLane.m_Lane);

            // Try the oncoming carriageway FIRST. If the vehicle can get fully onto it, the
            // whole queue on its own side does NOT need to form a corridor - it simply zips
            // past on the empty oncoming lane and only needs room where it merges back in
            // (which happens automatically once oncoming traffic approaches: the state drops
            // to "straddling" and the corridor kicks in there).
            // Once committed to the oncoming lane, keep the maneuver alive REGARDLESS of
            // speed. Otherwise the brisk pass-speed pushes the vehicle over the hardEvade
            // speed cap, which disables the oncoming pass, which drops the speed, which
            // re-enables it - a feedback oscillation that showed up as the lateral wobble.
            bool committedOncoming = frame < s.m_PreviousStuck.m_OncomingActiveUntil;
            s.m_OncomingState = 0;
            s.m_OncomingClearAhead = 0f;
            // Near the dispatch target no NEW oncoming maneuver is started (the vehicle must
            // brake to a stop there, not swing out); an already-committed one keeps being
            // serviced so its merge-back/hold logic stays alive.
            if (s.m_CanManeuver && setting.UseOncomingLane && (s.m_Evade >= EvadeStage.Hard || committedOncoming) &&
                (!s.m_NearArrivalTarget || committedOncoming) &&
                (committedOncoming || s.m_EmergencySpeed < kMaxOncomingSpeed || math.abs(currentLane.m_LanePosition) > 1f))
            {
                s.m_OncomingState = m_Ctx.Desperate.TryOncomingDisplacement(vehicle, ref currentLane, side, s.m_Desperate, frame, out s.m_OncomingClearAhead);
                if (s.m_OncomingState > 0)
                {
                    s.m_Changed = true;
                }
            }
            s.m_FullCrossover = s.m_OncomingState == 2;
            s.m_Merging = s.m_OncomingState == 1;

            // Gridlock drain: fully stopped behind the same blocker for a long time (desperate)
            // with no way through (not crossing to oncoming). Let the queue AHEAD drive off to
            // clear the jam instead of freezing it in place - the case where a tram or a boxed-in
            // car in front just needs to roll on and the responder can never squeeze past it.
            s.m_DrainAhead = s.m_Desperate && s.m_OncomingState == 0 && s.m_EmergencySpeed < 0.5f;
            // German 3-minute rule: sat at a signalised junction for ~3 min => treat the light
            // as broken. Hard-force the corridor lanes to Go and push the queue through, so a
            // dead/gridlocked junction can never trap the responder (and its queue) forever.
            s.m_LightOverride = s.m_PreviousStuck.m_Blocker != Entity.Null && s.m_EmergencySpeed < 0.5f &&
                frame - s.m_PreviousStuck.m_BlockerSinceFrame >= kLightOverrideFrames;
            // Absolute last resort: the junction stays dead even with the green override AND the
            // queue push. Two ways out of it, tried in this order under one shared throttle:
            //
            //  1. Clear the PLUG. If a plain civilian car sits directly ahead, flag THAT car
            //     Obsolete: the game re-localizes and re-paths it, and if its route is genuinely
            //     dead it despawns - deliberately. One sacrificed car frees the lane and the whole
            //     queue behind (including us) rolls on with NO repath flood. Preferred, because it
            //     gets us THROUGH instead of the long way around.
            //  2. Reroute OURSELVES. If there is nothing civilian to sacrifice ahead (dead junction,
            //     or the car ahead is another responder / a tow truck / a wreck we must not touch),
            //     flag our OWN lane Obsolete so CarNavigationSystem re-paths us AROUND the junction.
            //
            // Only when not already recovering a lane change (that path owns the Obsolete flag
            // above), and throttled hard by the shared defreeze cooldown so neither floods the
            // pathfinder.
            if (s.m_LightOverride && currentLane.m_ChangeLane == Entity.Null && !s.m_NearArrivalTarget &&
                (s.m_PreviousStuck.m_LastDefreezeFrame == 0u ||
                 frame - s.m_PreviousStuck.m_LastDefreezeFrame >= kDefreezeCooldown))
            {
                if (!TryReleaseLeadBlocker(vehicle, frame, setting))
                {
                    currentLane.m_LaneFlags |= CarLaneFlags.Obsolete;
                    s.m_Changed = true;
                    if (setting.VerboseLogging)
                    {
                        Mod.Log.Info($"[lightunstuck] veh={vehicle.Index} stuck {frame - s.m_PreviousStuck.m_BlockerSinceFrame}f " +
                            "at a dead junction - forcing a route recompute");
                    }
                }
                s.m_PreviousStuck.m_LastDefreezeFrame = frame;
                m_StuckStates[vehicle] = s.m_PreviousStuck;
            }
            m_Ctx.CorridorRun.Apply(ref s, ref currentLane, setting, vehicle, frame);

            // Lateral stages: turn hint, lane takeover, hug/lean/roundabout steering.
            m_Ctx.Steering.Apply(ref s, ref currentLane, setting, vehicle, side, frame);

            // Longitudinal stages: squeeze, speed management, lane-return, and the write.
            m_Ctx.SpeedStage.Apply(ref s, ref currentLane, setting, vehicle, side, frame);
        }

        /// <summary>
        /// Last-resort plug removal. Flags the civilian car directly ahead of a hard-stuck
        /// responder Obsolete so CarNavigationSystem re-localizes and re-paths it next tick; if its
        /// route is genuinely dead (the blockage leaves no way through) the game despawns it, which
        /// is accepted - one sacrificed car frees the lane and the queue behind rolls on with no
        /// repath flood.
        ///
        /// Strict target rules - only ever a plain civilian car:
        ///  - never another emergency vehicle or a tow/maintenance vehicle (those are protected),
        ///  - never a wreck (RecoveryAssist owns crashed vehicles),
        ///  - never a car mid lane-change (flagging Obsolete while m_ChangeLane is set is only safe
        ///    for the vehicle the game is about to re-localize itself; forcing it on a foreign car
        ///    risks dangling it in the change lane's LaneObject buffer - see the defreeze note),
        ///  - only a car that is actually STOPPED, i.e. a genuine plug, not one merely crawling.
        ///
        /// The sacrificed car is recorded in <see cref="ResponderStates.Sacrifice"/> so
        /// NearWreckProtection stops keeping it alive for a short window and the despawn can
        /// complete. Uses the live game Blocker (not the cached stuck-spell blocker) so a flicker
        /// onto a trailer or crossing vehicle cannot be picked as the target.
        ///
        /// Returns true when a lead was flagged, so the caller skips rerouting the responder itself.
        /// </summary>
        private bool TryReleaseLeadBlocker(Entity vehicle, uint frame, Setting setting)
        {
            if (!EntityManager.HasComponent<Blocker>(vehicle))
            {
                return false;
            }
            Entity lead = EntityManager.GetComponentData<Blocker>(vehicle).m_Blocker;
            if (lead == Entity.Null || lead == vehicle || !EntityManager.Exists(lead) ||
                !EntityManager.HasComponent<Car>(lead) ||
                !EntityManager.HasComponent<CarCurrentLane>(lead))
            {
                return false;
            }
            // Civilian only: shield every responder and tow truck, and never touch a wreck.
            if ((EntityManager.GetComponentData<Car>(lead).m_Flags & CarFlags.Emergency) != 0 ||
                EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(lead) ||
                EntityManager.HasComponent<Game.Events.InvolvedInAccident>(lead))
            {
                return false;
            }
            CarCurrentLane leadLane = EntityManager.GetComponentData<CarCurrentLane>(lead);
            if (leadLane.m_ChangeLane != Entity.Null)
            {
                return false;
            }
            // A genuine plug is stopped; a crawling car will clear on its own.
            if (EntityManager.HasComponent<Moving>(lead) &&
                math.lengthsq(EntityManager.GetComponentData<Moving>(lead).m_Velocity) > 0.25f)
            {
                return false;
            }
            leadLane.m_LaneFlags |= CarLaneFlags.Obsolete;
            EntityManager.SetComponentData(lead, leadLane);
            m_Ctx.States.Sacrifice[lead] = frame + kSacrificeShieldWindow;
            if (setting.VerboseLogging)
            {
                Mod.Log.Info($"[sacrifice] veh={vehicle.Index} flagged lead civilian car={lead.Index} " +
                    "Obsolete to clear the plug - it re-paths or despawns, freeing the queue behind");
            }
            return true;
        }

        public void LogVehicleState(Entity vehicle, CarCurrentLane currentLane, int pushed, uint frame, EvadeStage evade, int oncomingState, bool evadeSideBlocked, bool drainAhead, bool noseCreep)
        {
            float speed = math.length(EntityManager.GetComponentData<Moving>(vehicle).m_Velocity);
            Blocker blocker = EntityManager.GetComponentData<Blocker>(vehicle);
            float blockerSpeed = -1f;
            float blockerLanePos = 0f;
            float separation = -1f;
            if (blocker.m_Blocker != Entity.Null && EntityManager.Exists(blocker.m_Blocker))
            {
                blockerSpeed = EntityManager.HasComponent<Moving>(blocker.m_Blocker)
                    ? math.length(EntityManager.GetComponentData<Moving>(blocker.m_Blocker).m_Velocity)
                    : 0f;
                if (EntityManager.HasComponent<CarCurrentLane>(blocker.m_Blocker))
                {
                    blockerLanePos = EntityManager.GetComponentData<CarCurrentLane>(blocker.m_Blocker).m_LanePosition;
                }
                if (EntityManager.HasComponent<Transform>(blocker.m_Blocker))
                {
                    separation = m_Geometry.GetLateralSeparation(vehicle, currentLane, blocker.m_Blocker);
                }
            }
            m_StuckStates.TryGetValue(vehicle, out StuckState stuck);
            // pos= makes a case recognisable ACROSS a reload. Entity ids are reassigned every
            // time a save is loaded, so "veh=" cannot identify the same vehicle in a second run of
            // the same diag save - the coordinates can, and they are what makes a before/after
            // comparison of one fix possible at all.
            float3 logPos = EntityManager.HasComponent<Transform>(vehicle)
                ? EntityManager.GetComponentData<Transform>(vehicle).m_Position
                : default;
            Mod.Log.Info($"frame={frame} veh={vehicle.Index} pos=({logPos.x:F0},{logPos.z:F0}) speed={speed:F1} lanePos={currentLane.m_LanePosition:F2} " +
                $"corridorLanes={m_Corridor.CorridorLanes.Count} pushed={pushed} changing={(currentLane.m_ChangeLane != Entity.Null ? 1 : 0)} " +
                $"ignore={((currentLane.m_LaneFlags & CarLaneFlags.IgnoreBlocker) != 0 ? 1 : 0)} " +
                $"blocker={blocker.m_Blocker.Index} type={blocker.m_Type} bSpeed={blockerSpeed:F1} bLanePos={blockerLanePos:F2} sep={separation:F2} " +
                $"stuckFor={(stuck.m_Blocker != Entity.Null ? frame - stuck.m_BlockerSinceFrame : 0)} latch={(frame < stuck.m_SqueezeUntilFrame ? 1 : 0)} evade={evade} onc={oncomingState} nearFor={stuck.m_NearTargetFrames} evadeBlk={(evadeSideBlocked ? 1 : 0)} drain={(drainAhead ? 1 : 0)} " +
                $"hug={m_Corridor.ChannelHugDir:F0} free={m_Corridor.FreeLaneDir:F0} creep={(noseCreep ? 1 : 0)}");
        }
    }
}

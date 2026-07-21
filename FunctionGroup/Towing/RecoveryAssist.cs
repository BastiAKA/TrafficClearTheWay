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
        private static readonly bool kProfile = false;

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
                // 8192 frames - deliberately far longer than the other prunes. Dropping a
                // progress watch resets the stuck timer, so pruning early would keep a wedged
                // truck from ever reaching its give-up.
                if (frame - entry.Value.m_SinceFrame > 8192u || !EntityManager.Exists(entry.Key))
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
            Game.Vehicles.MaintenanceVehicle maintenance = EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(vehicle);
            if ((maintenance.m_State & MaintenanceVehicleFlags.Returning) != 0)
            {
                return; // going home, not to a crash
            }
            Entity target = EntityManager.GetComponentData<Target>(vehicle).m_Target;
            if (target == Entity.Null || !EntityManager.Exists(target) ||
                (!EntityManager.HasComponent<Damaged>(target) && !EntityManager.HasComponent<Game.Events.InvolvedInAccident>(target)))
            {
                return;
            }

            CarCurrentLane currentLane = EntityManager.GetComponentData<CarCurrentLane>(vehicle);
            if (!EntityManager.Exists(currentLane.m_Lane))
            {
                return;
            }
            // A recovery vehicle on a wreck run gets the same despawn protection as the
            // emergency vehicles - our corridor slows it down, the game must not delete it.
            m_Control.ClearStuck(vehicle);

            // Progress bookkeeping first: once it has failed to get meaningfully CLOSER to
            // its wreck for ~30 s, escalate from the gentle corridor to the emergency
            // toolbox (hard evade + squeeze + green lights) - a full accident jam never
            // opens up by politeness alone. Measured by distance, not by speed: in a
            // stop-and-go jam it inches forward constantly, so a speed-based timer reset
            // on every creep and the escalation never fired.
            bool stuckLong = false;
            float targetDistance = math.distance(
                EntityManager.GetComponentData<Transform>(vehicle).m_Position.xz,
                EntityManager.GetComponentData<Transform>(target).m_Position.xz);
            if (!m_AssistProgress.TryGetValue(vehicle, out AssistProgress progress) ||
                targetDistance < progress.m_BestDistance - kAssistProgressMeters)
            {
                progress.m_SinceFrame = frame;                 // real progress - start over
                progress.m_BestDistance = targetDistance;
                progress.m_GaveUpLogged = false;               // no longer wedged - re-arm the diagnostic
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
            m_AssistProgress[vehicle] = progress;

            // Give-up cap: past ~90 s of zero progress within escalate range the recovery is
            // wedged in an unwinnable deadlock. Escalating changes nothing (dist stays fixed
            // for minutes) and only churns the surrounding traffic - so back off ENTIRELY (no
            // corridor, no evade/squeeze/green) and let the wreck's give-up despawn clean it.
            // Emit a full diagnostic of HOW it wedged (once, then every 10 s) - we may later try
            // to actually free such trucks instead of abandoning the recovery.
            if (stuckLong && frame - progress.m_SinceFrame >= kAssistGiveUpFrames)
            {
                if (Mod.Setting.VerboseLogging && (!progress.m_GaveUpLogged || frame % 600u == 0u))
                {
                    LogAssistGiveUp(vehicle, target, currentLane, targetDistance, progress, frame);
                    progress.m_GaveUpLogged = true;
                    m_AssistProgress[vehicle] = progress;
                }
                return;
            }

            // The corridor only when the vehicle actually needs help: rolling slowly or out
            // of progress. A cruising truck gets NO special treatment ("Rettungsgassenmodus
            // die ganze Zeit an") - that disturbed traffic along the whole route for
            // nothing, and tow trucks in convoy kept shoving each other aside.
            float assistSpeed = math.length(EntityManager.GetComponentData<Moving>(vehicle).m_Velocity);
            if (assistSpeed >= kAssistHelpSpeed && !stuckLong)
            {
                return;
            }

            m_Corridor.BuildCorridor(vehicle, ref currentLane, Mod.Setting, side, assistSpeed);
            for (int i = 0; i < m_Corridor.CorridorLanes.Count; i++)
            {
                m_Ctx.Push.PullCarsAside(vehicle, m_Corridor.CorridorLanes[i], frame, hardEvade: stuckLong);
            }
            if (!stuckLong)
            {
                return;
            }

            // Squeeze past a pulled-aside/boxed-in blocker exactly like an emergency
            // vehicle (separation-gated, IgnoreBlocker) ...
            if (EntityManager.HasComponent<Blocker>(vehicle) &&
                Mod.Setting.SqueezePastBlockers &&
                m_Ctx.Squeeze.TrySqueezePastBlocker(vehicle, ref currentLane, frame))
            {
                EntityManager.SetComponentData(vehicle, currentLane);
            }
            // ... and petition the lights green at recovery priority (106): civilian petitions
            // (100) lose, emergency petitions (108) still win. No preemption - a tow truck is
            // not worth cutting a running green phase short for.
            m_Ctx.Lights.PetitionRouteAhead(vehicle, currentLane.m_Lane, kAssistSignalPriority, upcomingLanes: 2);
            if (Mod.Setting.VerboseLogging && frame % 180u == 0u)
            {
                Mod.Log.Info($"[assist] veh={vehicle.Index} no progress for {(frame - progress.m_SinceFrame) / 60u}s " +
                    $"(dist={targetDistance:F0} best={progress.m_BestDistance:F0}) - escalating (evade+squeeze+green)");
            }
        }

        /// <summary>
        /// Full diagnostic of a recovery vehicle wedged in an unwinnable deadlock (the give-up cap
        /// in ProcessAssistVehicle). Dumps how long it has been stuck, truck/wreck positions and
        /// distance, the truck's lane + IgnoreBlocker state, and WHO is directly blocking it plus
        /// that blocker's own state (is it another wreck? an emergency vehicle? a maintenance
        /// truck? and is IT moving?). This is the data needed to later actually diagnose and break
        /// these gridlocks instead of abandoning the recovery to the wreck's give-up despawn.
        /// </summary>
        private void LogAssistGiveUp(Entity vehicle, Entity wreck, CarCurrentLane lane,
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

            Mod.Log.Info($"[assist] veh={vehicle.Index} GAVE UP - deadlocked {(frame - progress.m_SinceFrame) / 60u}s, " +
                $"dist={targetDistance:F0} best={progress.m_BestDistance:F0} spd={speed:F1} " +
                $"pos=({vpos.x:F0},{vpos.z:F0}) wreck={wreck.Index}@({wpos.x:F0},{wpos.z:F0}) " +
                $"lane={lane.m_Lane.Index} lanePos={lane.m_LanePosition:F1} ignoreBlocker={(ignore ? 1 : 0)} " +
                $"blockedBy=[{blockerInfo}]");
        }

    }
}

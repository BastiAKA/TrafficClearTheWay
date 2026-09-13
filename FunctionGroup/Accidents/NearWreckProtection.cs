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

namespace ClearTheWay.FunctionGroup.Accidents
{
    /// <summary>
    /// Per-car handling for every vehicle found near a wreck in one tick sweep.
    ///
    /// Does four things to each: shields its ORIGINAL route from being invalidated (the repath is
    /// what kills it - see AccidentGuard), clears stuck and failed path flags so the AIs do not
    /// delete it, brakes it short of the pile if the wreck lies in its path, and freezes wrecks
    /// that are still jiggling so they stop firing collision impacts into the queue head.
    ///
    /// Every car is processed exactly ONCE per tick even when several wrecks are near it, and the
    /// component reads are hoisted - doing this per wreck per car was a large part of the
    /// post-accident frame-rate drop.
    /// </summary>
    internal sealed class NearWreckProtection
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private VehicleControl m_Control => m_Ctx.VehicleControl;

        private Dictionary<Entity, CarSnapshot> m_NearWreckSnapshots => m_Ctx.Accidents.m_NearWreckSnapshots;

        private static bool kDespawnForensics => AccidentGuard.kDespawnForensics;
        private List<float3> m_WreckPosScratch => m_Ctx.Accidents.m_WreckPosScratch;
        private List<int> m_WreckClusterOf => m_Ctx.Accidents.m_WreckClusterOf;
        private HashSet<Entity> m_NearSeenScratch => m_Ctx.Accidents.m_NearSeenScratch;
        private List<Entity> m_NearWreckCars => m_Ctx.Accidents.m_NearWreckCars;
        public NearWreckProtection(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Per-car protection work for one cluster sweep: despawn shields, repath
        /// shield and approach-brake, each car exactly once per tick (deduped across
        /// clusters). Component reads are hoisted; the per-wreck brake test is pure float
        /// math against the cluster's wreck positions.</summary>
        public void ProtectNearWreckCars(NativeList<Entity> found, int cluster, Setting setting, uint frame,
            ref int protectedCount, ref int stuckCleared, ref int nPending, ref int nFailed, ref int nStuck,
            ref int nDummy, ref int nStopped, ref int nObsCleared, ref int nBrakeCand, ref int nBraked)
        {
            for (int i = 0; i < found.Length; i++)
            {
                Entity other = found[i];
                if (!m_NearSeenScratch.Add(other) || !EntityManager.Exists(other) ||
                    !EntityManager.HasComponent<Car>(other) ||
                    !EntityManager.HasComponent<PathOwner>(other))
                {
                    continue;
                }
                // Deliberately sacrificed lead blocker (EmergencyEscalation.TryReleaseLeadBlocker):
                // a responder is hard-stuck behind this car and flagged it Obsolete to clear the
                // plug. Do NOT keep it alive or track it - leave it to the game's own re-path /
                // despawn so the lane frees for the queue behind. Time-boxed; once the window
                // elapses it falls back to normal protection here.
                if (m_Ctx.States.Sacrifice.TryGetValue(other, out uint sacUntil) && frame < sacUntil)
                {
                    continue;
                }
                m_NearWreckCars.Add(other);
                protectedCount++;
                bool isEmergency = (EntityManager.GetComponentData<Car>(other).m_Flags & CarFlags.Emergency) != 0;
                bool isMaintenance = EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(other);
                bool isInvolved = EntityManager.HasComponent<Game.Events.InvolvedInAccident>(other);
                PathFlags state = EntityManager.GetComponentData<PathOwner>(other).m_State;
                if ((state & PathFlags.Pending) != 0) nPending++;
                if ((state & PathFlags.Failed) != 0) nFailed++;
                if ((state & PathFlags.Stuck) != 0) nStuck++;
                // Diagnostic-only counters. nDummy and nStopped cost two component lookups
                // EACH per car per tick and are only ever read back out in the [accident] line,
                // which is itself behind VerboseLogging - so in ordinary play this was pure
                // repetition on the hottest per-car loop in the mod. The three PathFlags counts
                // above stay ungated: they read a state word that has already been fetched.
                if (setting.VerboseLogging)
                {
                    if (EntityManager.HasComponent<Game.Vehicles.PersonalCar>(other) &&
                        (EntityManager.GetComponentData<Game.Vehicles.PersonalCar>(other).m_State
                            & PersonalCarFlags.DummyTraffic) != 0) nDummy++;
                    if (EntityManager.HasComponent<Moving>(other) &&
                        math.lengthsq(EntityManager.GetComponentData<Moving>(other).m_Velocity) < 0.25f) nStopped++;
                }
                if ((state & (PathFlags.Stuck | PathFlags.Failed)) != 0)
                {
                    stuckCleared++;
                    m_Control.ClearStuckAndFailed(other);
                }

                // Repath shield: a repath at a fully blocked road is what KILLS the
                // queue (see kAccidentQueueRange comment). Keep the original route
                // alive; emergency/recovery vehicles must stay reactive - skip them.
                if ((state & (PathFlags.Obsolete | PathFlags.DivertObsolete)) != 0 &&
                    !isEmergency && !isMaintenance)
                {
                    PathOwner shieldPo = EntityManager.GetComponentData<PathOwner>(other);
                    shieldPo.m_State &= ~(PathFlags.Obsolete | PathFlags.DivertObsolete);
                    EntityManager.SetComponentData(other, shieldPo);
                    nObsCleared++;
                }

                // Approach-brake: if this car is moving toward a wreck of this cluster,
                // cap its speed so it stops a safe gap short instead of crashing into it
                // (which is how the "despawns" actually happen - it becomes a wreck
                // itself). Skip cars already in an accident, emergency and recovery
                // vehicles. The per-wreck test is pure float math; component reads
                // happen once per car.
                if (!isInvolved && !isEmergency && !isMaintenance &&
                    EntityManager.HasComponent<Transform>(other) &&
                    EntityManager.HasComponent<Moving>(other))
                {
                    float3 vel = EntityManager.GetComponentData<Moving>(other).m_Velocity;
                    if (math.lengthsq(vel) > kBrakeMinSpeed * kBrakeMinSpeed)
                    {
                        nBrakeCand++;
                        float3 carPos = EntityManager.GetComponentData<Transform>(other).m_Position;
                        float2 fwd = math.normalizesafe(vel.xz);
                        for (int wi = 0; wi < m_WreckPosScratch.Count; wi++)
                        {
                            if (m_WreckClusterOf[wi] != cluster)
                            {
                                continue;
                            }
                            float2 toWreck = m_WreckPosScratch[wi].xz - carPos.xz;
                            float along = math.dot(toWreck, fwd);
                            // Wreck must be ahead and close to the car's travel line (so we
                            // brake for the car's OWN lane, not a free neighbour lane). Range
                            // is kept short so this straight-line test still holds on the
                            // gentle curves of a highway.
                            if (along > 0f && along < kBrakeRange &&
                                math.length(toWreck - along * fwd) < kBrakeLateral)
                            {
                                m_Control.SetCeiling(other, math.max(0f, (along - kBrakeSafeGap) * kBrakeSpeedPerMeter));
                                nBraked++;
                            }
                        }
                    }
                }

                // Tombstone diagnostics: snapshot every near-wreck car each frame so
                // the moment one vanishes we can log what it was and how it went.
                // Forensics only (kDespawnForensics) - ~15 component reads per car
                // per tick, a large chunk of the post-accident lag when verbose is on.
                if (kDespawnForensics && setting.VerboseLogging)
                {
                    float3 snapPos = EntityManager.HasComponent<Transform>(other)
                        ? EntityManager.GetComponentData<Transform>(other).m_Position : default;
                    float snapSpeed = EntityManager.HasComponent<Moving>(other)
                        ? math.length(EntityManager.GetComponentData<Moving>(other).m_Velocity) : 0f;
                    bool snapInvolved = EntityManager.HasComponent<Game.Events.InvolvedInAccident>(other);
                    bool snapDummy = EntityManager.HasComponent<Game.Vehicles.PersonalCar>(other) &&
                        (EntityManager.GetComponentData<Game.Vehicles.PersonalCar>(other).m_State & PersonalCarFlags.DummyTraffic) != 0;
                    uint snapLane = EntityManager.HasComponent<CarCurrentLane>(other)
                        ? (uint)EntityManager.GetComponentData<CarCurrentLane>(other).m_LaneFlags : 0u;
                    if (m_NearWreckSnapshots.TryGetValue(other, out CarSnapshot prev) &&
                        !prev.m_Involved && snapInvolved)
                    {
                        Mod.Log.Info($"[infected] veh={other.Index} speed={snapSpeed:F1} prevSpeed={prev.m_Speed:F1} " +
                            $"y={snapPos.y:F1} dummy={(snapDummy ? 1 : 0)}");
                    }
                    char snapType = EntityManager.HasComponent<Game.Vehicles.PersonalCar>(other) ? 'P'
                        : EntityManager.HasComponent<Game.Vehicles.DeliveryTruck>(other) ? 'D'
                        : EntityManager.HasComponent<Game.Vehicles.GarbageTruck>(other) ? 'G'
                        : EntityManager.HasComponent<Game.Vehicles.Taxi>(other) ? 'T' : '?';
                    // Track the TARGET too: "route deleted right before despawn" (seen
                    // in the UI) is the RemovePath→delete sequence, fired by PathfindFailed
                    // OR by the target entity ceasing to exist. Record which one it is.
                    Entity snapTarget = Entity.Null;
                    bool snapTargetExists = false;
                    char snapTargetType = '-';
                    float3 snapTargetPos = default;
                    if (EntityManager.HasComponent<Target>(other))
                    {
                        snapTarget = EntityManager.GetComponentData<Target>(other).m_Target;
                        snapTargetExists = snapTarget != Entity.Null && EntityManager.Exists(snapTarget);
                        if (snapTargetExists)
                        {
                            snapTargetType = EntityManager.HasComponent<Game.Buildings.Building>(snapTarget) ? 'B'
                                : EntityManager.HasComponent<Game.Objects.OutsideConnection>(snapTarget) ? 'O'
                                : EntityManager.HasComponent<Vehicle>(snapTarget) ? 'V'
                                : EntityManager.HasComponent<Game.Creatures.Creature>(snapTarget) ? 'C'
                                : EntityManager.HasComponent<Game.Net.Lane>(snapTarget) ? 'L' : '?';
                            if (EntityManager.HasComponent<Transform>(snapTarget))
                            {
                                snapTargetPos = EntityManager.GetComponentData<Transform>(snapTarget).m_Position;
                            }
                        }
                    }
                    if (m_NearWreckSnapshots.TryGetValue(other, out CarSnapshot prevT) &&
                        prevT.m_TargetExists && !snapTargetExists)
                    {
                        Mod.Log.Info($"[targetlost] veh={other.Index} type={snapType} target={prevT.m_Target.Index} " +
                            $"targetType={prevT.m_TargetType} speed={snapSpeed:F1} dummy={(snapDummy ? 1 : 0)}");
                    }
                    m_NearWreckSnapshots[other] = new CarSnapshot
                    {
                        m_Frame = frame,
                        m_Pos = snapPos,
                        m_Speed = snapSpeed,
                        m_LaneFlags = snapLane,
                        m_PathState = (uint)state,
                        m_Involved = snapInvolved,
                        m_Dummy = snapDummy,
                        m_Type = snapType,
                        m_Target = snapTarget,
                        m_TargetExists = snapTargetExists,
                        m_TargetType = snapTargetType,
                        m_TargetPos = snapTargetPos
                    };
                }
            }
        }
    }
}

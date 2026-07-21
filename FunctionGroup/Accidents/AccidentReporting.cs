using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using Game.Common;
using Game.Objects;
using Game.Simulation;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace ClearTheWay.FunctionGroup.Accidents
{
    /// <summary>
    /// Post-sweep bookkeeping for the accident pass: tombstone forensics, wreck-age baseline
    /// pruning and the [accident] heartbeat line.
    ///
    /// Split out because none of it influences behaviour - it is the diagnostic layer that made
    /// the despawn chain findable in the first place, and it should be readable (and skippable)
    /// on its own.
    /// </summary>
    internal sealed class AccidentReporting
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        // The accident pass owns this bookkeeping; this helper only reads and prunes it.
        private Dictionary<Entity, uint> m_WreckSeenInvolvedFrame => m_Ctx.Accidents.m_WreckSeenInvolvedFrame;
        private HashSet<Entity> m_ClearedWrecks => m_Ctx.Accidents.m_ClearedWrecks;
        private Dictionary<Entity, CarSnapshot> m_NearWreckSnapshots => m_Ctx.Accidents.m_NearWreckSnapshots;
        private List<Entity> m_SnapshotPrune => m_Ctx.Accidents.m_SnapshotPrune;
        private List<Entity> m_WreckPruneScratch => m_Ctx.Accidents.m_WreckPruneScratch;
        private EntityQuery m_WreckQuery => m_Ctx.Accidents.wreckQuery;
        private static bool kDespawnForensics => AccidentGuard.kDespawnForensics;
        public AccidentReporting(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Post-sweep bookkeeping split out of ProtectAccidentQueues: tombstone
        /// sweep (forensics only), wreck-age baseline pruning, [accident] heartbeat.</summary>
        public void AfterProtectSweep(Setting setting, uint frame, int withPos, int protectedCount,
            int stuckCleared, int nPending, int nFailed, int nStuck, int nDummy, int nStopped,
            uint maxWreckAge, int nDestroyed, int extendedWrecks, int nBrakeCand, int nBraked,
            int nFrozen, int nObsCleared, int nSecuredLanes, int nClearedAside)
        {
            // Tombstone sweep: any snapshotted car not seen THIS frame either left the area
            // or ceased to exist. If it vanished within a couple of frames of its last
            // snapshot, log its fate + last state - this names the actual despawn mechanism.
            if (kDespawnForensics && setting.VerboseLogging && m_NearWreckSnapshots.Count != 0)
            {
                m_SnapshotPrune.Clear();
                foreach (KeyValuePair<Entity, CarSnapshot> kv in m_NearWreckSnapshots)
                {
                    if (kv.Value.m_Frame == frame)
                    {
                        continue; // still present this frame
                    }
                    Entity e = kv.Key;
                    uint silent = frame - kv.Value.m_Frame;
                    bool exists = EntityManager.Exists(e);
                    bool deletedNow = exists && EntityManager.HasComponent<Deleted>(e);
                    bool unspawnedNow = exists && EntityManager.HasComponent<Unspawned>(e);
                    if ((!exists || deletedNow) && silent <= 32u)
                    {
                        CarSnapshot s = kv.Value;
                        bool tgtExistsNow = s.m_Target != Entity.Null && EntityManager.Exists(s.m_Target);
                        float tgtDistGone = s.m_TargetExists ? math.distance(s.m_Pos.xz, s.m_TargetPos.xz) : -1f;
                        Mod.Log.Info($"[tombstone] veh={e.Index} type={s.m_Type} fate={(exists ? "deleted" : "gone")} " +
                            $"lastSpeed={s.m_Speed:F1} pos=({s.m_Pos.x:F0};{s.m_Pos.y:F0};{s.m_Pos.z:F0}) involved={(s.m_Involved ? 1 : 0)} " +
                            $"dummy={(s.m_Dummy ? 1 : 0)} lane=0x{s.m_LaneFlags:X} path=0x{s.m_PathState:X} " +
                            $"tgt={s.m_Target.Index} tgtType={s.m_TargetType} tgtWasAlive={(s.m_TargetExists ? 1 : 0)} tgtNow={(tgtExistsNow ? 1 : 0)} tgtDist={tgtDistGone:F0} silent={silent}");
                        m_SnapshotPrune.Add(e);
                    }
                    else if (unspawnedNow && silent <= 32u)
                    {
                        CarSnapshot s = kv.Value;
                        float tgtDist = s.m_TargetExists ? math.distance(s.m_Pos.xz, s.m_TargetPos.xz) : -1f;
                        Mod.Log.Info($"[tombstone] veh={e.Index} type={s.m_Type} fate=unspawned " +
                            $"lastSpeed={s.m_Speed:F1} pos=({s.m_Pos.x:F0};{s.m_Pos.y:F0};{s.m_Pos.z:F0}) involved={(s.m_Involved ? 1 : 0)} " +
                            $"dummy={(s.m_Dummy ? 1 : 0)} lane=0x{s.m_LaneFlags:X} path=0x{s.m_PathState:X} " +
                            $"tgt={s.m_Target.Index} tgtType={s.m_TargetType} tgtWasAlive={(s.m_TargetExists ? 1 : 0)} tgtDist={tgtDist:F0} silent={silent}");
                        m_SnapshotPrune.Add(e);
                    }
                    else if (silent > 32u)
                    {
                        m_SnapshotPrune.Add(e); // left the area normally - drop silently
                    }
                }
                for (int i = 0; i < m_SnapshotPrune.Count; i++)
                {
                    m_NearWreckSnapshots.Remove(m_SnapshotPrune[i]);
                }
            }

            // Prune wrecks we no longer see (cleared / deleted) from the age-baseline map.
            if (m_WreckSeenInvolvedFrame.Count != 0)
            {
                m_WreckPruneScratch.Clear();
                foreach (KeyValuePair<Entity, uint> kv in m_WreckSeenInvolvedFrame)
                {
                    if (!EntityManager.Exists(kv.Key) ||
                        !EntityManager.HasComponent<Game.Events.InvolvedInAccident>(kv.Key))
                    {
                        m_WreckPruneScratch.Add(kv.Key);
                    }
                }
                for (int i = 0; i < m_WreckPruneScratch.Count; i++)
                {
                    m_WreckSeenInvolvedFrame.Remove(m_WreckPruneScratch[i]);
                    m_ClearedWrecks.Remove(m_WreckPruneScratch[i]);
                }
            }

            if (setting.VerboseLogging && frame % 120u == 0u)
            {
                Mod.Log.Info($"[accident] wrecks={m_WreckQuery.CalculateEntityCount()} withPos={withPos} " +
                    $"carsInArea={protectedCount} stuckCleared={stuckCleared} " +
                    $"| nPending={nPending} nFailed={nFailed} nStuck={nStuck} nDummy={nDummy} nStopped={nStopped} " +
                    $"| maxWreckAge={maxWreckAge} destroyed={nDestroyed} extended={extendedWrecks} brakeCand={nBrakeCand} braked={nBraked} frozen={nFrozen} obsCleared={nObsCleared} securedLanes={nSecuredLanes} cleared={nClearedAside}/{m_ClearedWrecks.Count}");
            }
        }
    }
}

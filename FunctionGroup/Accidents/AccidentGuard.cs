using System.Collections.Generic;
using Colossal.Collections;
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
using Unity.Jobs;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;

namespace ClearTheWay.FunctionGroup.Accidents
{
    /// <summary>
    /// Everything that keeps an accident from destroying the traffic around it.
    ///
    /// The queue behind a wreck does NOT despawn through any of the mechanisms it looks like it
    /// should. Proven by tombstone logging, the real chain is: a blocked lane is HARD impassable
    /// to the pathfinder, so a full block makes every destination behind it unreachable; the
    /// wreck's lanes broadcast PathfindUpdated, the queue repaths, the pathfinder can only hand
    /// out a short disposal path into the nearest building connection, and the car evaporates
    /// mid-drive ("route disappears, then the car does"). That is vanilla's graceful traffic
    /// evaporation for unroutable trips - not a delete, not a collision, not a path-end, not a
    /// stuck flag. Every earlier theory was chasing a side mechanism.
    ///
    /// So this class works on the cause rather than the symptom:
    ///  - shields the queue's original, still-valid route from being invalidated at all,
    ///  - marks wreck-blocked lanes secured so the repath broadcast stops repeating,
    ///  - consolidates settled wrecks onto one lane so a carriageway stays passable,
    ///  - brakes approaching traffic short of the pile so it stops rear-ending it,
    ///  - and holds a wreck alive long enough to be recovered, then lets the game despawn it
    ///    cleanly if nobody ever comes.
    ///
    /// PERFORMANCE: wrecks of one accident lie meters apart, and each used to fire its own
    /// 600x600 m quadtree traversal every tick (10 wrecks = 10 sweeps, plus the path-end guard's
    /// 10 - that was the post-accident frame-rate drop). Wrecks are clustered into one sweep per
    /// SITE, each car is processed exactly once, and the resulting car list is shared with
    /// <see cref="PathEndGuardSystem"/> so it needs no traversal of its own.
    /// </summary>
    internal sealed class AccidentGuard
    {
        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = false;

        private readonly WayClearanceContext modContext;
        private EntityManager EntityManager => modContext.EntityManager;
        private Game.Objects.SearchSystem m_ObjectSearchSystem => modContext.ObjectSearch;
        private Game.Net.SearchSystem m_NetSearchSystem => modContext.NetSearch;

        /// <summary>The wreck query; also read by the reporting helper for its heartbeat.</summary>
        public readonly EntityQuery wreckQuery;

        public AccidentGuard(WayClearanceContext modContext, EntityQuery wreckQuery)
        {
            this.modContext = modContext;
            this.wreckQuery = wreckQuery;
        }
        // Original InvolvedFrame captured the first time we saw each wreck, so the lifetime
        // extension can be bounded by the wreck's TRUE age even after we bump InvolvedFrame.
        public readonly Dictionary<Entity, uint> m_WreckSeenInvolvedFrame = new();
        public readonly HashSet<Entity> m_ClearedWrecks = new();

        public readonly Dictionary<Entity, CarSnapshot> m_NearWreckSnapshots = new();
        public readonly List<Entity> m_SnapshotPrune = new();

        // Per-car tombstone forensics ([tombstone]/[infected]/[targetlost]): ~15 component
        // lookups per near-wreck car per tick, only ever needed to HUNT a despawn mechanism.
        // The hunt is done (truncated path ends + repath broadcast + approach crashes, all
        // fixed) - flip this on only to re-arm the forensics for a new hunt.
        public static readonly bool kDespawnForensics = false;

        // Spatial-sweep clustering: wrecks of one accident lie within meters of each other,
        // yet each used to fire its own 600x600 m quadtree traversal EVERY tick (10 wrecks =
        // 10 sweeps + the path-end guard's 10 = the post-accident lag). Wrecks closer than
        // this share one sweep whose bounds are the union of their positions +- range.
        private const float kWreckClusterMerge = 150f;
        public readonly List<float3> m_WreckPosScratch = new List<float3>();
        public readonly List<int> m_WreckClusterOf = new List<int>();
        public readonly List<Bounds3> m_ClusterBounds = new List<Bounds3>();
        public readonly HashSet<Entity> m_NearSeenScratch = new HashSet<Entity>();
        // Deduped "cars near any wreck" result of this tick's sweep, shared with the
        // path-end guard system so it does not repeat the traversal post-navigation.
        public readonly List<Entity> m_NearWreckCars = new List<Entity>(256);
        public uint m_NearWreckCarsFrame = uint.MaxValue;
        public List<Entity> NearWreckCars => m_NearWreckCars;
        public uint NearWreckCarsFrame => m_NearWreckCarsFrame;
        public readonly List<Entity> m_WreckPruneScratch = new List<Entity>();


        /// <summary>
        /// Clears the stuck/failed path flags on vehicles queued near a crashed vehicle, so
        /// the game does not despawn them - they wait for the wreck to be recovered. Covers
        /// the wreck's own lane within a generous range around it.
        /// </summary>
        // Cached near-wreck MEMBERSHIP, one list per accident cluster (see
        // kNearWreckSweepInterval). Only WHO is near a scene is cached; WHAT is done to each of
        // them still runs every tick, so the despawn shield, the repath shield and the approach
        // brake keep their old cadence and behaviour.
        //
        // The cached bounds are kept alongside and compared on every tick: cluster INDICES are
        // rebuilt from the wreck query each pass, so a changed wreck set can renumber them while
        // leaving the count alone - and a stale list would then be handed to the wrong cluster,
        // braking cars against wrecks that are somewhere else entirely. Wrecks do not move, so
        // while the scene is unchanged the bounds match exactly and the cache holds.
        private readonly List<List<Entity>> m_ClusterCarsCache = new List<List<Entity>>();
        private readonly List<Bounds3> m_CachedClusterBounds = new List<Bounds3>();
        private uint m_ClusterCarsFrame;

        private bool NearWreckCacheStale(uint frame)
        {
            if (m_ClusterCarsCache.Count != m_ClusterBounds.Count ||
                m_CachedClusterBounds.Count != m_ClusterBounds.Count ||
                frame - m_ClusterCarsFrame >= kNearWreckSweepInterval)
            {
                return true;
            }
            for (int c = 0; c < m_ClusterBounds.Count; c++)
            {
                if (!m_CachedClusterBounds[c].min.Equals(m_ClusterBounds[c].min) ||
                    !m_CachedClusterBounds[c].max.Equals(m_ClusterBounds[c].max))
                {
                    return true;
                }
            }
            return false;
        }

        public void ProtectAccidentQueues(Setting setting, uint frame)
        {
            using (ModProfiler.Sample(kProfile, "AccidentGuard"))
            {
                ProtectAccidentQueuesImpl(setting, frame);
            }
            ModProfiler.EndTick(kProfile, "AccidentGuard");
        }

        private void ProtectAccidentQueuesImpl(Setting setting, uint frame)
        {
            NativeArray<Entity> wrecks = wreckQuery.ToEntityArray(Allocator.Temp);
            // Search trees are fetched LAZILY: every GetXxxSearchTree + Complete() is a
            // main-thread job sync, and three of them per tick were a big share of the
            // post-accident lag. The net tree is only needed in the rare tick a wreck is
            // actually cleared aside; the moving tree only once per tick for the clustered
            // sweep below.
            NativeQuadTree<Entity, QuadTreeBoundsXZ> netTree = default;
            bool netTreeFetched = false;
            NativeList<Entity> found = new NativeList<Entity>(128, Allocator.Temp);
            m_WreckPosScratch.Clear();
            m_WreckClusterOf.Clear();
            m_ClusterBounds.Clear();
            m_NearSeenScratch.Clear();
            m_NearWreckCars.Clear();
            m_NearWreckCarsFrame = frame;
            int protectedCount = 0;
            int stuckCleared = 0;
            int withPos = 0;
            WreckSweepStats stats = default;
            // Diagnostics: what state are the nearby cars actually in? (stuckCleared has been
            // 0 all along, so the queue clearly does not despawn via Stuck|Failed - find out
            // what it DOES do.) And how old are the wrecks vs the 14400-frame delete timeout?
            int nPending = 0, nFailed = 0, nStuck = 0, nDummy = 0, nStopped = 0;
            int nBraked = 0, nBrakeCand = 0, nObsCleared = 0, nClearedAside = 0;
            try
            {
                for (int w = 0; w < wrecks.Length; w++)
                {
                    Entity wreck = wrecks[w];
                    if (!EntityManager.HasComponent<Transform>(wreck))
                    {
                        continue;
                    }
                    withPos++;

                    uint trueAge = modContext.WreckLifetime.Apply(wreck, setting, frame, ref stats);

                    float3 pos = EntityManager.GetComponentData<Transform>(wreck).m_Position;

                    // Consolidate settled wrecks onto the outermost lane of their side so at
                    // least one lane stays pathfind-passable (see kWreckClearDelay comment).
                    if (setting.ClearWrecksAside && !m_ClearedWrecks.Contains(wreck) &&
                        EntityManager.HasComponent<Stopped>(wreck) &&
                        !EntityManager.HasComponent<Moving>(wreck) &&
                        !EntityManager.HasComponent<Game.Events.OnFire>(wreck) &&
                        trueAge >= kWreckClearDelay)
                    {
                        if (!netTreeFetched)
                        {
                            netTree = m_NetSearchSystem.GetNetSearchTree(readOnly: true, out JobHandle netDeps);
                            netDeps.Complete();
                            netTreeFetched = true;
                        }
                        if (modContext.WreckClearing.TryClearWreckAside(wreck, pos, netTree))
                        {
                            m_ClearedWrecks.Add(wreck);
                            nClearedAside++;
                        }
                    }

                    // Cluster membership: join the first cluster whose bounds are within
                    // kWreckClusterMerge (same accident scene), growing its bounds; else
                    // start a new one. ONE spatial sweep per cluster below replaces the old
                    // one-per-wreck sweeps.
                    int clusterIndex = -1;
                    for (int c = 0; c < m_ClusterBounds.Count; c++)
                    {
                        Bounds3 b = m_ClusterBounds[c];
                        if (pos.x >= b.min.x - kWreckClusterMerge && pos.x <= b.max.x + kWreckClusterMerge &&
                            pos.z >= b.min.z - kWreckClusterMerge && pos.z <= b.max.z + kWreckClusterMerge)
                        {
                            b.min = math.min(b.min, pos);
                            b.max = math.max(b.max, pos);
                            m_ClusterBounds[c] = b;
                            clusterIndex = c;
                            break;
                        }
                    }
                    if (clusterIndex < 0)
                    {
                        clusterIndex = m_ClusterBounds.Count;
                        m_ClusterBounds.Add(new Bounds3(pos, pos));
                    }
                    m_WreckPosScratch.Add(pos);
                    m_WreckClusterOf.Add(clusterIndex);
                }

                // --- One sweep per accident scene; every nearby car processed exactly ONCE
                // (the old per-wreck boxes overlapped almost completely, so every car went
                // through the whole protection block up to wreck-count times per tick) ---
                if (m_ClusterBounds.Count != 0)
                {
                    // Rebuild tick: the quadtree traversal and its main-thread job sync happen
                    // here and ONLY here. This is the pass that used to run at 58 Hz.
                    if (NearWreckCacheStale(frame))
                    {
                        while (m_ClusterCarsCache.Count < m_ClusterBounds.Count)
                        {
                            m_ClusterCarsCache.Add(new List<Entity>());
                        }
                        m_CachedClusterBounds.Clear();
                        NativeQuadTree<Entity, QuadTreeBoundsXZ> tree =
                            m_ObjectSearchSystem.GetMovingSearchTree(readOnly: true, out JobHandle deps);
                        deps.Complete();
                        for (int c = 0; c < m_ClusterBounds.Count; c++)
                        {
                            found.Clear();
                            AreaIterator iterator = new AreaIterator
                            {
                                m_Bounds = new Bounds3(m_ClusterBounds[c].min - kAccidentQueueRange,
                                                       m_ClusterBounds[c].max + kAccidentQueueRange),
                                m_Results = found
                            };
                            tree.Iterate(ref iterator);
                            List<Entity> slot = m_ClusterCarsCache[c];
                            slot.Clear();
                            for (int k = 0; k < found.Length; k++)
                            {
                                slot.Add(found[k]);
                            }
                            m_CachedClusterBounds.Add(m_ClusterBounds[c]);
                        }
                        m_ObjectSearchSystem.AddMovingSearchTreeReader(default);
                        m_ClusterCarsFrame = frame;
                    }

                    // Every tick, rebuild or not: the protection work itself. Entities that died
                    // since the sweep are filtered by the Exists/HasComponent guards at the top of
                    // ProtectNearWreckCars, so a stale id in the list is harmless.
                    for (int c = 0; c < m_ClusterBounds.Count; c++)
                    {
                        found.Clear();
                        List<Entity> slot = m_ClusterCarsCache[c];
                        for (int k = 0; k < slot.Count; k++)
                        {
                            found.Add(slot[k]);
                        }
                        modContext.NearWreck.ProtectNearWreckCars(found, c, setting, frame,
                            ref protectedCount, ref stuckCleared, ref nPending, ref nFailed, ref nStuck,
                            ref nDummy, ref nStopped, ref nObsCleared, ref nBrakeCand, ref nBraked);
                    }
                }
            }
            finally
            {
                found.Dispose();
                wrecks.Dispose();
            }
            if (netTreeFetched)
            {
                m_NetSearchSystem.AddNetSearchTreeReader(default);
            }
            modContext.Reporting.AfterProtectSweep(setting, frame, withPos, protectedCount, stuckCleared, nPending, nFailed,
                nStuck, nDummy, nStopped, stats.m_MaxWreckAge, stats.m_Destroyed, stats.m_Extended, nBrakeCand, nBraked,
                stats.m_Frozen, nObsCleared, stats.m_SecuredLanes, nClearedAside);
        }






    }
}

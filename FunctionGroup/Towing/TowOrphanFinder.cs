using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>Where a candidate wreck last was, and since when it has not moved.</summary>
    internal struct WreckRest
    {
        public float3 m_Pos;
        public uint m_Since;
    }

    /// <summary>
    /// Finder and last-resort sweeper for stranded "orphan" wrecks - the widest net we cast, so
    /// nothing that is going nowhere can hide from us.
    ///
    /// The single test is MOVEMENT, not the wreck's tag/controller state. A wreck being hauled is
    /// teleported behind its truck every frame, so its position keeps changing and its stranded
    /// timer keeps resetting; a wreck nobody is moving sits still and its timer grows. That one
    /// test catches every stranding cause at once - self-controller (206440), a controller pointing
    /// at a stuck or dead truck (1486251), a live accident wreck the vanilla dispatch never served,
    /// or one invisible to all our other logs (177005) - without having to reason about each.
    ///
    /// It reports from kOrphanStrandedFrames (~20 s) and only ACTS at kOrphanDeleteFrames (~10 min),
    /// where it deletes the wreck and its rig. That gap is the point: the log tells us which wrecks
    /// are stranded and why long before anything is removed, and ten minutes of total stillness is
    /// slow enough that every recovery route - a truck arriving, TowReleaseFrozen freeing the car,
    /// vanilla's own 14400-frame accident timeout - has already had its chance. This is the
    /// backstop under all of them, not a first resort.
    /// </summary>
    internal sealed class TowOrphanFinder
    {
        private readonly TowingContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private EntityQuery m_Query => m_Ctx.OrphanCandidateQuery;
        private Dictionary<Entity, WreckRest> m_Rest => m_Ctx.OrphanRest;
        private HashSet<Entity> m_TrucksWithLoad => m_Ctx.TrucksWithLoad;

        private readonly HashSet<Entity> m_Seen = new HashSet<Entity>();
        private readonly List<Entity> m_Prune = new List<Entity>();

        public TowOrphanFinder(TowingContext ctx)
        {
            m_Ctx = ctx;
        }

        public void FindOrphans(uint frame, Setting setting)
        {
            m_Seen.Clear();
            if (!m_Query.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> cars = m_Query.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < cars.Length; i++)
                    {
                        Entity w = cars[i];
                        m_Seen.Add(w);
                        float3 pos = EntityManager.GetComponentData<Transform>(w).m_Position;
                        // Moved (or first seen) -> it is progressing / being hauled; reset the timer.
                        if (!m_Rest.TryGetValue(w, out WreckRest rest) ||
                            math.distancesq(rest.m_Pos.xz, pos.xz) > kOrphanMoveEpsilon * kOrphanMoveEpsilon)
                        {
                            m_Rest[w] = new WreckRest { m_Pos = pos, m_Since = frame };
                            continue;
                        }
                        uint strandedFor = frame - rest.m_Since;
                        if (strandedFor < kOrphanStrandedFrames)
                        {
                            continue;
                        }
                        if (setting.VerboseLogging)
                        {
                            LogCandidate(w, strandedFor, frame);
                        }
                        // Terminal state (Sebastian's rule): ten minutes without moving a metre
                        // means nothing is ever coming. Every other route has had its chance by
                        // now - a truck would have hauled it (which moves it, resetting this
                        // timer), the release pass would have freed it, vanilla's own 14400-frame
                        // accident timeout is long past. Deleting is what is left, and it is what
                        // vanilla does with a wreck nobody clears.
                        if (strandedFor >= kOrphanDeleteFrames)
                        {
                            DeleteStranded(w, strandedFor, setting);
                        }
                    }
                }
                finally
                {
                    cars.Dispose();
                }
            }

            // Forget wrecks that left the candidate set (towed away, despawned, started moving out
            // of it) so the rest map cannot grow unbounded.
            if (m_Rest.Count != 0)
            {
                m_Prune.Clear();
                foreach (KeyValuePair<Entity, WreckRest> kv in m_Rest)
                {
                    if (!m_Seen.Contains(kv.Key) || !EntityManager.Exists(kv.Key))
                    {
                        m_Prune.Add(kv.Key);
                    }
                }
                for (int i = 0; i < m_Prune.Count; i++)
                {
                    m_Rest.Remove(m_Prune[i]);
                }
                m_Prune.Clear();
            }
        }

        /// <summary>
        /// Remove a wreck nothing has moved in ten minutes, together with the rest of its rig.
        ///
        /// The whole LayoutElement group goes at once, the way vanilla's own VehicleUtils
        /// .DeleteVehicle does it: leaving a trailer behind with a Controller pointing at a deleted
        /// tractor is the dangling reference that crashes CarTrailerMoveSystem, and it is also how
        /// the orphan-trailer problem was created in the first place.
        ///
        /// One exception: a wreck genuinely hanging on a live recovery truck is never touched.
        /// A truck stuck in a jam does not move its load either, so the movement test alone cannot
        /// tell "abandoned" from "waiting in traffic" - the carrier can.
        /// </summary>
        private void DeleteStranded(Entity w, uint strandedFor, Setting setting)
        {
            if (EntityManager.HasComponent<Controller>(w))
            {
                Entity carrier = EntityManager.GetComponentData<Controller>(w).m_Controller;
                if (carrier != w && carrier != Entity.Null && EntityManager.Exists(carrier) &&
                    !EntityManager.HasComponent<Deleted>(carrier) &&
                    EntityManager.HasComponent<MaintenanceVehicle>(carrier))
                {
                    return;
                }
            }
            int members = 0;
            if (EntityManager.HasBuffer<LayoutElement>(w))
            {
                DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(w, isReadOnly: true);
                for (int i = 0; i < layout.Length; i++)
                {
                    Entity member = layout[i].m_Vehicle;
                    if (member == Entity.Null || member == w || !EntityManager.Exists(member) ||
                        EntityManager.HasComponent<Deleted>(member))
                    {
                        continue;
                    }
                    m_Ctx.SafeDelete(member);
                    members++;
                }
            }
            m_Ctx.SafeDelete(w);
            m_Rest.Remove(w);
            if (setting.VerboseLogging)
            {
                Mod.Log.Info($"[orphanfind] wreck={w.Index} DELETED - not moved for {strandedFor}f " +
                    $"(~{strandedFor / 58u}s); nothing recovered it" +
                    (members != 0 ? $"; +{members} rig member(s)" : string.Empty));
            }
        }

        private void LogCandidate(Entity w, uint strandedFor, uint frame)
        {
            // The finder pass already runs only every 128 frames (~2 s), which is the throttle;
            // one line per stranded wreck per pass matches the existing [towdiag] cadence and lets
            // us watch strandedFor climb.
            string ctrlDesc = "none";
            int carrierIdx = 0;
            bool carrierLoad = false;
            if (EntityManager.HasComponent<Controller>(w))
            {
                Entity carrier = EntityManager.GetComponentData<Controller>(w).m_Controller;
                carrierIdx = carrier.Index;
                if (carrier == w)
                {
                    ctrlDesc = "self";
                }
                else if (carrier == Entity.Null)
                {
                    ctrlDesc = "null";
                }
                else if (!EntityManager.Exists(carrier) || EntityManager.HasComponent<Deleted>(carrier))
                {
                    ctrlDesc = "dead";
                }
                else if (EntityManager.HasComponent<MaintenanceVehicle>(carrier))
                {
                    ctrlDesc = "recov";
                    carrierLoad = m_TrucksWithLoad.Contains(carrier);
                }
                else
                {
                    ctrlDesc = "other";
                }
            }
            Mod.Log.Info($"[orphanfind] wreck={w.Index} strandedFor={strandedFor}f " +
                $"ctrl={ctrlDesc} carrier={carrierIdx} carrierHauling={(carrierLoad ? 1 : 0)} " +
                $"invAcc={(EntityManager.HasComponent<Game.Events.InvolvedInAccident>(w) ? 1 : 0)} " +
                $"tow={(EntityManager.HasComponent<TowMarker>(w) ? 1 : 0)} " +
                $"dmg={(EntityManager.HasComponent<Damaged>(w) ? 1 : 0)} " +
                $"dstr={(EntityManager.HasComponent<Destroyed>(w) ? 1 : 0)} " +
                $"outctl={(EntityManager.HasComponent<OutOfControl>(w) ? 1 : 0)} " +
                $"relicRec={(EntityManager.HasComponent<RelicRecovery>(w) ? 1 : 0)}");
        }
    }
}

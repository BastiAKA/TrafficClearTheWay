using System.Collections.Generic;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// Dropping dead entities out of the per-vehicle maps.
    ///
    /// Nearly every pass here remembers something per vehicle or per wreck - when it was last
    /// assigned, where it last stood, whether we already reported something about it. Vehicles come
    /// and go constantly in a city, so any such map grows for the whole session unless something
    /// removes the departed. Individually each entry is a handful of bytes; over hours, across a
    /// dozen maps, in a mod other people run on their own cities, it is a slow leak - and the kind
    /// that only ever shows up in somebody else's long game, never in a short test.
    ///
    /// Several maps clean up after themselves at the natural moment (the entry is removed when the
    /// tow finishes, the path recovers, the cooldown expires). This is the safety net underneath
    /// those: whatever the flow forgot, the entity is gone and so should the entry be. Deliberately
    /// infrequent - it is a scan over small maps, not something that needs to run every tick.
    /// </summary>
    internal static class EntityMapPrune
    {
        /// <summary>Remove every entry whose key no longer exists.</summary>
        public static void PruneDead<T>(EntityManager entityManager, Dictionary<Entity, T> map,
            List<Entity> scratch)
        {
            if (map.Count == 0)
            {
                return;
            }
            scratch.Clear();
            foreach (KeyValuePair<Entity, T> entry in map)
            {
                if (!entityManager.Exists(entry.Key))
                {
                    scratch.Add(entry.Key);
                }
            }
            for (int i = 0; i < scratch.Count; i++)
            {
                map.Remove(scratch[i]);
            }
            scratch.Clear();
        }

        /// <summary>Remove every member that no longer exists.</summary>
        public static void PruneDead(EntityManager entityManager, HashSet<Entity> set, List<Entity> scratch)
        {
            if (set.Count == 0)
            {
                return;
            }
            scratch.Clear();
            foreach (Entity entity in set)
            {
                if (!entityManager.Exists(entity))
                {
                    scratch.Add(entity);
                }
            }
            for (int i = 0; i < scratch.Count; i++)
            {
                set.Remove(scratch[i]);
            }
            scratch.Clear();
        }
    }
}

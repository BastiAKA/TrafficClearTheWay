using System;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// Central component access for the whole mod: the same calls as
    /// <see cref="EntityManager"/> (<c>HasComponent</c>, <c>GetComponentData</c>,
    /// <c>SetComponentData</c>, <c>HasBuffer</c>, <c>GetBuffer</c>) plus
    /// <see cref="TryGetComponent{T}"/> / <see cref="TryGetBuffer{T}"/>, served from one cached
    /// <see cref="ComponentLookup{T}"/> / <see cref="BufferLookup{T}"/> per type.
    ///
    /// WHY: every <c>EntityManager.GetComponentData&lt;T&gt;</c> runs a job-dependency check
    /// for T (<c>CompleteWriteDependency</c>) and searches the entity's chunk from scratch;
    /// <c>HasComponent</c> + <c>GetComponentData</c> searched twice. A lookup searches once,
    /// remembers the last archetype (<c>LookupCache</c>), and the dependency check runs once
    /// per type per system update instead of once per call.
    ///
    /// RULES, all three enforced here rather than at the call sites:
    /// <list type="number">
    /// <item><b>Every system calls <see cref="Begin"/> at the top of OnUpdate.</b> Between two
    /// of our systems the game's jobs run and change data, so each update starts a new epoch:
    /// lookups are refreshed and every type is synced again on its first use.</item>
    /// <item><b>Sync on first use, per type</b> (<c>CompleteDependencyBeforeRO/RW&lt;T&gt;</c>,
    /// exactly what EntityManager did on every call). The shipped game has no Unity safety
    /// checks compiled in, so reading a lookup while a job writes T would not throw - it would
    /// silently return a torn or stale value. We wait for the same types, in the same
    /// situations, as before; just once instead of on every call. Nothing schedules a job
    /// inside our updates, and a structural change completes all jobs anyway, so one sync
    /// per epoch stays valid for the rest of it.</item>
    /// <item><b>Lookups are created by <see cref="LookupHostSystem"/>, never by our real
    /// systems.</b> <c>SystemBase.GetComponentLookup</c> registers the type with the calling
    /// system, which widens that system's job dependencies (and through them the vanilla job
    /// graph) by every type we read. The host is never updated, so its registrations never
    /// reach the dependency manager.</item>
    /// </list>
    ///
    /// Values handed out are COPIES (plain structs). Changing one changes nothing in the game
    /// until it is written back with <see cref="SetComponentData{T}"/>.
    ///
    /// Implementation: one static generic class per component type (<c>ComponentSlot&lt;T&gt;</c>),
    /// so a call is a static field load on a type-specialised class - no dictionary, no boxing.
    /// </summary>
    internal static class Lookups
    {
        private static World s_World;
        private static LookupHostSystem s_Host;

        /// <summary>Bumped by <see cref="Begin"/>; a slot whose epoch differs is stale.</summary>
        private static uint s_Epoch;

        /// <summary>Bumped when the world changes, so slots of an old world are rebuilt.</summary>
        private static uint s_WorldGeneration;

        /// <summary>
        /// Starts a new epoch. Call first thing in every system's OnUpdate, before any access.
        /// Cost: two compares and an increment - slots refresh lazily on their next use.
        /// </summary>
        public static void Begin(SystemBase system)
        {
            World world = system.World;
            if (!ReferenceEquals(world, s_World) || s_Host == null)
            {
                s_World = world;
                s_Host = world.GetOrCreateSystemManaged<LookupHostSystem>();
                s_WorldGeneration++;
            }
            s_Epoch++;
            if (s_Epoch == 0)
            {
                s_Epoch = 1; // 0 means "never synced" in the slots
            }
        }

        // ---- components ------------------------------------------------------------------

        public static bool HasComponent<T>(Entity entity) where T : unmanaged, IComponentData
        {
            return ComponentSlot<T>.Has(entity);
        }

        /// <summary>
        /// The component's value; the entity MUST have it (same contract as EntityManager -
        /// use <see cref="TryGetComponent{T}"/> when it may not).
        /// </summary>
        public static T GetComponentData<T>(Entity entity) where T : unmanaged, IComponentData
        {
            return ComponentSlot<T>.Get(entity);
        }

        /// <summary>
        /// One lookup instead of HasComponent + GetComponentData. True with a copy of the value
        /// when the entity exists and has T; false with <c>default(T)</c> otherwise. For tag
        /// components (no fields) the value is always default - use HasComponent there.
        /// </summary>
        public static bool TryGetComponent<T>(Entity entity, out T component) where T : unmanaged, IComponentData
        {
            return ComponentSlot<T>.TryGet(entity, out component);
        }

        /// <summary>Writes the value back. The entity must have T.</summary>
        public static void SetComponentData<T>(Entity entity, T component) where T : unmanaged, IComponentData
        {
            ComponentSlot<T>.Set(entity, component);
        }

        // ---- buffers ---------------------------------------------------------------------

        public static bool HasBuffer<T>(Entity entity) where T : unmanaged, IBufferElementData
        {
            return BufferSlot<T>.Has(entity);
        }

        /// <summary>
        /// The entity's buffer; the entity MUST have it. <paramref name="isReadOnly"/> keeps
        /// EntityManager's meaning: read-only does not bump the chunk's change version and only
        /// waits for writers; read-write waits for readers and writers.
        /// </summary>
        public static DynamicBuffer<T> GetBuffer<T>(Entity entity, bool isReadOnly = false) where T : unmanaged, IBufferElementData
        {
            return BufferSlot<T>.Get(entity, isReadOnly);
        }

        /// <summary>One lookup instead of HasBuffer + GetBuffer.</summary>
        public static bool TryGetBuffer<T>(Entity entity, out DynamicBuffer<T> buffer, bool isReadOnly = false) where T : unmanaged, IBufferElementData
        {
            return BufferSlot<T>.TryGet(entity, out buffer, isReadOnly);
        }

        // ---- slots -----------------------------------------------------------------------

        private static LookupHostSystem Host
        {
            get
            {
                if (s_Host == null)
                {
                    throw new InvalidOperationException(
                        "ClearTheWay.Lookups used before Lookups.Begin - call it at the top of the system's OnUpdate");
                }
                return s_Host;
            }
        }

        private static class ComponentSlot<T> where T : unmanaged, IComponentData
        {
            private static readonly string s_SyncName = "JobSync." + typeof(T).Name;

            // Not readonly: TryGetComponent writes the lookup's internal cache, and Update()
            // must change this field itself. On a readonly field C# would work on a copy.
            private static ComponentLookup<T> s_Lookup;
            private static uint s_Generation;
            private static uint s_Epoch;
            private static uint s_ReadSynced;
            private static uint s_WriteSynced;

            /// <summary>Creates the lookup for this world, and refreshes it once per epoch.</summary>
            private static void Prepare()
            {
                if (s_Generation != s_WorldGeneration)
                {
                    // Read-write lookup: reads still take the read-only path (no version bump),
                    // only the setter stamps the chunk - as EntityManager does.
                    s_Lookup = Host.GetComponentLookup<T>(isReadOnly: false);
                    s_Generation = s_WorldGeneration;
                    s_ReadSynced = 0;
                    s_WriteSynced = 0;
                }
                else
                {
                    s_Lookup.Update(Host);
                }
                s_Epoch = Lookups.s_Epoch;
            }

            public static bool Has(Entity entity)
            {
                // Presence is structural (chunk archetype) - no job writes it, no sync needed,
                // exactly like EntityManager.HasComponent.
                if (s_Epoch != Lookups.s_Epoch)
                {
                    Prepare();
                }
                return s_Lookup.HasComponent(entity);
            }

            public static T Get(Entity entity)
            {
                SyncForRead();
                return s_Lookup[entity];
            }

            public static bool TryGet(Entity entity, out T component)
            {
                SyncForRead();
                return s_Lookup.TryGetComponent(entity, out component);
            }

            public static void Set(Entity entity, T component)
            {
                SyncForWrite();
                s_Lookup[entity] = component;
            }

            private static void SyncForRead()
            {
                if (s_Epoch != Lookups.s_Epoch)
                {
                    Prepare();
                }
                if (s_ReadSynced != s_Epoch)
                {
                    using (ModProfiler.Sample(ModProfiler.IsOn(ProfiledPass.JobSync), s_SyncName))
                    {
                        s_Host.EntityManager.CompleteDependencyBeforeRO<T>();
                    }
                    s_ReadSynced = s_Epoch;
                }
            }

            private static void SyncForWrite()
            {
                if (s_Epoch != Lookups.s_Epoch)
                {
                    Prepare();
                }
                if (s_WriteSynced != s_Epoch)
                {
                    using (ModProfiler.Sample(ModProfiler.IsOn(ProfiledPass.JobSync), s_SyncName))
                    {
                        s_Host.EntityManager.CompleteDependencyBeforeRW<T>();
                    }
                    s_WriteSynced = s_Epoch;
                    s_ReadSynced = s_Epoch; // waiting for readers and writers covers reading too
                }
            }
        }

        private static class BufferSlot<T> where T : unmanaged, IBufferElementData
        {
            private static readonly string s_SyncName = "JobSync." + typeof(T).Name;

            // Two lookups on purpose: a buffer lookup's mode is fixed at creation, and a
            // read-write one stamps the chunk's change version on EVERY access, reads included.
            // Keeping EntityManager's isReadOnly meaning needs one of each.
            private static BufferLookup<T> s_ReadLookup;
            private static BufferLookup<T> s_WriteLookup;
            private static uint s_Generation;
            private static uint s_Epoch;
            private static uint s_ReadSynced;
            private static uint s_WriteSynced;

            private static void Prepare()
            {
                if (s_Generation != s_WorldGeneration)
                {
                    s_ReadLookup = Host.GetBufferLookup<T>(isReadOnly: true);
                    s_WriteLookup = Host.GetBufferLookup<T>(isReadOnly: false);
                    s_Generation = s_WorldGeneration;
                    s_ReadSynced = 0;
                    s_WriteSynced = 0;
                }
                else
                {
                    s_ReadLookup.Update(Host);
                    s_WriteLookup.Update(Host);
                }
                s_Epoch = Lookups.s_Epoch;
            }

            public static bool Has(Entity entity)
            {
                if (s_Epoch != Lookups.s_Epoch)
                {
                    Prepare();
                }
                return s_ReadLookup.HasBuffer(entity);
            }

            public static DynamicBuffer<T> Get(Entity entity, bool isReadOnly)
            {
                if (isReadOnly)
                {
                    SyncForRead();
                    return s_ReadLookup[entity];
                }
                SyncForWrite();
                return s_WriteLookup[entity];
            }

            public static bool TryGet(Entity entity, out DynamicBuffer<T> buffer, bool isReadOnly)
            {
                if (isReadOnly)
                {
                    SyncForRead();
                    return s_ReadLookup.TryGetBuffer(entity, out buffer);
                }
                SyncForWrite();
                return s_WriteLookup.TryGetBuffer(entity, out buffer);
            }

            private static void SyncForRead()
            {
                if (s_Epoch != Lookups.s_Epoch)
                {
                    Prepare();
                }
                if (s_ReadSynced != s_Epoch)
                {
                    using (ModProfiler.Sample(ModProfiler.IsOn(ProfiledPass.JobSync), s_SyncName))
                    {
                        s_Host.EntityManager.CompleteDependencyBeforeRO<T>();
                    }
                    s_ReadSynced = s_Epoch;
                }
            }

            private static void SyncForWrite()
            {
                if (s_Epoch != Lookups.s_Epoch)
                {
                    Prepare();
                }
                if (s_WriteSynced != s_Epoch)
                {
                    using (ModProfiler.Sample(ModProfiler.IsOn(ProfiledPass.JobSync), s_SyncName))
                    {
                        s_Host.EntityManager.CompleteDependencyBeforeRW<T>();
                    }
                    s_WriteSynced = s_Epoch;
                    s_ReadSynced = s_Epoch;
                }
            }
        }
    }

    /// <summary>
    /// Factory for <see cref="Lookups"/>: owns every lookup the mod uses. Created with
    /// GetOrCreateSystemManaged but never added to an update phase, so OnUpdate never runs and
    /// the reader/writer types its lookups register never enter the dependency manager - our
    /// real systems' job dependencies stay exactly what their queries make them.
    /// </summary>
    public partial class LookupHostSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
}

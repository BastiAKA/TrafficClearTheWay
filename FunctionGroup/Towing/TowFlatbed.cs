using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace ClearTheWay.FunctionGroup.Towing
{
    /// <summary>
    /// RETIRED flatbed-towing path, parked here intact.
    ///
    /// Nothing in the mod calls any of this. Towing on master is PURE DRAWBAR: the wreck stays a
    /// static Stopped object that TowHookupSystem teleports to a rope-point behind the truck each
    /// frame. That is the only variant that has ever been stable.
    ///
    /// Why flatbed was abandoned, so it is not re-attempted blindly:
    ///  - The real-trailer flatbed rendered and cornered correctly, but the wreck riding its deck
    ///    glitched under the world.
    ///  - Making the wreck itself a trailer (branch wreck-as-trailer-B) repeatedly hard-crashed in
    ///    CarTrailerMoveSystem, an unguarded Burst job that reads tractor/trailer data off the
    ///    PREFAB - a plain car or maintenance van as either end is a null lookup and an instant
    ///    native crash.
    ///  - The flatbed SWEEP that used to live alongside this was deleted for a separate reason: our
    ///    Abschlepphaenger is a REGISTERED CarTrailerPrefab clone, so the game hands it to ordinary
    ///    civilian cars. The sweep saw "PrefabRef == our clone" and deleted other people's trailers,
    ///    leaving dangling LayoutElement references. Do not reintroduce a prefab-identity sweep.
    ///
    /// The Abschlepphaenger PREFAB itself is NOT dead and is registered every load by
    /// <see cref="TowTrailerAsset"/> - it is live in save games and driving around cities as a
    /// normal civilian trailer. Leave that alone.
    ///
    /// To revive: the class needs the fields TowHookupSystem owns (m_SimulationSystem, the
    /// CreateEntityFromArchetype reflection helper, SetOrAdd) - see git history before this file
    /// was split out.
    /// </summary>
    internal static class TowFlatbed
    {
        internal const float kDeckHeight = 1.0f;    // wreck rides this high above the flatbed origin (deck height)
        internal const float kDeckForward = 0.0f;   // ... and this far forward on the deck (+Z)

        /*
         * The dormant code, verbatim as it last built. Kept commented so it cannot drift out of
         * sync with the live code around it (it referenced private members of TowHookupSystem),
         * while staying readable as the starting point for any revival.
         *
         *             // Our spawned flatbed trailers (flatbed-tow mode): real vanilla CarTrailer entities
         *             // whose PrefabRef is our Abschlepphaenger clone. Swept each tick so a flatbed is
         *             // removed once its truck has parked/despawned (the wreck riding it then self-deletes).
         *             m_FlatbedQuery = GetEntityQuery(new EntityQueryDesc
         *             {
         *                 All = new[]
         *                 {
         *                     ComponentType.ReadOnly<CarTrailer>(),
         *                     ComponentType.ReadOnly<Controller>(),
         *                     ComponentType.ReadOnly<PrefabRef>()
         *                 },
         *                 None = new[]
         *                 {
         *                     ComponentType.ReadOnly<Deleted>(),
         *                     ComponentType.ReadOnly<Game.Tools.Temp>()
         *                 }
         *             });
         * 
         * 
         *             // Flatbed mode: the carrier is our spawned flatbed (a real vanilla trailer moved by
         *             // CarTrailerMoveSystem). The wreck rides rigidly on its deck - snap to the deck point
         *             // each frame; the correct cornering motion comes from the trailer physics, not us.
         *             if (EntityManager.HasComponent<CarTrailer>(carrier) &&
         *                 TowTrailerAsset.Prefab != Entity.Null &&
         *                 EntityManager.GetComponentData<PrefabRef>(carrier).m_Prefab == TowTrailerAsset.Prefab)
         *             {
         *                 Transform deckTransform = EntityManager.GetComponentData<Transform>(carrier);
         *                 Transform ridden = EntityManager.GetComponentData<Transform>(wreck);
         *                 float3 deckPos = deckTransform.m_Position +
         *                     math.rotate(deckTransform.m_Rotation, new float3(0f, kDeckHeight, kDeckForward));
         *                 if (math.lengthsq(deckPos - ridden.m_Position) < 0.0004f &&
         *                     math.abs(math.dot(ridden.m_Rotation.value, deckTransform.m_Rotation.value)) > 0.99999f)
         *                 {
         *                     return; // already parked on the deck - no churn
         *                 }
         *                 ridden.m_Position = deckPos;
         *                 ridden.m_Rotation = deckTransform.m_Rotation;
         *                 EntityManager.SetComponentData(wreck, ridden);
         *                 EntityManager.AddComponent<Updated>(wreck);
         *                 EntityManager.AddComponent<BatchesUpdated>(wreck);
         *                 return;
         *             }
         * 
         * 
         *         /// <summary>
         *         /// Spawns a vanilla flatbed trailer (our Abschlepphaenger clone) behind the truck and
         *         /// hitches it via the truck's LayoutElement, mirroring how Game.Vehicles.InitializeSystem
         *         /// sets a freshly-spawned trailer up. Both prefabs carry the tractor/trailer data
         *         /// CarTrailerMoveSystem reads unguarded off PrefabRef, so the pair is driven safely by
         *         /// vanilla physics. Returns the flatbed entity, or Entity.Null on any failure (caller
         *         /// then falls back to the drawbar follow). Never throws out.
         *         /// </summary>
         *         private Entity TrySpawnFlatbed(Entity truck, uint frame)
         *         {
         *             try
         *             {
         *                 Entity fbPrefab = TowTrailerAsset.Prefab;
         *                 if (!EntityManager.HasComponent<ObjectData>(fbPrefab) ||
         *                     !EntityManager.HasComponent<CarTrailerData>(fbPrefab))
         *                 {
         *                     return Entity.Null;
         *                 }
         *                 Entity truckPrefab = EntityManager.GetComponentData<PrefabRef>(truck).m_Prefab;
         *                 Transform truckTransform = EntityManager.GetComponentData<Transform>(truck);
         * 
         *                 // Position the flatbed behind the truck exactly like InitializeSystem does:
         *                 // tractor attach point minus the trailer's own attach point (same heading).
         *                 float3 tractorAttach = EntityManager.GetComponentData<CarTractorData>(truckPrefab).m_AttachPosition;
         *                 float3 trailerAttach = EntityManager.GetComponentData<CarTrailerData>(fbPrefab).m_AttachPosition;
         *                 Transform fbTransform = truckTransform;
         *                 fbTransform.m_Position += math.rotate(truckTransform.m_Rotation, tractorAttach);
         *                 fbTransform.m_Position -= math.rotate(fbTransform.m_Rotation, trailerAttach);
         * 
         *                 Unity.Entities.EntityArchetype archetype = EntityManager.GetComponentData<ObjectData>(fbPrefab).m_Archetype;
         *                 // NB: EntityManager.CreateEntity(EntityArchetype) is called via reflection on
         *                 // purpose. Its method group also has a ReadOnlySpan<ComponentType> overload, and
         *                 // resolving that overload at compile time needs System.ReadOnlySpan defined -
         *                 // which the net472 reference assemblies here do not provide (CS0518). Reflection
         *                 // sidesteps compile-time overload resolution entirely.
         *                 Entity flatbed = CreateEntityFromArchetype(archetype);
         *                 EntityManager.SetComponentData(flatbed, fbTransform);
         *                 EntityManager.SetComponentData(flatbed, new PrefabRef(fbPrefab));
         *                 SetOrAdd(flatbed, new Controller(truck));
         *                 SetOrAdd(flatbed, default(Moving));
         * 
         *                 // TransformFrame ring MUST hold 4 frames or CarTrailerMoveSystem/CarMoveSystem
         *                 // index out of bounds and hard-crash (learned the hard way).
         *                 DynamicBuffer<TransformFrame> frames = EntityManager.HasBuffer<TransformFrame>(flatbed)
         *                     ? EntityManager.GetBuffer<TransformFrame>(flatbed)
         *                     : EntityManager.AddBuffer<TransformFrame>(flatbed);
         *                 frames.Clear();
         *                 for (int i = 0; i < 4; i++)
         *                 {
         *                     frames.Add(new TransformFrame(fbTransform));
         *                 }
         * 
         *                 // Seed the trailer lane from the truck's current lane (InitializeSystem does the
         *                 // same); CarNavigationSystem self-heals it anyway if this ends up null.
         *                 CarTrailerLane seededLane = EntityManager.HasComponent<CarCurrentLane>(truck)
         *                     ? new CarTrailerLane(EntityManager.GetComponentData<CarCurrentLane>(truck))
         *                     : default;
         *                 SetOrAdd(flatbed, seededLane);
         * 
         *                 if (!EntityManager.HasComponent<Game.Rendering.InterpolatedTransform>(flatbed))
         *                 {
         *                     EntityManager.AddComponentData(flatbed, new Game.Rendering.InterpolatedTransform(fbTransform));
         *                 }
         *                 if (!EntityManager.HasComponent<Game.Rendering.Swaying>(flatbed))
         *                 {
         *                     EntityManager.AddComponentData(flatbed, default(Game.Rendering.Swaying));
         *                 }
         *                 EntityManager.AddComponent<Updated>(flatbed);
         *                 EntityManager.AddComponent<BatchesUpdated>(flatbed);
         * 
         *                 // Hitch: the truck's LayoutElement must be [0]=truck itself, [1]=flatbed.
         *                 DynamicBuffer<LayoutElement> layout = EntityManager.HasBuffer<LayoutElement>(truck)
         *                     ? EntityManager.GetBuffer<LayoutElement>(truck)
         *                     : EntityManager.AddBuffer<LayoutElement>(truck);
         *                 if (layout.Length == 0)
         *                 {
         *                     layout.Add(new LayoutElement(truck));
         *                 }
         *                 layout.Add(new LayoutElement(flatbed));
         * 
         *                 if (Mod.Setting.VerboseLogging)
         *                 {
         *                     Mod.Log.Info($"[flatbed] spawned flatbed={flatbed.Index} behind truck={truck.Index}");
         *                 }
         *                 return flatbed;
         *             }
         *             catch (System.Exception e)
         *             {
         *                 Mod.Log.Error(e, "[flatbed] spawn failed - falling back to drawbar");
         *                 return Entity.Null;
         *             }
         *         }
         * 
         */
    }
}

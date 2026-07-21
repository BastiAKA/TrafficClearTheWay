using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace ClearTheWay
{
    /// <summary>
    /// Registers the "Abschleppwagen": a runtime deep-clone of the road maintenance vehicle,
    /// restricted to MaintenanceType.Vehicle and given tow-tractor capability (CarTractor).
    ///
    /// Why a Vehicle-only clone gets used automatically: MaintenanceVehicleSelectData scores
    /// candidates by matching maintenance-type bits (countbits(type ^ requested)); a Vehicle-only
    /// vehicle is the perfect match (score 0) for the Vehicle-only tow depot and beats the stock
    /// road van (extra Road/Snow bits), while road/snow depots never pick it (it lacks those bits).
    ///
    /// The CarTractor is dormant until a trailer is coupled - CarTrailerMoveSystem only touches
    /// vehicles whose LayoutElement buffer has more than one entry - so an un-hitched tow truck
    /// behaves exactly like a recovery van. Actual flatbed towing is wired up separately; this
    /// file only makes the tractor-capable vehicle exist and be selected.
    ///
    /// Created on game preload; never mutates the vanilla prefab (deep component copy, same
    /// pattern as <see cref="TowDepotAsset"/>). Behind the (default-off) TowTruckPrefab setting.
    /// </summary>
    public static class TowTruckAsset
    {
        public const string kName = "Abschleppwagen";
        private static bool s_Done;

        public static void EnsureCreated()
        {
            if (s_Done || ClearTheWay.Mod.Setting == null || !ClearTheWay.Mod.Setting.TowTruckPrefab)
            {
                return;
            }
            try
            {
                World world = World.DefaultGameObjectInjectionWorld;
                if (world == null)
                {
                    return;
                }
                PrefabSystem prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();

                FieldInfo prefabsField = typeof(PrefabSystem).GetField("m_Prefabs",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                List<PrefabBase> prefabs = (List<PrefabBase>)prefabsField.GetValue(prefabSystem);

                // Already registered (e.g. a previous load this session)?
                foreach (PrefabBase existing in prefabs)
                {
                    if (existing != null && existing.name == kName)
                    {
                        s_Done = true;
                        return;
                    }
                }

                // Find a road maintenance vehicle: a CarPrefab whose MaintenanceVehicle can
                // recover crashed vehicles (has the Vehicle maintenance bit). Prefer the road
                // maintenance van by name; any vehicle-capable one works as a fallback.
                PrefabBase source = null;
                foreach (PrefabBase candidate in prefabs)
                {
                    if (candidate is CarPrefab &&
                        candidate.TryGet(out Game.Prefabs.MaintenanceVehicle mv) &&
                        (mv.m_MaintenanceType & MaintenanceType.Vehicle) != 0)
                    {
                        source = candidate;
                        if (candidate.name.Contains("RoadMaintenance"))
                        {
                            break; // best match
                        }
                    }
                }
                if (source == null)
                {
                    ClearTheWay.Mod.Log.Warn("[abschleppwagen] no vehicle-capable maintenance vehicle prefab found - skipped");
                    s_Done = true;
                    return;
                }

                // Deep clone: create an EMPTY instance of the same type (silent OnEnable),
                // JSON-copy the serialized fields (meshes etc. stay shared - intended), then
                // deep-copy the components so the clone owns its own instances. Do NOT use
                // Object.Instantiate (fires OnEnable while components still point at the source).
                PrefabBase clone = (PrefabBase)ScriptableObject.CreateInstance(source.GetType());
                clone.name = kName;
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), clone);
                clone.name = kName;
                List<ComponentBase> shared = clone.components;
                clone.components = new List<ComponentBase>();
                if (shared != null)
                {
                    foreach (ComponentBase component in shared)
                    {
                        if (component != null)
                        {
                            clone.AddComponentFrom(component);
                        }
                    }
                }

                // Vehicle recovery only - never street/snow work (so a Vehicle-only depot picks it
                // and street depots don't).
                if (clone.TryGet(out Game.Prefabs.MaintenanceVehicle cloneMv))
                {
                    cloneMv.m_MaintenanceType = MaintenanceType.Vehicle;
                    // ONE wreck per trip. The source RoadMaintenanceVehicle carries capacity=1000,
                    // but a vehicle-recovery request costs up to 500 (MaintenanceVehicleAISystem.
                    // PreAddMaintenanceRequests: 500*(1-cleared) for Destroyed, min(500, damage*500)
                    // for Damaged), so ONE truck was loaded with ~2-10 wrecks and crawled through
                    // them serially while those wrecks sat claimed (no new truck spawned for them).
                    // Capacity 1 => EstimatedFull after the first wreck => the depot spawns a
                    // SEPARATE truck per wreck. Safe against efficiency scaling: the runtime value
                    // is CeilToInt(1 * m_Efficiency), which never rounds down to 0.
                    cloneMv.m_MaintenanceCapacity = 1;
                }

                // Tow-tractor capability (towbar). m_FixedTrailer stays null: no auto-spawned
                // trailer at vehicle spawn - a trailer is attached at hookup. This adds
                // CarTractorData to the prefab, which is what CarTrailerMoveSystem reads off the
                // tractor's PrefabRef (an unguarded lookup - the whole reason a plain car cannot
                // tow without crashing).
                if (!clone.Has<CarTractor>())
                {
                    CarTractor tractor = ScriptableObject.CreateInstance<CarTractor>();
                    tractor.m_TrailerType = CarTrailerType.Towbar;
                    tractor.m_AttachOffset = new float3(0f, 0.5f, -3.5f); // rear towbar, behind center; tune in-game
                    tractor.m_FixedTrailer = null;
                    clone.AddComponentFrom(tractor);
                }

                prefabSystem.AddPrefab(clone);
                ClearTheWay.Mod.Log.Info($"[abschleppwagen] created '{kName}' from '{source.name}' (MaintenanceType=Vehicle, CarTractor=Towbar)");
                s_Done = true;
            }
            catch (Exception e)
            {
                ClearTheWay.Mod.Log.Error(e, "[abschleppwagen] creation failed");
                s_Done = true; // don't retry every preload
            }
        }
    }
}

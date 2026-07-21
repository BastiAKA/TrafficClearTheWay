using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using UnityEngine;

namespace ClearTheWay.FunctionGroup.Towing
{
    /// <summary>
    /// Registers the "Abschleppfahrzeugdepot": a runtime clone of the road maintenance
    /// depot restricted to MaintenanceType.Vehicle. The game already dispatches vehicle
    /// recovery per wreck (DamagedVehicleSystem creates MaintenanceRequest prio 100), but
    /// vanilla depots mix road/snow patrol duty into the same trucks, which keep working
    /// their route after being called and take forever. A Vehicle-only depot cannot do any
    /// street work, so its trucks answer wreck requests directly.
    ///
    /// Created on game preload (before deserialization) so saves containing placed depots
    /// load safely. Never mutates the vanilla prefab: components are deep-copied
    /// (AddComponentFrom = JSON copy into fresh instances).
    /// </summary>
    public static class TowDepotAsset
    {
        public const string kName = "Abschleppfahrzeugdepot";
        private static bool s_Done;

        public static void EnsureCreated()
        {
            if (s_Done || Mod.Setting == null || !Mod.Setting.TowDepot)
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
                if (prefabSystem.TryGetPrefab(new PrefabID(nameof(BuildingPrefab), kName), out PrefabBase _))
                {
                    s_Done = true;
                    return;
                }

                // Find the road maintenance depot: a standalone building (not a service
                // upgrade) whose MaintenanceDepot component does Road work.
                FieldInfo prefabsField = typeof(PrefabSystem).GetField("m_Prefabs",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                List<PrefabBase> prefabs = (List<PrefabBase>)prefabsField.GetValue(prefabSystem);
                PrefabBase source = null;
                foreach (PrefabBase candidate in prefabs)
                {
                    if (candidate is BuildingPrefab &&
                        !candidate.Has<ServiceUpgrade>() &&
                        candidate.TryGet(out MaintenanceDepot depot) &&
                        (depot.m_MaintenanceType & MaintenanceType.Road) != 0)
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
                    Mod.Log.Warn("[abschleppdepot] no road maintenance depot prefab found - skipped");
                    s_Done = true;
                    return;
                }

                // Do NOT use Object.Instantiate here: it fires PrefabBase.OnEnable while the
                // copied component list still references the vanilla instances, which logs
                // "Component on prefab ... is referenced from another prefab". Instead create
                // an EMPTY prefab of the same type (OnEnable on empty = silent), JSON-copy all
                // serialized fields (meshes etc. stay shared references - intended), then
                // deep-copy the components so the clone owns its own instances.
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
                if (clone.TryGet(out MaintenanceDepot cloneDepot))
                {
                    cloneDepot.m_MaintenanceType = MaintenanceType.Vehicle; // tow duty only
                }
                prefabSystem.AddPrefab(clone);
                Mod.Log.Info($"[abschleppdepot] created '{kName}' from '{source.name}' (MaintenanceType=Vehicle)");
                s_Done = true;
            }
            catch (Exception e)
            {
                Mod.Log.Error(e, "[abschleppdepot] creation failed");
                s_Done = true; // don't retry every preload
            }
        }
    }
}

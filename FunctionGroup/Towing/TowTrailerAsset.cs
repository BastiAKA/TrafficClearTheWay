using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Prefabs;
using Unity.Entities;
using UnityEngine;

namespace ClearTheWay.FunctionGroup.Towing
{
    /// <summary>
    /// Registers the "Abschlepphaenger": a runtime deep-clone of an existing trailer
    /// (CarTrailerPrefab), used as the flatbed the Abschleppwagen tows the wreck on. Cloning a
    /// real trailer prefab is what makes towing crash-free: CarTrailerMoveSystem reads CarData +
    /// CarTrailerData off the trailer's PrefabRef with unguarded lookups (a plain car has neither).
    ///
    /// Set to a Towbar / Free-movement drawbar trailer so CarTrailerMoveSystem's Free branch
    /// follows the tractor with damping (its only movement branch is on m_MovementType). The
    /// actual wreck is not made a trailer - it rides rigidly on this flatbed's deck via our
    /// static-teleport follow (Piece 3), keeping the crashed-car look while the vanilla trailer
    /// physics provide the correct cornering motion.
    ///
    /// Created on game preload; never mutates the vanilla prefab (deep component copy, same
    /// pattern as <see cref="TowTruckAsset"/>). Behind the (default-off) TowTruckPrefab setting.
    /// </summary>
    public static class TowTrailerAsset
    {
        public const string kName = "Abschlepphaenger";
        private static bool s_Done;

        /// <summary>The registered trailer prefab entity, or Entity.Null if not created. Piece 3 spawns this.</summary>
        public static Entity Prefab { get; private set; }

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

                // Already registered this session?
                foreach (PrefabBase existing in prefabs)
                {
                    if (existing != null && existing.name == kName)
                    {
                        Prefab = prefabSystem.GetEntity(existing);
                        s_Done = true;
                        return;
                    }
                }

                // Find a trailer to clone. Prefer a flat, cargo-style one and AVOID construction
                // equipment haulers (Loader/Excavator/Crane look like a big rig, not a car
                // flatbed). Dump every candidate name once so a good model can be chosen.
                PrefabBase source = null;
                var trailerNames = new List<string>();
                foreach (PrefabBase candidate in prefabs)
                {
                    if (!(candidate is CarTrailerPrefab))
                    {
                        continue;
                    }
                    trailerNames.Add(candidate.name);
                    string n = candidate.name;
                    bool construction = n.Contains("Loader") || n.Contains("Excavator") ||
                        n.Contains("Crane") || n.Contains("Bulldozer") || n.Contains("Digger");
                    bool flatish = n.Contains("Flatbed") || n.Contains("Lowboy") ||
                        n.Contains("Cargo") || n.Contains("Container") || n.Contains("Car");
                    // Take the first non-construction trailer as a baseline; upgrade to a
                    // flat/cargo one if we find it.
                    if (source == null && !construction)
                    {
                        source = candidate;
                    }
                    if (flatish && !construction)
                    {
                        source = candidate;
                        break;
                    }
                }
                Mod.Log.Info($"[abschlepphaenger] available CarTrailerPrefabs: {string.Join(", ", trailerNames)}");
                if (source == null)
                {
                    ClearTheWay.Mod.Log.Warn("[abschlepphaenger] no CarTrailerPrefab found to clone - skipped");
                    s_Done = true;
                    return;
                }

                // Deep clone (empty instance + JSON copy of serialized fields + own components).
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

                // IMPORTANT: leave the clone a FAITHFUL copy of its source CarTrailer01 - do NOT
                // re-configure it. Flatbed towing is retired (drawbar does the job) so nothing here
                // ever spawns or couples this trailer; the prefab only still exists so that saves
                // which already contain instances of it resolve their PrefabRef on load (it is
                // registered pre-deserialization, see Mod.cs) instead of dangling into a
                // CarTrailerMoveSystem crash. The game's own random trailer selection hands
                // registered CarTrailerPrefabs to ordinary citizens; earlier we set it to
                // Towbar/Free with a tow-specific m_AttachOffset, which made those civilian-attached
                // instances glitch/spin ("der Trailer spinnt"). By keeping it byte-identical to a
                // real CarTrailer01 it behaves exactly like its vanilla twin wherever the game
                // attaches it - invisible, and guaranteed not to crash. Existing misconfigured
                // instances self-heal on the next load (they re-bake from this now-faithful prefab).
                // MUST register it: without AddPrefab the clone entity is never set up, so saved
                // instances that reference it by PrefabID resolve to a broken prefab and render as
                // an empty placeholder chunk (mesh missing) while still following - exactly the
                // "leeres Chunk" regression. Registration is also what makes the PrefabRef resolve
                // safely on load (see Mod.cs, pre-deserialization).
                prefabSystem.AddPrefab(clone);
                Prefab = prefabSystem.GetEntity(clone);
                ClearTheWay.Mod.Log.Info($"[abschlepphaenger] created '{kName}' from '{source.name}' (faithful copy of source)");
                s_Done = true;
            }
            catch (Exception e)
            {
                ClearTheWay.Mod.Log.Error(e, "[abschlepphaenger] creation failed");
                s_Done = true; // don't retry every preload
            }
        }
    }
}

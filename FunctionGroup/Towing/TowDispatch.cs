using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Dispatch assist for wreck recovery. Vanilla lets the generic dispatcher hand a wreck
    /// to ANY Vehicle-capable provider - often a van from a distant depot that first works
    /// through its road-maintenance queue and only "happens to come by" much later. Rules
    /// enforced here (every kInterval frames, per wreck that has an open recovery request):
    ///  1. If a Vehicle-capable maintenance van is already on the road NEARBY, it is
    ///     redirected to the wreck directly (order inserted at the front of its queue).
    ///  2. If none is nearby, the request goes straight to the dedicated tow depot, which
    ///     spawns an Abschleppwagen - a far-away road-maintenance van is sent home instead.
    ///  3. BEELINE: whoever holds the wreck order keeps driving straight to it - the order
    ///     stays at the front of its queue and TryWork/Working road-patching en route is
    ///     suppressed (that is what made them wander the map first).
    /// All wiring mirrors what the vanilla systems do themselves: ServiceDispatch buffer
    /// entries + a HandleRequest event (MaintenanceVehicleAISystem.SelectNextDispatch /
    /// MaintenanceDepotAISystem.SpawnVehicle), so the AIs accept the orders as their own.
    /// </summary>
    public partial class TowDispatchSystem : GameSystemBase
    {

        private SimulationSystem m_SimulationSystem;
        /// <summary>Shared dispatch state and passes; see DispatchContext.</summary>
        private DispatchContext m_Ctx;
        private Dictionary<Entity, uint> m_LastAction => m_Ctx.LastAction;
        private Dictionary<Entity, uint> m_VanSentHome => m_Ctx.VanSentHome;
        private Dictionary<Entity, Entity> m_VanJob => m_Ctx.VanJob;
        private List<Entity> m_PruneScratch => m_Ctx.PruneScratch;
        private TowHookupSystem m_HookupSystem;
        private EntityQuery m_WreckQuery;
        private EntityQuery m_VanQuery;
        private EntityQuery m_DepotQuery;
        private EntityQuery m_AllWreckQuery; // diagnostics only: settled wrecks regardless of MaintenanceConsumer
        private EntityQuery m_VanillaDamagedQuery; // diagnostics only: EXACT mirror of DamagedVehicleSystem.m_DamagedQuery
        private EntityArchetype m_HandleRequestArchetype;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_HookupSystem = World.GetOrCreateSystemManaged<TowHookupSystem>();
            m_HandleRequestArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<HandleRequest>(), ComponentType.ReadWrite<Game.Common.Event>());

            // Settled wrecks with an open recovery request.
            m_WreckQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Events.InvolvedInAccident>(),
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<Stopped>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<Game.Simulation.MaintenanceConsumer>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Events.OnFire>()
                }
            });

            // Diagnostics only (item 1 "Ruf-System"): every settled accident wreck, WITHOUT the
            // MaintenanceConsumer requirement m_WreckQuery imposes - so [towruf] can show whether
            // vanilla files a recovery request at all vs. TowDispatch simply not seeing them.
            m_AllWreckQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Events.InvolvedInAccident>(),
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<Stopped>(),
                    ComponentType.ReadOnly<Transform>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Events.OnFire>()
                }
            });

            // EXACT mirror of Game.Simulation.DamagedVehicleSystem.m_DamagedQuery: this is the
            // query whose entities vanilla actually turns into recovery requests. Requires
            // Game.Objects.Damaged + Stopped + Car. If settledWrecks > 0 while this is empty,
            // our wrecks are not "Damaged" in the game's eyes and vanilla will never file a
            // recovery request for them (the [towruf] "vanillaDamaged" field surfaces that).
            m_VanillaDamagedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Damaged>(),
                    ComponentType.ReadOnly<Stopped>(),
                    ComponentType.ReadOnly<Car>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>()
                }
            });

            // Maintenance vans on the road.
            m_VanQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<Game.Vehicles.MaintenanceVehicle>(),
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<CarCurrentLane>(),
                    ComponentType.ReadOnly<Owner>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<ParkedCar>()
                }
            });

            // Maintenance depots (the tow depot is found by its Vehicle-ONLY maintenance type).
            m_DepotQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.MaintenanceDepot>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>()
                }
            });

            // Built last: it captures the queries created above.
            m_Ctx = new DispatchContext
            {
                EntityManager = EntityManager,
                Simulation = m_SimulationSystem,
                HookupSystem = m_HookupSystem,
                WreckQuery = m_WreckQuery,
                VanQuery = m_VanQuery,
                DepotQuery = m_DepotQuery,
                AllWreckQuery = m_AllWreckQuery,
                VanillaDamagedQuery = m_VanillaDamagedQuery,
                HandleRequestArchetype = m_HandleRequestArchetype
            };
            m_Ctx.Assignment = new TowAssignment(m_Ctx);
            m_Ctx.Beeline = new TowBeeline(m_Ctx);
            m_Ctx.Reporting = new TowDispatchReporting(m_Ctx);
        }

        protected override void OnUpdate()
        {
            Setting setting = Mod.Setting;
            if (setting == null || !setting.Enabled || !setting.AssistTowTrucks)
            {
                return;
            }
            uint frame = m_SimulationSystem.frameIndex;
            // Item 1 diagnostic (runs BEFORE the early-return so it fires even when NO wreck has
            // a MaintenanceConsumer - which is exactly the case we need to catch).
            if (setting.VerboseLogging && frame % 120u == 0u)
            {
                m_Ctx.Reporting.LogRufDiagnostics();
            }
            if (frame % kInterval != 0u || m_WreckQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> wrecks = m_WreckQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Entity> vans = m_VanQuery.ToEntityArray(Allocator.Temp);
            try
            {
                // Pass 1 - ONE wreck per van, van-first. The dispatcher happily leaves
                // several wreck requests sitting in one van's buffer; iterating wrecks and
                // beelining each responder therefore re-targeted the SAME van once per
                // wreck, every pass: measured 2746 re-targets + repaths on a single van in
                // 20 minutes. Such a van never keeps a path long enough to drive off (it
                // stands still and blocks whatever queues behind it), and the repath flood
                // starves the pathfinder, which despawns unrelated vehicles - trams
                // included. So each van is resolved to exactly one job here, and gets at
                // most one repath per pass.
                m_VanJob.Clear();
                for (int i = 0; i < vans.Length; i++)
                {
                    Entity van = vans[i];
                    Entity wreck = m_Ctx.Beeline.ResolveVanJob(van, out Entity request);
                    if (wreck == Entity.Null)
                    {
                        continue;
                    }
                    m_VanJob[van] = wreck;
                    m_Ctx.Beeline.ShieldPath(van, frame);
                    float dist = math.distance(
                        EntityManager.GetComponentData<Transform>(van).m_Position.xz,
                        EntityManager.GetComponentData<Transform>(wreck).m_Position.xz);
                    if (dist > kHandsOffRange)
                    {
                        m_Ctx.Beeline.EnforceBeeline(van, wreck, request);
                    }
                }

                // Pass 2 - wrecks, for assignment decisions only (never re-targeting a van
                // that pass 1 already resolved).
                for (int w = 0; w < wrecks.Length; w++)
                {
                    m_Ctx.Assignment.DispatchWreck(wrecks[w], vans, frame, setting);
                }
            }
            finally
            {
                wrecks.Dispose();
                vans.Dispose();
            }

            // Occasionally drop cooldown entries for wrecks that no longer exist.
            if ((frame & 0x3FFu) == 0u && m_LastAction.Count != 0)
            {
                m_PruneScratch.Clear();
                foreach (KeyValuePair<Entity, uint> kv in m_LastAction)
                {
                    if (!EntityManager.Exists(kv.Key))
                    {
                        m_PruneScratch.Add(kv.Key);
                    }
                }
                for (int i = 0; i < m_PruneScratch.Count; i++)
                {
                    m_LastAction.Remove(m_PruneScratch[i]);
                }
            }

            // Drop expired / dead sent-home cooldowns.
            if ((frame & 0x3FFu) == 0u && m_VanSentHome.Count != 0)
            {
                m_PruneScratch.Clear();
                foreach (KeyValuePair<Entity, uint> kv in m_VanSentHome)
                {
                    if (frame - kv.Value >= kActionCooldown || !EntityManager.Exists(kv.Key))
                    {
                        m_PruneScratch.Add(kv.Key);
                    }
                }
                for (int i = 0; i < m_PruneScratch.Count; i++)
                {
                    m_VanSentHome.Remove(m_PruneScratch[i]);
                }
            }
        }













    }
}

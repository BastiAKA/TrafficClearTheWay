using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Diagnostics for the dispatch chain: how many settled wrecks exist, how many carry an open
    /// maintenance request, and how many the dispatcher can actually see.
    ///
    /// This is what settled the question of whether the mod needed to raise its own recovery
    /// requests - it does not. Vanilla raises them and the dispatcher sees them all, so the
    /// bottleneck was throughput, not missing requests.
    /// </summary>
    internal sealed class TowDispatchReporting
    {
        private readonly DispatchContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private SimulationSystem m_SimulationSystem => m_Ctx.Simulation;
        private TowHookupSystem m_HookupSystem => m_Ctx.HookupSystem;
        private EntityQuery m_WreckQuery => m_Ctx.WreckQuery;
        private EntityQuery m_VanQuery => m_Ctx.VanQuery;
        private EntityQuery m_DepotQuery => m_Ctx.DepotQuery;
        private EntityQuery m_AllWreckQuery => m_Ctx.AllWreckQuery;
        private EntityQuery m_VanillaDamagedQuery => m_Ctx.VanillaDamagedQuery;
        private EntityArchetype m_HandleRequestArchetype => m_Ctx.HandleRequestArchetype;
        private Dictionary<Entity, uint> m_LastAction => m_Ctx.LastAction;
        private Dictionary<Entity, uint> m_VanSentHome => m_Ctx.VanSentHome;
        private Dictionary<Entity, Entity> m_VanJob => m_Ctx.VanJob;
        private List<Entity> m_PruneScratch => m_Ctx.PruneScratch;

        public TowDispatchReporting(DispatchContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Item 1 diagnostic: how many settled wrecks exist, how many carry a
        /// MaintenanceConsumer, and how many have an OPEN recovery request - vs. how many
        /// TowDispatch's own query (which requires a consumer) actually sees. Answers whether
        /// vanilla files recovery requests at all, or whether they exist but go unserved.</summary>
        public void LogRufDiagnostics()
        {
            NativeArray<Entity> all = m_AllWreckQuery.ToEntityArray(Allocator.Temp);
            int n = all.Length, consumer = 0, openReq = 0;
            int damaged = 0, destroyed = 0, clearedGe1 = 0, damagedNoConsumer = 0;
            try
            {
                for (int i = 0; i < all.Length; i++)
                {
                    Entity wreck = all[i];
                    // Vanilla DamagedVehicleSystem processes ONLY entities carrying Game.Objects.Damaged
                    // (+ Stopped + Car). Break the settled wrecks down by the components that gate the
                    // recovery request: Damaged (is it even a candidate?), Destroyed + m_Cleared>=1
                    // (a cleared totaled wreck has its consumer REMOVED, so no request).
                    bool hasDamaged = EntityManager.HasComponent<Game.Objects.Damaged>(wreck);
                    bool hasDestroyed = EntityManager.HasComponent<Destroyed>(wreck);
                    bool hasConsumer = EntityManager.HasComponent<Game.Simulation.MaintenanceConsumer>(wreck);
                    if (hasDamaged) damaged++;
                    if (hasDestroyed)
                    {
                        destroyed++;
                        if (EntityManager.GetComponentData<Destroyed>(wreck).m_Cleared >= 1f) clearedGe1++;
                    }
                    if (hasDamaged && !hasConsumer) damagedNoConsumer++;
                    if (!hasConsumer)
                    {
                        continue;
                    }
                    consumer++;
                    Entity req = EntityManager.GetComponentData<Game.Simulation.MaintenanceConsumer>(wreck).m_Request;
                    if (req != Entity.Null && EntityManager.Exists(req) &&
                        EntityManager.HasComponent<MaintenanceRequest>(req))
                    {
                        openReq++;
                    }
                }
            }
            finally
            {
                all.Dispose();
            }
            Mod.Log.Info($"[towruf] settledWrecks={n} damaged={damaged} destroyed={destroyed} " +
                $"cleared>=1={clearedGe1} damagedNoConsumer={damagedNoConsumer} " +
                $"consumer={consumer} openReq={openReq} " +
                $"vanillaDamagedQuery={m_VanillaDamagedQuery.CalculateEntityCount()} " +
                $"inDispatchQuery={m_WreckQuery.CalculateEntityCount()}");
        }
    }
}

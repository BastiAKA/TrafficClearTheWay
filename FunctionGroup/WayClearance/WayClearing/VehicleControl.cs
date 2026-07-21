using System.Collections.Generic;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing
{
    /// <summary>
    /// The two primitives every pass uses to actually influence a vehicle, and the arbitration
    /// between them.
    ///
    /// SPEED is not written directly: CarNavigationSystem recomputes m_MaxSpeed from scratch for
    /// the ~1/16 of vehicles it processes each tick, so anything written before it is lost on
    /// exactly the tick a vehicle is simulated. Passes record an intent here and
    /// <see cref="ClearTheWayPostSystem"/> applies it afterwards. A ceiling (hold down) always
    /// beats a floor (grant up) and the lowest ceiling wins - so a car several passes want to
    /// hold is never accidentally granted speed by one of them.
    ///
    /// STUCK FLAGS are cleared because the vehicle AIs delete a vehicle whose path is flagged
    /// stuck or failed. Our own maneuvers deliberately hold cars still, which is exactly what
    /// trips that flag - without this the mod despawns the traffic it is trying to organise.
    /// </summary>
    internal sealed class VehicleControl
    {
        private readonly EntityManager EntityManager;
        private readonly Dictionary<Entity, SpeedOverride> m_SpeedOverrides = new Dictionary<Entity, SpeedOverride>();

        /// <summary>This tick's pending overrides, drained by ClearTheWayPostSystem.</summary>
        public IReadOnlyDictionary<Entity, SpeedOverride> SpeedOverrides => m_SpeedOverrides;

        public VehicleControl(EntityManager entityManager)
        {
            EntityManager = entityManager;
        }

        /// <summary>Drops the previous tick's overrides. Called once at the start of the pass.</summary>
        public void BeginTick()
        {
            m_SpeedOverrides.Clear();
        }
        public void SetCeiling(Entity entity, float speed)
        {
            if (m_SpeedOverrides.TryGetValue(entity, out SpeedOverride existing))
            {
                // A ceiling always wins over a floor (safety: never grant speed to a car
                // we also want to hold), and lower ceilings win.
                if (existing.m_Mode == 0)
                {
                    existing.m_Speed = math.min(existing.m_Speed, speed);
                }
                else
                {
                    existing.m_Speed = speed;
                    existing.m_Mode = 0;
                    existing.m_HasTarget = false;
                }
                m_SpeedOverrides[entity] = existing;
                return;
            }
            m_SpeedOverrides[entity] = new SpeedOverride { m_Speed = speed, m_Mode = 0 };
        }

        public void SetFloor(Entity entity, float speed, bool hasTarget, float3 target, quaternion rotation)
        {
            if (m_SpeedOverrides.TryGetValue(entity, out SpeedOverride existing) && existing.m_Mode == 0)
            {
                return; // ceiling wins
            }
            m_SpeedOverrides[entity] = new SpeedOverride
            {
                m_Speed = speed,
                m_Mode = 1,
                m_HasTarget = hasTarget,
                m_Target = target,
                m_Rotation = rotation
            };
        }

        /// <summary>
        /// Clears the game's stuck flag on a vehicle so it is not despawned by its AI while
        /// our maneuver deliberately holds/slows it. Safe no-op if the flag is not set.
        /// </summary>
        public void ClearStuck(Entity entity)
        {
            if (!EntityManager.HasComponent<PathOwner>(entity))
            {
                return;
            }
            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(entity);
            if ((pathOwner.m_State & PathFlags.Stuck) != 0)
            {
                pathOwner.m_State &= ~PathFlags.Stuck;
                EntityManager.SetComponentData(entity, pathOwner);
            }
        }

        /// <summary>
        /// Clears both stuck AND failed path flags - PathfindFailed = Failed|Stuck, and
        /// dummy traffic despawns on PathfindFailed. Behind an accident the route is still
        /// valid (a wreck is a dynamic obstacle, not a network cut), so the car should just
        /// wait.
        /// </summary>
        public void ClearStuckAndFailed(Entity entity)
        {
            if (!EntityManager.HasComponent<PathOwner>(entity))
            {
                return;
            }
            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(entity);
            if ((pathOwner.m_State & (PathFlags.Stuck | PathFlags.Failed)) != 0)
            {
                pathOwner.m_State &= ~(PathFlags.Stuck | PathFlags.Failed);
                EntityManager.SetComponentData(entity, pathOwner);
            }
        }
    }
}

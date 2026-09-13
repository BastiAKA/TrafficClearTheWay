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
        /// <summary>
        /// A recovery vehicle on its way home with a load. Recognised by the flags
        /// TowFollow.KeepTruckReturning pins on a loaded truck every tick.
        ///
        /// An ordinary maintenance van driving home full matches this too, deliberately: it is
        /// leaving anyway, so not slowing it costs nothing, and a test that needed the towing
        /// group's live TrucksWithLoad set would tie this leaf class to another system for no gain.
        /// </summary>
        private bool IsHaulingHome(Entity entity)
        {
            if (!EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(entity))
            {
                return false;
            }
            Game.Vehicles.MaintenanceVehicleFlags state =
                EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(entity).m_State;
            return (state & Game.Vehicles.MaintenanceVehicleFlags.Returning) != 0 &&
                   (state & Game.Vehicles.MaintenanceVehicleFlags.Full) != 0;
        }

        public void SetCeiling(Entity entity, float speed)
        {
            // Never throttle a recovery vehicle hauling a wreck home. Every ceiling in the mod
            // exists to open a path for a responder - and this one vehicle is not traffic in the
            // way, it IS the clearance, carrying the obstruction off the road. Holding it closes a
            // loop that nothing else can open, and we watched it happen (2026-08-09, a police car
            // standing beside a loaded tow truck on a four-lane road): the responder is stuck, so
            // its corridor holds everything around it including the truck; the truck cannot leave,
            // so the wreck stays on the carriageway; so the responder stays stuck.
            if (IsHaulingHome(entity))
            {
                return;
            }
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

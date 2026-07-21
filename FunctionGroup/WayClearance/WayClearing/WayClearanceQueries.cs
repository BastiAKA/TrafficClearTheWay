using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using Unity.Entities;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing
{
    /// <summary>
    /// The entity queries the way-clearance system runs on, as plain descriptions.
    ///
    /// They are here rather than inline in OnCreate because each one encodes a decision that is
    /// easy to get subtly wrong and expensive to debug - the comments are the point.
    /// </summary>
    internal static class WayClearanceQueries
    {
        /// <summary>Responders actively driving with lights and siren: the ones that get a corridor.</summary>
        public static EntityQueryDesc Emergency => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadWrite<CarCurrentLane>(),
                ComponentType.ReadOnly<CarNavigationLane>(),
                ComponentType.ReadOnly<Blocker>(),
                ComponentType.ReadOnly<Moving>()
            },
            Any = new[]
            {
                ComponentType.ReadOnly<Game.Vehicles.PoliceCar>(),
                ComponentType.ReadOnly<Game.Vehicles.Ambulance>(),
                ComponentType.ReadOnly<Game.Vehicles.FireEngine>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Destroyed>(),
                ComponentType.ReadOnly<ParkedCar>(),
                ComponentType.ReadOnly<OutOfControl>()
            }
        };

        /// <summary>
        /// EVERY police/ambulance/fire vehicle, including stopped and returning ones that the
        /// corridor query above misses because it requires Moving.
        ///
        /// This is the shield against the AIs' "PathfindFailed and (Returning or Stuck) => Deleted"
        /// branch: at a fully blocked accident it is the RETURN path that fails, so a unit which
        /// just finished its job evaporates on the spot.
        /// </summary>
        public static EntityQueryDesc EmergencyPath => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadWrite<PathOwner>()
            },
            Any = new[]
            {
                ComponentType.ReadOnly<Game.Vehicles.PoliceCar>(),
                ComponentType.ReadOnly<Game.Vehicles.Ambulance>(),
                ComponentType.ReadOnly<Game.Vehicles.FireEngine>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<ParkedCar>()
            }
        };

        /// <summary>
        /// Recovery / tow vehicles: road-maintenance vehicles that can recover damaged vehicles.
        /// One heading to a crash gets a gentle make-way corridor - the game already dispatches
        /// these to accidents on its own.
        /// </summary>
        public static EntityQueryDesc Assist => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Game.Vehicles.MaintenanceVehicle>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadWrite<CarCurrentLane>(),
                ComponentType.ReadOnly<Target>(),
                ComponentType.ReadOnly<Moving>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<ParkedCar>()
            }
        };

        /// <summary>
        /// Crashed vehicles blocking a lane; the traffic queued behind them is protected from the
        /// stuck-traffic despawn so it waits for recovery.
        ///
        /// Keyed on InvolvedInAccident because that covers Damaged AND Destroyed - a Damaged-only
        /// query missed every totaled wreck.
        /// </summary>
        public static EntityQueryDesc Wrecks => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Game.Events.InvolvedInAccident>(),
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<CarCurrentLane>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Temp>()
            }
        };
    }
}

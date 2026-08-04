using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;

namespace ClearTheWay
{
    /// <summary>
    /// Companion pass that runs AFTER CarNavigationSystem (right before CarMoveSystem) and
    /// applies the speed/steering overrides the main system recorded. CarNavigationSystem
    /// recomputes m_MaxSpeed and m_TargetPosition from scratch for every vehicle it touches,
    /// so these must be re-applied here or they are lost on exactly the tick a vehicle is
    /// simulated - which is why held cars used to drive off and stuck vehicles never nosed
    /// out. Kept intentionally tiny: it only reads a dictionary and writes CarNavigation.
    /// </summary>
    public partial class ClearTheWayPostSystem : GameSystemBase
    {
        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = false;

        private ClearTheWaySystem m_MainSystem;
        private TowHookupSystem m_HookupSystem;
        private SimulationSystem m_SimulationSystem;
        private EntityQuery m_ServiceQuery;

        /// <summary>Vehicles the last duty scan found to be on recovery duty. The flag itself is
        /// re-asserted from this set EVERY tick (the AI clears it), but deciding who belongs in it
        /// is the expensive part and runs only every kBeaconScanInterval ticks - the same
        /// scan-rarely / re-apply-always split the pedestrian hold uses.</summary>
        private readonly HashSet<Entity> m_OnRecoveryDuty = new HashSet<Entity>();
        private readonly List<Entity> m_DutyScratch = new List<Entity>();
        private const uint kBeaconScanInterval = 4u;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_MainSystem = World.GetOrCreateSystemManaged<ClearTheWaySystem>();
            m_HookupSystem = World.GetOrCreateSystemManaged<TowHookupSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            // Maintenance vehicles on the road, for the amber-beacon pass.
            m_ServiceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Vehicles.MaintenanceVehicle>(),
                    ComponentType.ReadWrite<Car>(),
                    ComponentType.ReadOnly<Target>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<ParkedCar>()
                }
            });
        }

        /// <summary>
        /// Amber beacons (CarFlags.Warning) for recovery duty: on while a maintenance
        /// vehicle is driving to / repairing a wreck and while it is towing one home.
        /// Vanilla only flashes them during road-side work and clears them on every
        /// dispatch change, so recovery runs looked like ordinary commutes. Runs AFTER
        /// MaintenanceVehicleAISystem (this system sits right before CarMoveSystem), so
        /// our flag survives the AI's per-tick clears.
        /// </summary>
        private void ApplyRecoveryBeacons()
        {
            if (m_ServiceQuery.IsEmptyIgnoreFilter)
            {
                m_OnRecoveryDuty.Clear();
                return;
            }
            // Off-tick: the duty verdict is stable for a few ticks, so only re-assert the flag on
            // the (small) set already known to be on duty. Scanning EVERY maintenance vehicle in
            // the city - a Target lookup plus up to four component tests each - every single tick
            // was pure repetition: whether a truck is hauling a wreck does not change at 58 Hz.
            if (m_SimulationSystem.frameIndex % kBeaconScanInterval != 0u)
            {
                if (m_OnRecoveryDuty.Count == 0)
                {
                    return;
                }
                m_DutyScratch.Clear();
                m_DutyScratch.AddRange(m_OnRecoveryDuty);
                for (int i = 0; i < m_DutyScratch.Count; i++)
                {
                    Entity vehicle = m_DutyScratch[i];
                    // Gone or parked: drop it and take the beacon back off. Nothing else ever
                    // clears the flag, so a truck that parked between scans must not keep
                    // flashing on the depot apron until the next one.
                    if (!EntityManager.Exists(vehicle) || !EntityManager.HasComponent<Car>(vehicle) ||
                        EntityManager.HasComponent<Game.Vehicles.ParkedCar>(vehicle))
                    {
                        m_OnRecoveryDuty.Remove(vehicle);
                        ClearBeacon(vehicle);
                        continue;
                    }
                    Car onDutyCar = EntityManager.GetComponentData<Car>(vehicle);
                    if ((onDutyCar.m_Flags & CarFlags.Warning) == 0)
                    {
                        onDutyCar.m_Flags |= CarFlags.Warning;
                        EntityManager.SetComponentData(vehicle, onDutyCar);
                    }
                }
                return;
            }

            m_OnRecoveryDuty.Clear();
            NativeArray<Entity> vehicles = m_ServiceQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    // Parked = off duty, beacons off. This needs an explicit CLEAR, not just a
                    // skip: we re-assert Warning every tick after the AI has run, so a truck that
                    // parked while its Target still pointed at a wreck kept flashing on the depot
                    // apron forever - nothing else ever takes the flag back off.
                    if (EntityManager.HasComponent<Game.Vehicles.ParkedCar>(vehicle))
                    {
                        ClearBeacon(vehicle);
                        continue;
                    }
                    bool onRecoveryDuty = m_HookupSystem.TrucksWithLoad.Contains(vehicle);
                    if (!onRecoveryDuty)
                    {
                        Entity target = EntityManager.GetComponentData<Target>(vehicle).m_Target;
                        onRecoveryDuty = target != Entity.Null && EntityManager.Exists(target) &&
                            (EntityManager.HasComponent<Damaged>(target) ||
                             EntityManager.HasComponent<Game.Events.InvolvedInAccident>(target));
                    }
                    if (!onRecoveryDuty)
                    {
                        continue;
                    }
                    m_OnRecoveryDuty.Add(vehicle);
                    Car car = EntityManager.GetComponentData<Car>(vehicle);
                    if ((car.m_Flags & CarFlags.Warning) == 0)
                    {
                        car.m_Flags |= CarFlags.Warning;
                        EntityManager.SetComponentData(vehicle, car);
                    }
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        /// <summary>Takes the amber beacon back off a vehicle that is off recovery duty. Explicit,
        /// because we are the only thing that ever sets the flag for recovery runs - nothing else
        /// clears it again.</summary>
        private void ClearBeacon(Entity vehicle)
        {
            if (!EntityManager.Exists(vehicle) || !EntityManager.HasComponent<Car>(vehicle))
            {
                return;
            }
            Car car = EntityManager.GetComponentData<Car>(vehicle);
            if ((car.m_Flags & CarFlags.Warning) != 0)
            {
                car.m_Flags &= ~CarFlags.Warning;
                EntityManager.SetComponentData(vehicle, car);
            }
        }

        protected override void OnUpdate()
        {
            using (ModProfiler.Sample(kProfile, "ClearTheWayPostSystem"))
            {
                RunPass();
            }
            ModProfiler.EndTick(kProfile, "ClearTheWayPostSystem");
        }

        private void RunPass()
        {
            Setting setting = Mod.Setting;
            if (setting != null && setting.Enabled && setting.AssistTowTrucks)
            {
                ApplyRecoveryBeacons();
            }
            IReadOnlyDictionary<Entity, SpeedOverride> overrides = m_MainSystem.SpeedOverrides;
            if (overrides.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<Entity, SpeedOverride> entry in overrides)
            {
                Entity entity = entry.Key;
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<CarNavigation>(entity))
                {
                    continue;
                }
                SpeedOverride ov = entry.Value;
                CarNavigation navigation = EntityManager.GetComponentData<CarNavigation>(entity);
                if (ov.m_Mode == 0)
                {
                    // Ceiling: hold the car (never speed it up, only cap it down).
                    if (navigation.m_MaxSpeed > ov.m_Speed)
                    {
                        navigation.m_MaxSpeed = ov.m_Speed;
                        EntityManager.SetComponentData(entity, navigation);
                    }
                    // Protect every held car from the game's stuck-vehicle despawn.
                    if (EntityManager.HasComponent<PathOwner>(entity))
                    {
                        PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(entity);
                        if ((pathOwner.m_State & PathFlags.Stuck) != 0)
                        {
                            pathOwner.m_State &= ~PathFlags.Stuck;
                            EntityManager.SetComponentData(entity, pathOwner);
                        }
                    }
                }
                else
                {
                    // Floor: grant a speed budget and, for a stopped vehicle, a sideways
                    // target so it noses toward its offset instead of forward into its
                    // blocker. Crucially we do NOT override m_TargetRotation any more:
                    // pointing the heading at the sideways target made vehicles turn nearly
                    // perpendicular and weave. Leaving the game's rotation keeps a natural
                    // forward-ish heading while the body drifts toward the offset.
                    bool write = false;
                    if (ov.m_HasTarget)
                    {
                        navigation.m_TargetPosition = ov.m_Target;
                        write = true;
                    }
                    if (navigation.m_MaxSpeed < ov.m_Speed)
                    {
                        navigation.m_MaxSpeed = ov.m_Speed;
                        write = true;
                    }
                    if (write)
                    {
                        EntityManager.SetComponentData(entity, navigation);
                    }
                    // Also protect these from the stuck-despawn while we steer/nudge them.
                    if (EntityManager.HasComponent<PathOwner>(entity))
                    {
                        PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(entity);
                        if ((pathOwner.m_State & PathFlags.Stuck) != 0)
                        {
                            pathOwner.m_State &= ~PathFlags.Stuck;
                            EntityManager.SetComponentData(entity, pathOwner);
                        }
                    }
                }
            }
        }
    }
}

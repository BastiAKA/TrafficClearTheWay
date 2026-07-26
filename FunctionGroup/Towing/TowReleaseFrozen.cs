using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Frees cars this mod froze on the road: Stopped and OutOfControl, with no damage left and no
    /// live accident - roadworthy vehicles that are simply not allowed to move any more.
    ///
    /// HOW WE MADE THEM. Vanilla never tows a repairable car. MaintenanceVehicleAISystem works its
    /// Damaged.m_Damage down to zero on the spot, DamageSystem then drops Damaged (and the
    /// MaintenanceConsumer with it), and AccidentVehicleSystem's "no Damaged left" branch calls
    /// StartVehicle + ClearAccident so the car drives off by itself. ClearAccident is also the ONLY
    /// place in the game that ever removes OutOfControl, and it removes it together with
    /// InvolvedInAccident - always as a pair.
    ///
    /// Our coupling used to break that pair: it stripped InvolvedInAccident and left OutOfControl
    /// behind. That single leftover component is a life sentence. Every vehicle AI query carries
    /// ComponentType.Exclude&lt;OutOfControl&gt;() (PersonalCarAISystem, PersonalCarOwnerSystem and
    /// the rest), so nothing drives the car and nothing cleans it up; without InvolvedInAccident,
    /// AccidentVehicleSystem can never reach it to lift the sentence; and without Damaged,
    /// DamagedVehicleSystem (All&lt;Damaged, Stopped, Car&gt;) never raises a recovery request, so no
    /// truck is dispatched either. It is also invisible to every relic query we own, because those
    /// all require Any{Damaged, Destroyed}. The car just stands there - the game labels it
    /// "Abandoned Vehicle" - until the save is deleted.
    ///
    /// WHAT WE DO. Exactly what vanilla would have done at the end of the accident: drop the stale
    /// ownership tags, remove OutOfControl, and restart the vehicle (StartVehicle's component set).
    /// The car rejoins traffic under its own power - no tow truck, no despawn, no lost citizen.
    /// Deleting them would be defensible (it is vanilla's own answer to a dangling Controller), but
    /// a roadworthy car deserves the cheaper fix.
    ///
    /// The hookup gate in <see cref="TowCoupling"/> stops new ones being made; this pass clears out
    /// the ones already stranded in a save.
    /// </summary>
    internal sealed class TowReleaseFrozen
    {
        private readonly TowingContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private EntityQuery m_Query => m_Ctx.FrozenReleaseQuery;
        private Dictionary<Entity, uint> m_FrozenSince => m_Ctx.FrozenSince;

        private readonly HashSet<Entity> m_Seen = new HashSet<Entity>();
        private readonly List<Entity> m_Prune = new List<Entity>();

        public TowReleaseFrozen(TowingContext ctx)
        {
            m_Ctx = ctx;
        }

        public void ReleaseFrozenCars(uint frame, Setting setting)
        {
            m_Seen.Clear();
            if (!m_Query.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> cars = m_Query.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < cars.Length; i++)
                    {
                        Entity car = cars[i];
                        m_Seen.Add(car);
                        // A car whose Controller is a LIVE recovery truck is genuinely on a hook
                        // (or mid-hookup) - never touch that, whatever its damage state.
                        if (EntityManager.HasComponent<Controller>(car))
                        {
                            Entity ctrl = EntityManager.GetComponentData<Controller>(car).m_Controller;
                            if (ctrl != car && ctrl != Entity.Null && EntityManager.Exists(ctrl) &&
                                EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(ctrl))
                            {
                                continue;
                            }
                        }
                        // Settle time. The state is permanent once reached, so this is not about
                        // waiting for it to resolve on its own - it is a guard against catching a
                        // car mid-transition, while some other system's command buffer still has
                        // the rest of its changes queued.
                        if (!m_FrozenSince.TryGetValue(car, out uint since))
                        {
                            m_FrozenSince[car] = frame;
                            continue;
                        }
                        if (frame - since < kFrozenReleaseGraceFrames)
                        {
                            continue;
                        }
                        Release(car, setting);
                    }
                }
                finally
                {
                    cars.Dispose();
                }
            }

            // Forget cars that left the query (released, deleted, damaged again) so the map
            // cannot grow unbounded.
            if (m_FrozenSince.Count != 0)
            {
                m_Prune.Clear();
                foreach (KeyValuePair<Entity, uint> kv in m_FrozenSince)
                {
                    if (!m_Seen.Contains(kv.Key) || !EntityManager.Exists(kv.Key))
                    {
                        m_Prune.Add(kv.Key);
                    }
                }
                for (int i = 0; i < m_Prune.Count; i++)
                {
                    m_FrozenSince.Remove(m_Prune[i]);
                }
                m_Prune.Clear();
            }
        }

        /// <summary>
        /// Hand the car back to its own AI: drop every stale tag we or a failed recovery left on
        /// it, remove the OutOfControl that blocks all vehicle AI, then restart it exactly as
        /// AccidentVehicleSystem.StartVehicle does.
        /// </summary>
        private void Release(Entity car, Setting setting)
        {
            // RELEASE IS THE DANGEROUS DIRECTION - restarting a car that is not fit to drive is far
            // worse than leaving it standing. Adding Moving hands the entity to the Burst movement
            // jobs, and those read their inputs unguarded; a car missing a piece of its driving
            // state takes the game down with it (the CarTrailerMoveSystem crashes on branch
            // wreck-as-trailer-B are the same failure mode). So every precondition is proven
            // BEFORE anything is touched, and anything unproven is simply left alone - the
            // 10-minute no-movement sweep in TowOrphanFinder is the terminal state for those.
            //
            //  - Passenger: somebody has to be aboard to drive it away. An empty personal car has
            //    no occupant whose trip would resume, which is also why vanilla's own restart
            //    (AccidentVehicleSystem StartVehicle + ClearAccident) only ever fires on cars that
            //    still hold their people.
            //  - CarCurrentLane / UpdateFrame: in every vehicle AI query, so without them removing
            //    OutOfControl wakes nothing up and Moving would be added to a car nothing steers.
            //  - PathOwner: the AI needs it to re-path from where the car is standing.
            bool hasPassenger = EntityManager.HasBuffer<Passenger>(car) &&
                EntityManager.GetBuffer<Passenger>(car, isReadOnly: true).Length != 0;
            if (!hasPassenger)
            {
                // Nobody aboard: there is no trip to resume and no driver, so releasing it would
                // only put a driverless car on the road. Sebastian's call - tow it instead.
                Condemn(car, setting);
                return;
            }
            if (!EntityManager.HasComponent<CarCurrentLane>(car) ||
                !EntityManager.HasComponent<Game.Simulation.UpdateFrame>(car) ||
                !EntityManager.HasComponent<Game.Pathfind.PathOwner>(car))
            {
                if (setting.VerboseLogging)
                {
                    Mod.Log.Info($"[release] car={car.Index} NOT released - occupied but unfit to drive: " +
                        $"lane={(EntityManager.HasComponent<CarCurrentLane>(car) ? 1 : 0)} " +
                        $"updframe={(EntityManager.HasComponent<Game.Simulation.UpdateFrame>(car) ? 1 : 0)} " +
                        $"pathowner={(EntityManager.HasComponent<Game.Pathfind.PathOwner>(car) ? 1 : 0)}; " +
                        $"left standing for the stranded sweep");
                }
                return;
            }

            if (EntityManager.HasComponent<TowMarker>(car))
            {
                EntityManager.RemoveComponent<TowMarker>(car);
            }
            if (EntityManager.HasComponent<RelicRecovery>(car))
            {
                EntityManager.RemoveComponent<RelicRecovery>(car);
            }
            // Any Controller still on it points at nothing, itself, or a dead truck (a live
            // recovery carrier was skipped above). MUST come off BEFORE Moving is added below:
            // to vanilla, a vehicle whose Controller is not itself IS a trailer, so a Moving car
            // still carrying one gets picked up by CarTrailerMoveSystem - which reads tractor and
            // trailer data straight off prefabs an ordinary car does not have, and hard-crashes.
            // Everything here runs on the main thread in one pass, so no job can observe the
            // half-finished state in between.
            if (EntityManager.HasComponent<Controller>(car))
            {
                EntityManager.RemoveComponent<Controller>(car);
            }
            if (EntityManager.HasComponent<Game.Simulation.MaintenanceConsumer>(car))
            {
                Entity req = EntityManager.GetComponentData<Game.Simulation.MaintenanceConsumer>(car).m_Request;
                if (req != Entity.Null && EntityManager.Exists(req))
                {
                    EntityManager.AddComponent<Deleted>(req);
                }
                EntityManager.RemoveComponent<Game.Simulation.MaintenanceConsumer>(car);
            }
            // The life sentence itself.
            EntityManager.RemoveComponent<Game.Vehicles.OutOfControl>(car);

            // AccidentVehicleSystem.StartVehicle, component for component. The TransformFrame ring
            // is seeded from the current transform rather than left empty (vanilla's AddBuffer):
            // ObjectInterpolateSystem reads the ring immediately and an empty one renders the car
            // at the origin for a frame - the same seeding TowCoupling does when it restarts a
            // truck.
            Transform transform = EntityManager.GetComponentData<Transform>(car);
            EntityManager.RemoveComponent<Stopped>(car);
            DynamicBuffer<TransformFrame> frames = EntityManager.AddBuffer<TransformFrame>(car);
            for (int i = 0; i < 4; i++)
            {
                frames.Add(new TransformFrame(transform));
            }
            EntityManager.AddComponentData(car, new Game.Rendering.InterpolatedTransform(transform));
            EntityManager.AddComponentData(car, default(Moving));
            EntityManager.AddComponentData(car, default(Game.Rendering.Swaying));
            EntityManager.AddComponent<Updated>(car);
            PokeBlockedLanes(car);

            if (setting.VerboseLogging)
            {
                Mod.Log.Info($"[release] car={car.Index} released - OutOfControl removed, " +
                    $"restarted; it can drive away again");
            }
        }

        /// <summary>
        /// Declare an empty frozen car a wreck, so the ordinary recovery chain hauls it away.
        ///
        /// It IS a derelict: nobody aboard, no owner acting on it, no damage to repair, and frozen
        /// out of every AI. Rather than inventing a parallel dispatch for it, we give it the state
        /// that makes it a wreck by the game's own definition, and every proven path then applies.
        ///
        /// Both components are needed, and each for a specific reason:
        ///  - Destroyed makes MaintenanceVehicleAISystem take its CLEAR-DEBRIS branch (it checks
        ///    Destroyed first, ~L1397) instead of the repair branch, so the car is removed rather
        ///    than patched up and sent on its way. It is also what our own tow gate requires.
        ///  - Damaged is what DamagedVehicleSystem's query demands (All&lt;Damaged, Stopped, Car&gt;);
        ///    without it no maintenance request is ever raised and no truck is dispatched. With
        ///    Destroyed present its job takes the destroyed branch, which only asks whether
        ///    m_Cleared &lt; 1, so the damage figure itself is never used for anything.
        /// RelicRecovery is the "valid target" tag TowCoupling accepts in place of
        /// InvolvedInAccident, and it puts the car under the relic watchdog's delete fallback, so
        /// even if no truck ever reaches it the car cannot linger.
        /// </summary>
        private void Condemn(Entity car, Setting setting)
        {
            // Stale ownership first - a leftover Controller blocks hookup, and a stale request
            // would stop DamagedVehicleSystem raising a fresh one.
            if (EntityManager.HasComponent<TowMarker>(car))
            {
                EntityManager.RemoveComponent<TowMarker>(car);
            }
            if (EntityManager.HasComponent<Controller>(car))
            {
                EntityManager.RemoveComponent<Controller>(car);
            }
            if (EntityManager.HasComponent<Game.Simulation.MaintenanceConsumer>(car))
            {
                Entity req = EntityManager.GetComponentData<Game.Simulation.MaintenanceConsumer>(car).m_Request;
                if (req != Entity.Null && EntityManager.Exists(req))
                {
                    EntityManager.AddComponent<Deleted>(req);
                }
                EntityManager.RemoveComponent<Game.Simulation.MaintenanceConsumer>(car);
            }
            // Stopped and OutOfControl stay: it is a wreck now, and OutOfControl correctly keeps
            // every vehicle AI off it while it waits for the truck.
            if (!EntityManager.HasComponent<Damaged>(car))
            {
                EntityManager.AddComponentData(car, new Damaged(new float3(1f, 0f, 0f)));
            }
            if (!EntityManager.HasComponent<Destroyed>(car))
            {
                EntityManager.AddComponentData(car, default(Destroyed));
            }
            if (!EntityManager.HasComponent<RelicRecovery>(car))
            {
                EntityManager.AddComponent<RelicRecovery>(car);
            }
            m_Ctx.RelicArmedFrame[car] = m_Ctx.Simulation.frameIndex;
            if (setting.VerboseLogging)
            {
                Mod.Log.Info($"[release] car={car.Index} condemned - empty and frozen, tagged as a " +
                    $"wreck so a tow truck hauls it away");
            }
        }

        /// <summary>Recompute the blockage/pathfind data of every lane the car was blocking, so
        /// traffic routes through the space it is about to vacate. Same poke StartVehicle does.</summary>
        private void PokeBlockedLanes(Entity car)
        {
            if (!EntityManager.HasBuffer<BlockedLane>(car))
            {
                return;
            }
            DynamicBuffer<BlockedLane> lanes = EntityManager.GetBuffer<BlockedLane>(car, isReadOnly: true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_Lane;
                if (EntityManager.HasComponent<Game.Net.CarLane>(lane) &&
                    !EntityManager.HasComponent<PathfindUpdated>(lane))
                {
                    EntityManager.AddComponent<PathfindUpdated>(lane);
                }
            }
        }
    }
}

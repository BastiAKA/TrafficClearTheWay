using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Coupling a wreck onto a recovery truck.
    ///
    /// The wreck keeps its ENTIRE settled-wreck archetype (Stopped, no Moving, no TransformFrame).
    /// Two earlier attempts to make it a moving vehicle or a real trailer hard-crashed in Burst
    /// jobs, because the vanilla trailer machinery reads tractor/trailer data off the PREFAB with
    /// unguarded lookups and a plain car has none. So coupling is purely: strip the accident
    /// markers, add a Controller as a data marker, add the persistent TowMarker, and let the
    /// follow pass teleport it - the same operation that clearing a wreck aside already used
    /// safely for hours.
    ///
    /// The truck is then pinned Full and Returning. That is not belt-and-braces: the maintenance
    /// AI recomputes Full from its load EVERY tick, so setting the flags alone gets cleared next
    /// frame, dispatch reopens and the truck abandons the wreck it just picked up.
    /// </summary>
    internal sealed class TowCoupling
    {
        // Main-thread profiling switch for this pass - see ModProfiler.
        private static readonly bool kProfile = false;

        private readonly TowingContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private SimulationSystem m_SimulationSystem => m_Ctx.Simulation;
        private HashSet<Entity> m_TrucksWithLoad => m_Ctx.TrucksWithLoad;
        private HashSet<Entity> m_OurTows => m_Ctx.OurTows;
        private Dictionary<Entity, float3> m_TowTravelDir => m_Ctx.TowTravelDir;
        private Dictionary<Entity, float3> m_LastTruckPos => m_Ctx.LastTruckPos;
        private Dictionary<Entity, uint> m_RelicArmedFrame => m_Ctx.RelicArmedFrame;
        private EntityQuery m_OrphanQuery => m_Ctx.OrphanQuery;
        private EntityQuery m_TrailerOrphanQuery => m_Ctx.TrailerOrphanQuery;
        private EntityQuery m_ArmedRelicQuery => m_Ctx.ArmedRelicQuery;
        private EntityQuery m_RelicDiagQuery => m_Ctx.RelicDiagQuery;
        private EntityQuery m_RelicSweepDryRun => m_Ctx.RelicSweepDryRun;
        private EntityQuery m_AccidentSiteQuery => m_Ctx.AccidentSiteQuery;

        /// <summary>Trucks already reported as "not the dedicated tow truck", so the unchanging
        /// fact is stated once each instead of every diagnostic pass.</summary>
        internal readonly HashSet<Entity> NotTowTruckLogged = new HashSet<Entity>();
        private HashSet<Entity> m_NotTowTruckLogged => NotTowTruckLogged;

        /// <summary>Wrecks already reported as an invalid hookup target - same reasoning.</summary>
        internal readonly HashSet<Entity> InvalidTargetLogged = new HashSet<Entity>();
        private HashSet<Entity> m_InvalidTargetLogged => InvalidTargetLogged;

        public TowCoupling(TowingContext ctx)
        {
            m_Ctx = ctx;
        }

        public void TryHookup(Entity truck, uint frame)
        {
            using (ModProfiler.Sample(kProfile, "TowCoupling"))
            {
                TryHookupImpl(truck, frame);
            }
        }

        private void TryHookupImpl(Entity truck, uint frame)
        {
            bool diag = Mod.Setting.VerboseLogging && frame % 120u == 0u;
            // Only vehicle-recovery capable trucks, one load at a time.
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(truck).m_Prefab;
            if (!EntityManager.HasComponent<MaintenanceVehicleData>(prefab) ||
                (EntityManager.GetComponentData<MaintenanceVehicleData>(prefab).m_MaintenanceType & MaintenanceType.Vehicle) == 0 ||
                m_TrucksWithLoad.Contains(truck))
            {
                return;
            }
            // With the dedicated tow truck active, ONLY our tractor-capable Abschleppwagen tows.
            // The game dispatches ANY vehicle-capable maintenance van to a wreck (incl. idle
            // road-maintenance vans it reuses from other depots); left to the old path those
            // would drawbar-drag the wreck ("Straßenwartungsfahrzeug" without a flatbed). Skip
            // them here so they just clear the wreck the vanilla way and the only towing you see
            // is our proper flatbed. (When TowTruckPrefab is off, every recovery truck drawbars.)
            if (Mod.Setting.TowTruckPrefab && !EntityManager.HasComponent<CarTractorData>(prefab))
            {
                // ONCE per truck, never on a timer: whether a vehicle has CarTractorData is a
                // static property of its prefab and cannot change. Logged per pass it produced
                // 7403 of 12454 log lines in nine minutes - 112 ordinary maintenance vans each
                // repeating the same unchanging fact every 120 frames, 2.3 MB of file I/O on the
                // simulation thread. The line earned its keep once (it is what identified van
                // 302199 as "not our tow truck, skipped by design"); after that it is noise.
                if (Mod.Setting.VerboseLogging && m_NotTowTruckLogged.Add(truck))
                {
                    Mod.Log.Info($"[towdiag] truck={truck.Index} skipped: not the dedicated tow " +
                        $"truck (no CarTractorData) and TowTruckPrefab is on");
                }
                return;
            }
            // Target must be a settled, not burning wreck.
            Entity wreck = EntityManager.GetComponentData<Target>(truck).m_Target;
            // A live accident wreck (InvolvedInAccident) OR a relic we re-armed for recovery
            // (RelicRecovery - it has no InvolvedInAccident, the tag is its "valid target" signal).
            if (wreck == Entity.Null || !EntityManager.Exists(wreck) ||
                (!EntityManager.HasComponent<Game.Events.InvolvedInAccident>(wreck) &&
                 !EntityManager.HasComponent<RelicRecovery>(wreck)) ||
                !EntityManager.HasComponent<Car>(wreck))
            {
                // Deliberately loud: RecoveryAssist sends a truck on "Damaged OR
                // InvolvedInAccident", this gate wants "InvolvedInAccident OR RelicRecovery". A
                // wreck that lost InvolvedInAccident without getting the relic tag falls exactly
                // between the two - the truck drives there and then silently refuses forever.
                if (diag && wreck != Entity.Null && EntityManager.Exists(wreck) &&
                    m_InvalidTargetLogged.Add(wreck))
                {
                    Mod.Log.Info($"[towdiag] truck={truck.Index} wreck={wreck.Index} skipped: not a valid " +
                        $"target - invAcc={(EntityManager.HasComponent<Game.Events.InvolvedInAccident>(wreck) ? 1 : 0)} " +
                        $"relicRec={(EntityManager.HasComponent<RelicRecovery>(wreck) ? 1 : 0)} " +
                        $"car={(EntityManager.HasComponent<Car>(wreck) ? 1 : 0)}");
                }
                return;
            }
            // If a tow truck has been sent here, it tows. Damage state does not decide.
            //
            // The gate briefly required Destroyed, on the theory that a merely Damaged car gets
            // repaired in place by a van and drives on (which IS vanilla's model - see
            // MaintenanceVehicleAISystem, it works Damaged.m_Damage down to zero and
            // AccidentVehicleSystem then restarts the car). The field says otherwise: in one
            // session that gate produced 372 refusals against a single successful hookup, with
            // five tow trucks parked in front of three wrecks repeating the same refusal every
            // 2.5 s, giving up after 90 s and driving home - while the cars they had been sent to
            // just stood there. Nobody was repairing them. And "Damaged" says nothing about
            // whether a car is driveable: Sebastian's screenshot of one flipped onto its nose is
            // still only Damaged, and it is never going to drive again.
            //
            // So the decision belongs to the DISPATCH, not to this gate: something judged this
            // wreck worth a recovery vehicle, the recovery vehicle is here, and it does the job.
            // Only Destroyed remains special elsewhere - it is what vanilla never repairs at all.
            if (!EntityManager.HasComponent<Damaged>(wreck) && !EntityManager.HasComponent<Destroyed>(wreck))
            {
                if (diag)
                {
                    Mod.Log.Info($"[towdiag] truck={truck.Index} wreck={wreck.Index} skipped: " +
                        $"undamaged - nothing to recover here");
                }
                return;
            }
            if (!EntityManager.HasComponent<Transform>(wreck) ||
                !EntityManager.HasComponent<Stopped>(wreck) ||
                EntityManager.HasComponent<Game.Events.OnFire>(wreck) ||
                EntityManager.HasComponent<Controller>(wreck) && EntityManager.GetComponentData<Controller>(wreck).m_Controller != wreck)
            {
                if (diag)
                {
                    Mod.Log.Info($"[towdiag] truck={truck.Index} wreck={wreck.Index} blocked: " +
                        $"stopped={(EntityManager.HasComponent<Stopped>(wreck) ? 1 : 0)} " +
                        $"fire={(EntityManager.HasComponent<Game.Events.OnFire>(wreck) ? 1 : 0)} " +
                        $"hasCtrl={(EntityManager.HasComponent<Controller>(wreck) ? 1 : 0)}");
                }
                return;
            }
            // Close and slow = it pulled up next to the wreck.
            float3 truckPos = EntityManager.GetComponentData<Transform>(truck).m_Position;
            float3 wreckPos = EntityManager.GetComponentData<Transform>(wreck).m_Position;
            float dist = math.distance(truckPos.xz, wreckPos.xz);
            float speed = EntityManager.HasComponent<Moving>(truck)
                ? math.length(EntityManager.GetComponentData<Moving>(truck).m_Velocity) : 0f;
            if (dist > kHookupRange || speed > kHookupMaxSpeed)
            {
                if (diag && dist < 100f)
                {
                    Mod.Log.Info($"[towdiag] truck={truck.Index} wreck={wreck.Index} approaching: dist={dist:F0} speed={speed:F1}");
                }
                return;
            }

            // --- Couple the wreck ---
            // Strip everything that made it a managed accident wreck / an AI-driven car.
            if (EntityManager.HasComponent<Game.Simulation.MaintenanceConsumer>(wreck))
            {
                Entity request = EntityManager.GetComponentData<Game.Simulation.MaintenanceConsumer>(wreck).m_Request;
                if (request != Entity.Null && EntityManager.Exists(request))
                {
                    EntityManager.AddComponent<Deleted>(request);
                }
                EntityManager.RemoveComponent<Game.Simulation.MaintenanceConsumer>(wreck);
            }
            EntityManager.RemoveComponent<Game.Events.InvolvedInAccident>(wreck);
            // The wreck ALWAYS keeps its entire, hours-proven-stable "settled wreck" archetype
            // (Stopped, no Moving, no TransformFrame) - two earlier attempts to make the wreck
            // itself a moving vehicle/trailer hard-crashed in Burst jobs. It is only ever
            // teleported (the exact ClearWrecksAside operation). What differs is WHAT it follows:
            // PURE DRAWBAR (Sebastian 2026-07-12): the wreck trails a short rope-length directly
            // behind the truck, on the road. Flatbed mode is RETIRED - the real-trailer flatbed
            // rendered/cornered fine but the wreck riding its deck glitched under the world, and the
            // wreck-as-trailer rework (branch wreck-as-trailer-B) keeps tripping CarTrailerMoveSystem
            // (unguarded Burst job 8dd87328). Drawbar keeps the wreck a static Stopped object we
            // teleport ourselves every frame (the exact ClearWrecksAside operation) - it renders and
            // moves, no vanilla trailer machinery, no Burst crash. The retired flatbed path is
            // parked in TowFlatbed.cs.
            EntityManager.AddComponentData(wreck, new Controller(truck));
            // Persistent ownership tag: survives our stripping AND save/load, so this wreck is
            // always recognisable as ours even after a load empties m_OurTows (see TowMarker).
            if (!EntityManager.HasComponent<TowMarker>(wreck))
            {
                EntityManager.AddComponent<TowMarker>(wreck);
            }
            // A crashed RIG keeps its LayoutElement buffer ([0]=itself, [1..]=its trailers),
            // but the drawbar only ever hauls the tractor - the trailer used to stay behind
            // forever once its tractor was towed away and deleted at the depot. For now the
            // trailer of anything we tow simply despawns at the hookup. The buffer is then
            // trimmed to the wreck itself: a Deleted entity left inside a LayoutElement
            // buffer is exactly the dangling-reference crash from the wreck-as-trailer
            // branch (CarTrailerMoveSystem reads members' prefabs unguarded).
            if (EntityManager.HasBuffer<LayoutElement>(wreck))
            {
                DynamicBuffer<LayoutElement> wreckLayout = EntityManager.GetBuffer<LayoutElement>(wreck);
                for (int i = 0; i < wreckLayout.Length; i++)
                {
                    Entity member = wreckLayout[i].m_Vehicle;
                    if (member == wreck || member == Entity.Null || !EntityManager.Exists(member) ||
                        EntityManager.HasComponent<Deleted>(member))
                    {
                        continue;
                    }
                    if (EntityManager.HasComponent<Game.Simulation.MaintenanceConsumer>(member))
                    {
                        Entity memberRequest = EntityManager.GetComponentData<Game.Simulation.MaintenanceConsumer>(member).m_Request;
                        if (memberRequest != Entity.Null && EntityManager.Exists(memberRequest))
                        {
                            EntityManager.AddComponent<Deleted>(memberRequest);
                        }
                    }
                    m_Ctx.SafeDelete(member);
                    if (Mod.Setting.VerboseLogging)
                    {
                        Mod.Log.Info($"[tow] despawned trailer={member.Index} of wreck={wreck.Index} at hookup");
                    }
                }
                wreckLayout.Clear();
                wreckLayout.Add(new LayoutElement(wreck));
            }
            m_TrucksWithLoad.Add(truck);

            // --- Send the truck home with its load ---
            Game.Vehicles.MaintenanceVehicle maintenance =
                EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(truck);
            if (maintenance.m_TargetRequest != Entity.Null && EntityManager.Exists(maintenance.m_TargetRequest))
            {
                EntityManager.AddComponent<Deleted>(maintenance.m_TargetRequest);
            }
            maintenance.m_TargetRequest = Entity.Null;
            maintenance.m_RequestCount = 0;
            maintenance.m_State &= ~(MaintenanceVehicleFlags.TryWork | MaintenanceVehicleFlags.Working |
                MaintenanceVehicleFlags.ClearingDebris | MaintenanceVehicleFlags.TransformTarget |
                MaintenanceVehicleFlags.EdgeTarget);
            maintenance.m_State |= MaintenanceVehicleFlags.Returning |
                MaintenanceVehicleFlags.Full | MaintenanceVehicleFlags.EstimatedFull;
            // Mark the truck FULL and keep m_Maintained at capacity so it actually heads home.
            // MaintenanceVehicleAISystem.Tick recomputes Full/EstimatedFull from m_Maintained vs
            // capacity EVERY tick: with m_Maintained < capacity it clears Full again next frame,
            // its dispatch guard reopens and CheckServiceDispatches+SelectNextDispatch re-grab a
            // fresh wreck (clearing Returning) - the "truck never comes home" bug. Held Full, that
            // whole branch is skipped and the dispatch buffer stays cleared, so it drives to the
            // depot and despawns there. (Capacity is efficiency-scaled at runtime, always <= the
            // raw prefab value, so the raw capacity keeps Full asserted.)
            maintenance.m_Maintained = math.max(maintenance.m_Maintained,
                EntityManager.GetComponentData<MaintenanceVehicleData>(prefab).m_MaintenanceCapacity);
            EntityManager.SetComponentData(truck, maintenance);
            if (EntityManager.HasBuffer<ServiceDispatch>(truck))
            {
                EntityManager.GetBuffer<ServiceDispatch>(truck).Clear();
            }
            // A new job starts with a clean movement history. The wedge counter is keyed by TRUCK
            // and is now written from two places - the follow pass while hauling, and the empty
            // sweep while a truck is still on its way to a wreck. Without this reset a truck that
            // sat in traffic on the approach carries those strikes into the haul, so its first
            // stop as a loaded vehicle can trip a threshold it earned before it even had a load.
            m_Ctx.StuckRecovery.Forget(truck);
            // Aim at the depot and force a fresh path; clear any reached-path-end state so
            // the AI's "Returning + path end => despawn" branch cannot fire at the wreck.
            Entity depot = EntityManager.GetComponentData<Owner>(truck).m_Owner;
            EntityManager.SetComponentData(truck, new Target(depot));
            if (EntityManager.HasComponent<CarCurrentLane>(truck))
            {
                CarCurrentLane truckLane = EntityManager.GetComponentData<CarCurrentLane>(truck);
                truckLane.m_LaneFlags &= ~(CarLaneFlags.EndOfPath | CarLaneFlags.EndReached);
                EntityManager.SetComponentData(truck, truckLane);
            }
            if (EntityManager.HasComponent<PathOwner>(truck))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(truck);
                pathOwner.m_State &= ~(PathFlags.Failed | PathFlags.Stuck);
                pathOwner.m_State |= PathFlags.Obsolete;
                EntityManager.SetComponentData(truck, pathOwner);
            }
            if (EntityManager.HasComponent<Stopped>(truck))
            {
                Transform truckTransform = EntityManager.GetComponentData<Transform>(truck);
                EntityManager.RemoveComponent<Stopped>(truck);
                EntityManager.AddComponentData(truck, new Moving());
                DynamicBuffer<TransformFrame> truckFrames = EntityManager.AddBuffer<TransformFrame>(truck);
                for (int i = 0; i < 4; i++)
                {
                    truckFrames.Add(new TransformFrame(truckTransform));
                }
                EntityManager.AddComponentData(truck, new Game.Rendering.InterpolatedTransform(truckTransform));
                EntityManager.AddComponentData(truck, default(Game.Rendering.Swaying));
                EntityManager.AddComponent<Updated>(truck);
            }

            if (Mod.Setting.VerboseLogging)
            {
                Mod.Log.Info($"[tow] hooked wreck={wreck.Index} to truck={truck.Index} " +
                    $"(drawbar), returning to depot={depot.Index}");
            }
        }
    }
}

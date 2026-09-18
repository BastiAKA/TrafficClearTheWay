using System.Collections.Generic;
using Colossal.Collections;
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
using Unity.Jobs;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Dragging a coupled wreck along behind its truck, and delivering it.
    ///
    /// A drawbar, not a trailer: the wreck is a static object teleported to a rope-point behind
    /// the truck each frame. Two details are load-bearing:
    ///  - The anchor is the truck VISUAL position, not its simulation Transform. Vehicles are
    ///    DRAWN 32-48 sim frames in the past, while a teleported static object is drawn exactly
    ///    where it is put - so anchoring on the sim position made the wreck appear to sit on, or
    ///    ahead of, the truck at speed.
    ///  - The wreck is anchored directly BEHIND the truck rather than at a fixed distance from
    ///    it, because a rope model alone fixes distance but not direction, and coupling from
    ///    beside left the wreck beside or ahead of its tractor.
    /// </summary>
    internal sealed class TowFollow
    {
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

        public TowFollow(TowingContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Teleports a towed wreck each frame so it stays a static Stopped object (the
        /// same per-frame Transform+Updated write ClearWrecksAside uses); removes it once its
        /// carrier has parked/despawned. The carrier is always the recovery truck itself
        /// (Plan-C drawbar follow); the retired deck-ride variant lives in TowFlatbed.cs.</summary>
        public void FollowOrFinish(Entity wreck, uint frame, Setting setting)
        {
            Entity carrier = EntityManager.GetComponentData<Controller>(wreck).m_Controller;
            if (carrier == wreck)
            {
                return; // not ours
            }
            // Establish it is OUR tow BEFORE deciding anything - especially before deleting.
            // A Controller pointing at a live non-recovery vehicle means this is somebody
            // else's rig; hands off. (The delete branch below used to run first, so a
            // crashed civilian trailer whose car had parked was destroyed by us.) Only when
            // the carrier is really gone do we fall through - m_OurTows then says whether
            // the wreck was ever on OUR hook.
            bool carrierAlive = carrier != Entity.Null && EntityManager.Exists(carrier) &&
                !EntityManager.HasComponent<Deleted>(carrier) &&
                !EntityManager.HasComponent<ParkedCar>(carrier) &&
                EntityManager.HasComponent<Transform>(carrier);
            if (carrierAlive && !EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(carrier))
            {
                return; // controller is not a recovery truck: not our tow
            }
            if (!carrierAlive)
            {
                // Ours if the in-memory set knows it OR it carries our persistent tag. The tag is
                // what makes this survive a save/load: m_OurTows is empty after a load, so without
                // it a towed wreck whose truck despawned across the load was never cleaned up and
                // stood on the road forever (the leftover-relic bug).
                bool wasOurs = m_OurTows.Remove(wreck);
                if (!wasOurs && !EntityManager.HasComponent<TowMarker>(wreck))
                {
                    return; // never on our hook - leave it alone
                }
                m_Ctx.SafeDelete(wreck);
                m_TowTravelDir.Remove(carrier);
                m_LastTruckPos.Remove(carrier);
                if (setting.VerboseLogging)
                {
                    Mod.Log.Info($"[tow] wreck={wreck.Index} delivered - removed");
                }
                return;
            }
            m_OurTows.Add(wreck);
            // Re-tag if missing: covers tows that were coupled before the tag existed and re-asserts
            // ownership after a load, so the persistent tag always tracks what we are hauling.
            if (!EntityManager.HasComponent<TowMarker>(wreck))
            {
                EntityManager.AddComponent<TowMarker>(wreck);
            }

            // Re-normalise to the static towed state. After a save/load (or a secondary impact)
            // the wreck can come back Moving and/or with InvolvedInAccident re-added; left that way
            // the physics/accident systems fight our teleport and the accident logic may re-grab
            // it. Strip both and re-assert Stopped so it stays the inert object we drag along. This
            // is what lets a tow survive loading: the broadened m_TowedQuery still finds it, and
            // this pass (which also calls KeepTruckReturning below) keeps the truck on its load.
            if (EntityManager.HasComponent<Game.Events.InvolvedInAccident>(wreck))
            {
                EntityManager.RemoveComponent<Game.Events.InvolvedInAccident>(wreck);
            }
            if (EntityManager.HasComponent<Moving>(wreck))
            {
                EntityManager.RemoveComponent<Moving>(wreck);
            }
            if (!EntityManager.HasComponent<Stopped>(wreck))
            {
                EntityManager.AddComponent<Stopped>(wreck);
            }
            Entity truck = carrier; // already confirmed to be a live recovery truck above
            // Let go of a tow whose truck has been routeless too long. The path shield keeps the
            // truck alive, but a vehicle with no route drives dead straight - and it drags the
            // teleported wreck with it, which is how tow trucks and their loads ended up scattered
            // across parks and rooftops all over the map. Shielding therefore has to end somewhere:
            // hand the wreck back to the recovery pipeline (RelicRecovery re-requests it, and the
            // relic watchdog and orphan sweep both still cover it) and stop asserting ownership of
            // the truck, so its own AI decides what happens to it.
            bool pathGaveUp = m_Ctx.PathFailingSince.TryGetValue(truck, out uint failingSince) &&
                frame - failingSince > kTowPathGiveUpFrames;
            if (pathGaveUp)
            {
                ReleaseTow(wreck, truck, frame, setting, $"no route home for {kTowPathGiveUpFrames}f");
                m_Ctx.SafeDelete(truck);
                return;
            }
            // ...and the case a failed PATH cannot describe: a truck that drove itself somewhere
            // with no way out (a car park interior) may carry no Failed flag at all, so only its
            // standing still gives it away.
            //
            // Standing still is NOT enough to act on, though - a truck queueing in a jam looks
            // exactly the same. So ask the network: is there a driving lane under its wheels? If
            // yes it is queueing and nothing here touches it (that case belongs to the deadlock
            // machinery), and it is simply counted; if no - or if it has been counted through
            // kTowWedgeStrikes checks without ever moving - the wreck goes back to the recovery
            // pipeline and the truck is deleted.
            //
            // It used to be REPOSITIONED here instead. That wrote the vehicle's lane fields by
            // hand, which bypasses the game's lane registry and hard-crashes a Burst job (see
            // TowStuckRecovery). Losing a truck is the cheaper failure by a wide margin.
            Transform stuckCheck = EntityManager.GetComponentData<Transform>(truck);
            if (m_Ctx.StuckRecovery.IsWedged(truck, stuckCheck.m_Position, frame))
            {
                bool onRoad = false;
                Game.Net.SearchSystem netSearch = m_Ctx.NetSearch;
                if (netSearch != null)
                {
                    NativeQuadTree<Entity, QuadTreeBoundsXZ> netTree =
                        netSearch.GetNetSearchTree(readOnly: true, out JobHandle netDeps);
                    netDeps.Complete();
                    onRoad = m_Ctx.StuckRecovery.StandsOnRoad(stuckCheck.m_Position, netTree);
                    netSearch.AddNetSearchTreeReader(default);
                }
                // OFF the network is the only thing that ends a tow here now. It is the case this
                // machinery was built for: a truck that drove itself into a car park interior,
                // where no route exists and nothing can reach it.
                //
                // Standing ON a road used to end it too, after kTowWedgeStrikes - and that was
                // wrong, measurably. A loaded truck on a road is not wedged in the geometry, it is
                // queueing in traffic, and in a busy city it reaches 20 strikes just by waiting.
                // Session 2026-08-09: five trucks deleted that way in twenty minutes, each time
                // the wreck was re-armed, a fresh truck was sent, hooked, stood in the same jam and
                // was deleted in turn - wreck 2416869 went through 101555 and then 502287, wreck
                // 2120200 through 73589 and then 967945. The mechanism did not resolve a single
                // blockage; it just fed trucks into one. So the strikes are still counted and
                // logged (they are a useful signal that something is wrong there), but they no
                // longer cost the player a vehicle.
                if (!onRoad)
                {
                    ReleaseTow(wreck, truck, frame, setting, "it is off the road network");
                    m_Ctx.SafeDelete(truck);
                    return;
                }
                m_Ctx.StuckRecovery.NoteOnRoadStrike(truck, frame, setting);
            }
            m_TrucksWithLoad.Add(truck);
            LogHaulState(truck, wreck, frame, setting);
            // A loaded truck must ALWAYS deliver first. The game's MaintenanceVehicleAISystem can
            // re-open dispatch and send a still-loaded truck to a fresh accident (Sebastian saw one
            // "drop its car and drive to the next accident"). Setting Returning+Full once at hookup
            // isn't enough - re-assert it every frame until the wreck is delivered.
            KeepTruckReturning(truck);

            Transform truckTransform = EntityManager.GetComponentData<Transform>(truck);
            Transform wreckTransform = EntityManager.GetComponentData<Transform>(wreck);

            // Anchor the rope to the truck's RENDERED position, not its sim Transform. A moving
            // vehicle is DRAWN interpolated between two HISTORICAL TransformFrame slots -
            // ObjectInterpolateSystem.CalculateUpdateFrames: num = frame - updateFrameIndex - 32,
            // i.e. the visual model trails the sim Transform by 32-48 sim frames (several meters
            // at speed). The wreck has no TransformFrame ring, so it is drawn AT its sim Transform
            // with zero lag (UpdateStaticAnimations). Anchored to the truck's sim position the
            // wreck therefore LOOKED on/in front of the truck's nose while driving, even though
            // the sim-side math was provably behind (towdbg newSide<0). Recreating the renderer's
            // own interpolation from the truck's ring keeps the wreck glued behind the VISIBLE
            // truck at any speed, and works even off-camera (pure sim data, no render state).
            Transform truckVisual = truckTransform;
            if (EntityManager.HasBuffer<TransformFrame>(truck) &&
                EntityManager.HasComponent<Game.Simulation.UpdateFrame>(truck))
            {
                DynamicBuffer<TransformFrame> ring = EntityManager.GetBuffer<TransformFrame>(truck, isReadOnly: true);
                if (ring.Length == 4)
                {
                    uint batch = EntityManager.GetSharedComponent<Game.Simulation.UpdateFrame>(truck).m_Index;
                    Game.Rendering.ObjectInterpolateSystem.CalculateUpdateFrames(
                        frame, m_SimulationSystem.frameTime, batch,
                        out uint frame1, out uint frame2, out float framePos);
                    Game.Rendering.InterpolatedTransform visual =
                        Game.Rendering.ObjectInterpolateSystem.CalculateTransform(
                            ring[(int)frame1], ring[(int)frame2], framePos);
                    truckVisual = new Transform(visual.m_Position, visual.m_Rotation);
                }
            }

            // "Behind" from the truck's ACTUAL visual movement (anchor position this frame minus
            // last), NOT Moving.m_Velocity (its direction here isn't plain world-forward and placed
            // the wreck in FRONT). Position delta is ground truth: the truck came FROM the
            // -travelDir side, so that is where the wreck belongs. The tow runs inside the accident
            // JAM (stop-and-go), so we latch the direction whenever the anchor moves >0.1 m and
            // reuse it while stopped. Until it has ever moved, DON'T move the wreck (never place
            // it wrong).
            float3 travelDir;
            bool haveLast = m_LastTruckPos.TryGetValue(truck, out float3 lastPos);
            m_LastTruckPos[truck] = truckVisual.m_Position;
            float3 move = truckVisual.m_Position - lastPos;
            move.y = 0f;
            if (haveLast && math.lengthsq(move) > 0.01f) // moved > 0.1 m this tick
            {
                travelDir = math.normalize(move);
                m_TowTravelDir[truck] = travelDir;
            }
            else if (!m_TowTravelDir.TryGetValue(truck, out travelDir))
            {
                return; // never moved yet - leave the wreck where it coupled, correct once it drives
            }
            float3 ropePoint = truckVisual.m_Position - travelDir * (kHitchDistance + kTrailDistance);
            // Follow the TRUCK's height (its road plane), not the wreck's crash-site Y - the latter
            // made it clip under the road on slopes ("unter die Welt", fixed 2026-07-12).
            ropePoint.y = truckVisual.m_Position.y;
            float3 newPos = math.lerp(wreckTransform.m_Position, ropePoint, kFollowRate);
            newPos.y = truckVisual.m_Position.y; // snap to road height (no lerp lag on slopes)
            float3 delta = newPos - wreckTransform.m_Position;
            if (math.lengthsq(delta) < 0.0004f)
            {
                return; // resting behind the truck - no churn
            }

            // Face toward the visible truck (front of the towed car points at its tow truck), from
            // actual positions so it is right regardless of any direction convention.
            float3 faceDir = math.normalizesafe(truckVisual.m_Position - newPos, travelDir);
            quaternion targetRot = quaternion.LookRotationSafe(faceDir, math.up());
            quaternion newRot = math.slerp(wreckTransform.m_Rotation, targetRot, kFollowRate);

            wreckTransform.m_Position = newPos;
            wreckTransform.m_Rotation = newRot;
            EntityManager.SetComponentData(wreck, wreckTransform);
            // NO InterpolatedTransform sync needed: for an entity without a TransformFrame ring
            // the renderer itself sets InterpolatedTransform = Transform every render frame
            // (ObjectInterpolateSystem.UpdateStaticAnimations), so the wreck is always drawn
            // exactly at the Transform we just wrote.
            EntityManager.AddComponent<Updated>(wreck);
            EntityManager.AddComponent<BatchesUpdated>(wreck);

            if (setting.VerboseLogging && frame % 60u == 0u)
            {
                float3 tp = truckTransform.m_Position;
                float3 vp = truckVisual.m_Position;
                Mod.Log.Info($"[towdbg] truck={truck.Index} sim=({tp.x:F0},{tp.z:F0}) vis=({vp.x:F0},{vp.z:F0}) " +
                    $"lag={math.distance(tp.xz, vp.xz):F1} wreck={wreck.Index} " +
                    $"visSide={math.dot(newPos - vp, travelDir):F1} visDist={math.distance(newPos.xz, vp.xz):F1} " +
                    $"pos=({newPos.x:F0},{newPos.z:F0})");
            }
        }

        /// <summary>
        /// Re-assert every frame that a LOADED tow truck is going home and cannot be re-dispatched.
        /// MaintenanceVehicleAISystem otherwise re-opens dispatch and hands a still-loaded truck a
        /// fresh wreck target; it then abandons its load mid-route. We clear any new request, force
        /// Returning+Full (+ m_Maintained at capacity so Full is not recomputed away), empty the
        /// dispatch buffer, and steer the Target back to the depot whenever the game changed it.
        /// </summary>
        public void KeepTruckReturning(Entity truck)
        {
            if (!EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(truck))
            {
                return;
            }
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(truck).m_Prefab;
            Game.Vehicles.MaintenanceVehicle m = EntityManager.GetComponentData<Game.Vehicles.MaintenanceVehicle>(truck);
            if (m.m_TargetRequest != Entity.Null && EntityManager.Exists(m.m_TargetRequest))
            {
                EntityManager.AddComponent<Deleted>(m.m_TargetRequest);
            }
            m.m_TargetRequest = Entity.Null;
            m.m_RequestCount = 0;
            m.m_State &= ~(MaintenanceVehicleFlags.TryWork | MaintenanceVehicleFlags.Working |
                MaintenanceVehicleFlags.ClearingDebris | MaintenanceVehicleFlags.TransformTarget |
                MaintenanceVehicleFlags.EdgeTarget);
            m.m_State |= MaintenanceVehicleFlags.Returning | MaintenanceVehicleFlags.Full |
                MaintenanceVehicleFlags.EstimatedFull;
            if (EntityManager.HasComponent<MaintenanceVehicleData>(prefab))
            {
                m.m_Maintained = math.max(m.m_Maintained,
                    EntityManager.GetComponentData<MaintenanceVehicleData>(prefab).m_MaintenanceCapacity);
            }
            EntityManager.SetComponentData(truck, m);
            // Shield its path, every tick, for as long as it carries a load.
            //
            // This method pins Returning permanently - which means HALF of vanilla's delete branch
            // (MaintenanceVehicleAISystem: PathfindFailed && (IsStuck || Returning) => Deleted) is
            // always satisfied for a towing truck. The coupling forces a fresh repath to the depot,
            // and Failed was cleared exactly once, at hookup. So a single failed pathfind after
            // that deleted the truck instantly - and the wreck with it, logged as "delivered" a
            // heartbeat after "hooked" (truck 302200: hooked 21:16:49.7, wreck gone 21:16:50.4).
            //
            // Clearing the flag alone is NOT enough though - it stops the deletion and leaves the
            // truck with no route, carrying straight on over the kerb. PathShield therefore also
            // asks for a new path, at once on the first failure and throttled after that.
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (PathShield.Shield(EntityManager, m_Ctx.PathRetry, truck, nowFrame))
            {
                if (!m_Ctx.PathFailingSince.ContainsKey(truck))
                {
                    m_Ctx.PathFailingSince[truck] = nowFrame;
                }
            }
            else
            {
                m_Ctx.PathFailingSince.Remove(truck); // has a route again
            }
            if (EntityManager.HasBuffer<ServiceDispatch>(truck))
            {
                EntityManager.GetBuffer<ServiceDispatch>(truck).Clear();
            }
            // Steer back to the depot only if the game re-targeted the truck elsewhere (avoid churn).
            if (EntityManager.HasComponent<Owner>(truck) && EntityManager.HasComponent<Target>(truck))
            {
                Entity depot = EntityManager.GetComponentData<Owner>(truck).m_Owner;
                if (depot != Entity.Null && EntityManager.GetComponentData<Target>(truck).m_Target != depot)
                {
                    EntityManager.SetComponentData(truck, new Target(depot));
                    if (EntityManager.HasComponent<PathOwner>(truck))
                    {
                        PathOwner po = EntityManager.GetComponentData<PathOwner>(truck);
                        po.m_State |= PathFlags.Obsolete; // re-path home
                        EntityManager.SetComponentData(truck, po);
                    }
                }
            }
        }
        /// <summary>
        /// Give up a tow without losing the wreck: drop our coupling and put it back into the
        /// recovery pipeline as a relic, so a different truck can come for it.
        /// </summary>
        /// <summary>
        /// One line per hauling truck that is not making progress: why is it standing?
        ///
        /// This was the blind spot behind the whole "they hook up and then never drive" hunt. The
        /// [assist] diagnostics stop the moment a truck goes Returning, so for the entire HAUL
        /// phase the log could say only "motionless" and "strandedFor is climbing" - never who was
        /// in the way, and never whether the mod itself was capping the truck's speed. Both
        /// answers are one component read away, and without them every such case cost an hour of
        /// guessing.
        ///
        /// ourCap is the important one: an emergency corridor holds every car around a responder,
        /// and a loaded tow truck used to be one of those cars - so the vehicle carrying the
        /// obstruction away could be pinned at zero by the very pass trying to clear the road.
        /// A non-empty ourCap here means look at HoldVehicles, not at traffic.
        /// </summary>
        private void LogHaulState(Entity truck, Entity wreck, uint frame, Setting setting)
        {
            if (!setting.VerboseLogging || frame % 120u != 0u)
            {
                return;
            }
            float speed = EntityManager.HasComponent<Moving>(truck)
                ? math.length(EntityManager.GetComponentData<Moving>(truck).m_Velocity) : 0f;
            if (speed > 0.5f)
            {
                return; // rolling along - nothing to explain
            }
            string blockerInfo = "none";
            if (EntityManager.HasComponent<Blocker>(truck))
            {
                Blocker b = EntityManager.GetComponentData<Blocker>(truck);
                if (b.m_Blocker != Entity.Null && EntityManager.Exists(b.m_Blocker))
                {
                    float bspeed = EntityManager.HasComponent<Moving>(b.m_Blocker)
                        ? math.length(EntityManager.GetComponentData<Moving>(b.m_Blocker).m_Velocity) : 0f;
                    bool bMaint = EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(b.m_Blocker);
                    bool bEmerg = EntityManager.HasComponent<Car>(b.m_Blocker) &&
                        (EntityManager.GetComponentData<Car>(b.m_Blocker).m_Flags & CarFlags.Emergency) != 0;
                    blockerInfo = $"{b.m_Blocker.Index} type={b.m_Type} spd={bspeed:F1} " +
                        $"emerg={(bEmerg ? 1 : 0)} maint={(bMaint ? 1 : 0)}";
                }
                else
                {
                    blockerInfo = $"{b.m_Blocker.Index}(gone) type={b.m_Type}";
                }
            }
            string ourCap = "none";
            if (m_Ctx.WayClearance != null &&
                m_Ctx.WayClearance.SpeedOverrides.TryGetValue(truck, out SpeedOverride cap))
            {
                ourCap = cap.ToString();
            }
            string pathState = EntityManager.HasComponent<Game.Pathfind.PathOwner>(truck)
                ? EntityManager.GetComponentData<Game.Pathfind.PathOwner>(truck).m_State.ToString()
                : "n/a";
            Entity target = EntityManager.HasComponent<Target>(truck)
                ? EntityManager.GetComponentData<Target>(truck).m_Target : Entity.Null;
            Entity depot = EntityManager.HasComponent<Owner>(truck)
                ? EntityManager.GetComponentData<Owner>(truck).m_Owner : Entity.Null;
            Mod.Log.Info($"[towhaul] truck={truck.Index} wreck={wreck.Index} standing spd={speed:F1} " +
                $"target={target.Index} depot={depot.Index} targetIsDepot={(target == depot && depot != Entity.Null ? 1 : 0)} " +
                $"path={pathState} ourCap={ourCap} blockedBy=[{blockerInfo}]");
        }

        private void ReleaseTow(Entity wreck, Entity truck, uint frame, Setting setting, string reason)
        {
            if (EntityManager.HasComponent<Controller>(wreck))
            {
                EntityManager.RemoveComponent<Controller>(wreck);
            }
            if (EntityManager.HasComponent<TowMarker>(wreck))
            {
                EntityManager.RemoveComponent<TowMarker>(wreck);
            }
            if (!EntityManager.HasComponent<RelicRecovery>(wreck))
            {
                EntityManager.AddComponent<RelicRecovery>(wreck);
            }
            m_RelicArmedFrame[wreck] = frame;
            m_OurTows.Remove(wreck);
            m_TrucksWithLoad.Remove(truck);
            m_TowTravelDir.Remove(truck);
            m_LastTruckPos.Remove(truck);
            m_Ctx.PathFailingSince.Remove(truck);
            m_Ctx.PathRetry.Remove(truck);
            if (setting.VerboseLogging)
            {
                // The reason is passed in, because this method has TWO callers and used to print
                // the path-timeout text for both. Every release in the 2026-08-09 session said
                // "no route home for 600f" while the actual trigger was the wedge-strike counter -
                // and that sent the next investigation straight at the pathfinder, which was fine.
                Mod.Log.Info($"[tow] released wreck={wreck.Index} from truck={truck.Index} - " +
                    $"{reason}; re-armed for recovery");
            }
        }

    }
}

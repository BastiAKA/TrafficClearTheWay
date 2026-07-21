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
            m_TrucksWithLoad.Add(truck);
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
    }
}

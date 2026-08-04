using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using static ClearTheWay.Tuning;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes
{
    /// <summary>
    /// Everything the corridor does differently for a BIG RIG - a tractor with one or more
    /// trailers (LayoutElement buffer with more than one part).
    ///
    /// A rig is not a long car. Its trailer has no lateral lever of its own: it merely follows,
    /// and it OFF-TRACKS - swings outward - whenever the tractor is yanked across the lane. Pull
    /// a rig aside the way a car is pulled aside and the trailer sweeps into the very gap that
    /// was just opened, so the corridor gets NARROWER, not wider.
    ///
    /// Hence the three rules encoded here:
    ///  - Drift, never yank: a small offset, a gentle rate, and never the 1.7 m hard evade onto
    ///    the sidewalk (kArtic* in Tuning).
    ///  - Only while ROLLING. Dragging a near-stopped rig sideways produces the worst swing of
    ///    all, so a stopped or boxed-in rig is left straight in its lane - one lane wide and
    ///    predictable - and the responder takes the neighbour lane instead.
    ///  - A rig makes way by DRIVING FORWARD while it eases over (its trailer needs room ahead to
    ///    follow it), so it gets a gentle forward floor and is never held still, unlike a car.
    ///
    /// It is also never worth squeezing past: two wide bodies physically overlap even when the
    /// separation gate nominally passes, which is why the responder's escape from a rig is the
    /// overtake in EscalationSteering, not the squeeze.
    /// </summary>
    internal sealed class VehicleTrailerExt
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private VehicleControl vehicleControl => m_Ctx.VehicleControl;
        private Dictionary<Entity, uint> m_PushedCars => m_Ctx.PushedCars.Cars;
        private Dictionary<Entity, uint> m_BlockerShift => m_Ctx.PushedCars.BlockerShift;

        public VehicleTrailerExt(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Is this entity a towed part rather than a vehicle in its own right? Trailers follow
        /// their controller, so every pass skips them and works on the tractor instead.
        /// </summary>
        public static bool IsTrailer(EntityManager entityManager, Entity entity)
        {

            //if the controller is not the entity itself it must be a trailer, as it follows 
            return entityManager.HasComponent<Controller>(entity) &&
                entityManager.GetComponentData<Controller>(entity).m_Controller != entity;
        }

        /// <summary>The vehicle a part belongs to: a trailer resolves to its tractor, anything
        /// else to itself. What matters about a blocker (is it an emergency vehicle?) is a
        /// property of the whole rig, not of the box that happens to be in the way.</summary>
        public static Entity ResolveHead(EntityManager entityManager, Entity entity)
        {
            if (entityManager.HasComponent<Controller>(entity))
            {
                Entity controller = entityManager.GetComponentData<Controller>(entity).m_Controller;
                if (controller != Entity.Null && entityManager.Exists(controller))
                {
                    return controller;
                }
            }
            return entity;
        }

        /// <summary>Tractor + trailer(s): more than one part in the vehicle's layout.</summary>
        public static bool IsArticulated(EntityManager entityManager, Entity vehicle)
        {
            return entityManager.HasBuffer<LayoutElement>(vehicle) &&
                entityManager.GetBuffer<LayoutElement>(vehicle, isReadOnly: true).Length > 1;
        }

        /// <summary>
        /// The rig's whole make-way behaviour on one corridor lane, in place of the normal
        /// pull-aside: drift gently toward the corridor side, keep rolling forward, and get off
        /// the responder's lane entirely if a neighbour lane will take it. A stopped rig is left
        /// alone (see the class summary). Registers the rig as pushed either way, so the
        /// responder counts it as part of a working corridor.
        /// </summary>
        public void MakeWay(Entity rig, ref CarCurrentLane rigLane, CorridorLane corridorLane,
            float corridorLaneWidth, float aheadDistance, uint frame)
        {
            float rigSpeed = EntityManager.HasComponent<Moving>(rig)
                ? math.length(EntityManager.GetComponentData<Moving>(rig).m_Velocity)
                : 0f;


            if (rigSpeed >= kArticRollMin)
            {
                bool rigChanged = false;
                // Ease the whole body toward the corridor side - pure lateral drift, no
                // heading change, so the trailer tracks in line and never swings.
                float rigMeters = math.min(kArticEdgeMeters, m_Ctx.PrefabGeometry.MaxLateralMeters(rig, articulated: true));
                float rigUnits = rigMeters / m_Ctx.PrefabGeometry.LateralSlack(rig, corridorLaneWidth);
                float rigTarget = corridorLane.m_PushDirection * rigUnits;
                float rigNewPos = math.lerp(rigLane.m_LanePosition, rigTarget, kPullRate * kArticRateScale);
                if (math.abs(rigNewPos - rigLane.m_LanePosition) > 0.0005f)
                {
                    rigLane.m_LanePosition = rigNewPos;
                    rigChanged = true;
                }
                // Careful forward make-way: a rig clears the way by DRIVING slowly
                // forward while it eases aside (its trailer needs room ahead to follow).
                // Grant a gentle forward speed floor with NO nav-target override, so the
                // heading stays straight and the trailer does not swing into the gap.
                // Only just ahead of the responder, on its own path lane.
                if (corridorLane.m_OnPath && aheadDistance > 0f && aheadDistance <= kArticMakeWayRange)
                {
                    vehicleControl.SetFloor(rig, kArticMakeWaySpeed, hasTarget: false, default, default);
                }
                // While it can still get away, start a real lane change OFF the
                // responder's lane (toward the push side, into a clear neighbour lane) so
                // the corridor lane itself opens. Rate-limited per rig against a repath
                // flood; only ever SETS m_ChangeLane (never pins/nulls it), so the rig
                // completes or reverts the change through the game's own machinery.
                if (Mod.Setting != null && Mod.Setting.OvertakeStuckTraffic &&
                    corridorLane.m_OnPath && aheadDistance > 0f &&
                    rigLane.m_ChangeLane == Entity.Null &&
                    (!m_BlockerShift.TryGetValue(rig, out uint lastShift) ||
                     frame - lastShift >= kBlockerShiftCooldown) &&
                    m_Ctx.Squeeze.TryShiftBlockerLane(rig, ref rigLane, corridorLane.m_PushDirection))
                {
                    m_BlockerShift[rig] = frame;
                    rigChanged = true;
                }
                if (rigChanged)
                {
                    EntityManager.SetComponentData(rig, rigLane);
                }
            }
            // Deliberately do NOT hold a rig still: freezing a truck at kHoldSpeed the
            // moment it has drifted a little boxes it in place, and the responder can
            // never squeeze past a wide body anyway - a truck clears the way by DRIVING
            // FORWARD while it drifts aside. Its stuck flag is already cleared by the
            // caller (kProtectStuckRange) so it will not despawn while it rolls on.
            m_PushedCars[rig] = frame;
        }
    }
}

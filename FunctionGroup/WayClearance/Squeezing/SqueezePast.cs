using System.Collections.Generic;
using Colossal.Mathematics;
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
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes;

namespace ClearTheWay.FunctionGroup.WayClearance.Squeezing
{
    /// <summary>
    /// Getting a responder past a single vehicle that will not move.
    ///
    /// Squeezing uses CarLaneFlags.IgnoreBlocker - the same mechanism the game itself uses to
    /// resolve deadlocks - but only once REAL lateral separation exists, measured from the actual
    /// world positions rather than from intended lane offsets a boxed-in car may not have reached.
    /// Note the flag makes the game clamp speed to at least 3 m/s toward the ignored blocker, so
    /// it must never be left set where the vehicle needs to stop.
    ///
    /// When the blocker is a wide rig that cannot be squeezed past at all, the alternative is to
    /// make IT change lane - rate-limited per rig, because re-issuing a change the game reverts
    /// floods the pathfinder, and a repath flood despawns unrelated vehicles.
    /// </summary>
    internal sealed class SqueezePast
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private M_LaneGeometry m_Geometry => m_Ctx.Geometry;
        private VehicleControl m_Control => m_Ctx.VehicleControl;
        private CorridorBuilder m_Corridor => m_Ctx.Corridor;
        private Dictionary<Entity, StuckState> m_StuckStates => m_Ctx.States.Stuck;
        private Dictionary<Entity, uint> m_ForcedChanges => m_Ctx.States.ForcedChanges;
        private Dictionary<Entity, uint> m_PushedCars => m_Ctx.PushedCars.Cars;
        private SimulationSystem m_SimulationSystem => m_Ctx.Simulation;
        private Dictionary<Entity, PushClaim> m_PushClaims => m_Ctx.PushedCars.PushClaims;
        private List<Entity> m_ReleaseBuffer => m_Ctx.PushedCars.ReleaseBuffer;
        private Dictionary<Entity, uint> m_BlockerShift => m_Ctx.PushedCars.BlockerShift;
        public SqueezePast(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Start a lane change for a BLOCKER rig ahead of a responder: move it toward the push
        /// side (away from the gap the responder uses) into a clear, same-direction neighbour
        /// lane, so the responder's own lane opens. Mirrors the safe half of
        /// TryOvertakeLaneChange: it only ever SETS m_ChangeLane to a validated candidate
        /// (exactly what the game's lane selection does) - it never pins FixedLane and never
        /// nulls a change, so a civilian rig is never left in a corrupt lane state; it completes
        /// or reverts the change through the normal machinery. Returns true when a change was set.
        /// </summary>
        public bool TryShiftBlockerLane(Entity blocker, ref CarCurrentLane blockerLane, float pushDir)
        {
            if (blockerLane.m_ChangeLane != Entity.Null ||
                (blockerLane.m_LaneFlags & (CarLaneFlags.FixedLane | CarLaneFlags.Roundabout | CarLaneFlags.ParkingSpace | CarLaneFlags.Area | CarLaneFlags.TransformTarget | CarLaneFlags.EndReached | CarLaneFlags.EndOfPath)) != 0)
            {
                return false;
            }
            if (!EntityManager.HasComponent<SlaveLane>(blockerLane.m_Lane) ||
                !EntityManager.HasComponent<Owner>(blockerLane.m_Lane) ||
                !EntityManager.HasComponent<Curve>(blockerLane.m_Lane))
            {
                return false;
            }
            Curve curve = EntityManager.GetComponentData<Curve>(blockerLane.m_Lane);
            bool inverted = blockerLane.m_CurvePosition.z < blockerLane.m_CurvePosition.x;
            if (curve.m_Length * math.abs(blockerLane.m_CurvePosition.z - blockerLane.m_CurvePosition.x) < kOvertakeMinRemaining)
            {
                return false; // too little lane left to finish a change
            }
            SlaveLane slaveLane = EntityManager.GetComponentData<SlaveLane>(blockerLane.m_Lane);
            Owner owner = EntityManager.GetComponentData<Owner>(blockerLane.m_Lane);
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner.m_Owner))
            {
                return false;
            }
            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner.m_Owner, isReadOnly: true);
            int maxIndex = math.min(slaveLane.m_MaxIndex, subLanes.Length - 1);
            int myIndex = -1;
            for (int i = slaveLane.m_MinIndex; i <= maxIndex; i++)
            {
                if (subLanes[i].m_SubLane == blockerLane.m_Lane) { myIndex = i; break; }
            }
            if (myIndex < 0)
            {
                return false;
            }
            bool laneInverted = (EntityManager.GetComponentData<Game.Net.CarLane>(blockerLane.m_Lane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            // Positive lane-position is right of travel, so pushDir>0 => move to the physical
            // right neighbour, pushDir<0 => the left one (matches leftIndex/rightIndex above).
            bool towardRight = pushDir > 0f;
            int candidateIndex = (towardRight != laneInverted) ? myIndex + 1 : myIndex - 1;
            if (candidateIndex < slaveLane.m_MinIndex || candidateIndex > maxIndex)
            {
                return false;
            }
            Game.Net.SubLane candidate = subLanes[candidateIndex];
            Entity candidateLane = candidate.m_SubLane;
            if ((candidate.m_PathMethods & PathMethod.Road) == 0 ||
                candidateLane == blockerLane.m_Lane ||
                !EntityManager.HasComponent<Game.Net.CarLane>(candidateLane) ||
                EntityManager.HasComponent<MasterLane>(candidateLane))
            {
                return false;
            }
            Game.Net.CarLaneFlags candidateFlags = EntityManager.GetComponentData<Game.Net.CarLane>(candidateLane).m_Flags;
            if ((candidateFlags & Game.Net.CarLaneFlags.Forbidden) != 0)
            {
                return false;
            }
            // Same-direction lanes only - never shove the rig onto the oncoming carriageway.
            if (((candidateFlags & Game.Net.CarLaneFlags.Invert) != 0) != laneInverted)
            {
                return false;
            }
            if (!m_Geometry.IsLaneClearAround(candidateLane, blockerLane.m_CurvePosition.x, inverted))
            {
                return false;
            }
            blockerLane.m_ChangeLane = candidateLane;
            blockerLane.m_ChangeProgress = 0f;
            blockerLane.m_LaneFlags &= ~(CarLaneFlags.TurnLeft | CarLaneFlags.TurnRight);
            blockerLane.m_LaneFlags |= (candidateIndex < myIndex != laneInverted) ? CarLaneFlags.TurnLeft : CarLaneFlags.TurnRight;
            return true;
        }

        public bool TrySqueezePastBlocker(Entity vehicle, ref CarCurrentLane currentLane, uint frame)
        {
            Blocker blocker = EntityManager.GetComponentData<Blocker>(vehicle);
            Entity blockingEntity = blocker.m_Blocker;
            bool vehicleSlow = math.lengthsq(EntityManager.GetComponentData<Moving>(vehicle).m_Velocity) <= kMaxSqueezeSpeed * kMaxSqueezeSpeed;

            // Track how long we have been slow behind a blocker. The timer measures a
            // CONTINUOUS slow-and-blocked spell, NOT time behind one exact entity: in a real
            // jam Blocker.m_Blocker flickers between the cars/trailers/crossing vehicles ahead
            // every few frames, and keying the timer on entity identity reset it on every
            // flicker - so kStuckFrames/kDesperateFrames/kEvadeAfterFrames were almost never
            // reached and the squeeze / desperate / overtake escalations never fired (the
            // low-speed "just sits there behind a truck" bug). Reset the spell only when the
            // vehicle is actually rolling again or has nothing ahead; while it stays slow and
            // blocked the timer keeps running even as the reported blocker changes. m_Blocker
            // still tracks the CURRENT blocker for the separation/aside checks below.
            m_StuckStates.TryGetValue(vehicle, out StuckState stuck);
            bool blockedNow = vehicleSlow && blockingEntity != Entity.Null;
            if (!blockedNow)
            {
                stuck.m_Blocker = Entity.Null;
                stuck.m_BlockerSinceFrame = frame;
            }
            else
            {
                if (stuck.m_Blocker == Entity.Null)
                {
                    // Just became slow-and-blocked - start the spell.
                    stuck.m_BlockerSinceFrame = frame;
                }
                stuck.m_Blocker = blockingEntity;
            }
            stuck.m_LastSeenFrame = frame;
            m_StuckStates[vehicle] = stuck;

            if ((currentLane.m_LaneFlags & CarLaneFlags.IgnoreBlocker) != 0)
            {
                return false;
            }
            // The blocker may be anything vehicle-like in the way: a car, a truck's
            // TRAILER or a parked/working vehicle (those have no CarCurrentLane, which
            // previously made them impossible to ever pass). Never pedestrians.
            // Crossing counts too: a stationary vehicle wedged across the path in a
            // gridlocked intersection is passable exactly like a queued one - the
            // separation gate keeps it safe.
            if (!vehicleSlow ||
                blockingEntity == Entity.Null ||
                (blocker.m_Type != BlockerType.Continuing && blocker.m_Type != BlockerType.Temporary && blocker.m_Type != BlockerType.Crossing) ||
                !EntityManager.Exists(blockingEntity) ||
                !EntityManager.HasComponent<Transform>(blockingEntity) ||
                EntityManager.HasComponent<Game.Creatures.Creature>(blockingEntity))
            {
                return false;
            }

            // Never drive through another emergency vehicle (check a trailer's controller
            // too) or anything that is still moving too fast.
            Entity blockerHead = VehicleTrailerExt.ResolveHead(EntityManager, blockingEntity);
            if (EntityManager.HasComponent<Car>(blockerHead) &&
                (EntityManager.GetComponentData<Car>(blockerHead).m_Flags & CarFlags.Emergency) != 0)
            {
                return false;
            }
            if (EntityManager.HasComponent<Moving>(blockingEntity) &&
                math.lengthsq(EntityManager.GetComponentData<Moving>(blockingEntity).m_Velocity) > kMaxBlockerSpeed * kMaxBlockerSpeed)
            {
                return false;
            }

            // Known-aside state only exists for cars with lane data; trailers and parked
            // vehicles rely on the measured separation alone.
            bool aside = false;
            if (EntityManager.HasComponent<CarCurrentLane>(blockingEntity))
            {
                CarCurrentLane blockerLane = EntityManager.GetComponentData<CarCurrentLane>(blockingEntity);
                aside = math.abs(blockerLane.m_LanePosition) >= kMinAsidePosition ||
                        blockerLane.m_ChangeLane != Entity.Null;
            }
            // Stuck behind the same slow blocker for a while => start squeezing even if it
            // could not pull aside (it is usually boxed in itself). Once squeezing started,
            // keep the gate open for a while so the vehicle works through the whole queue
            // instead of waiting again at every single car.
            bool latched = frame < stuck.m_SqueezeUntilFrame;
            bool stuckLong = frame - stuck.m_BlockerSinceFrame >= kStuckFrames && stuck.m_Blocker == blockingEntity;
            if (!aside && !stuckLong && !latched)
            {
                return false;
            }

            // Only actually pass once there is real lateral distance between the two
            // vehicles - otherwise the emergency vehicle would visibly drive straight
            // through the blocker. Until then the creep-out and the evade keep building
            // that distance; if the road is truly too tight, the vehicle waits, like a
            // real one would.
            if (m_Geometry.GetLateralSeparation(vehicle, currentLane, blockingEntity) < kMinSqueezeSeparation)
            {
                return false;
            }

            stuck.m_SqueezeUntilFrame = frame + kSqueezeLatchFrames;
            m_StuckStates[vehicle] = stuck;
            currentLane.m_LaneFlags |= CarLaneFlags.IgnoreBlocker;
            return true;
        }
    }
}

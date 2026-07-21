using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing
{
    /// <summary>
    /// Which cars are currently displaced by a corridor, and how they get their lane back.
    ///
    /// The store is deliberately separate from the passes that write it: PushVehicles,
    /// HoldVehicles and SqueezePast all touch the same cars, and the release has to happen once
    /// per tick from a single place regardless of which pass moved a car.
    ///
    /// Cars that are DRIVING again drift back to the middle of their lane; cars still standing
    /// keep holding the corridor open, exactly like a real emergency corridor in a jam.
    /// </summary>
    internal sealed class PushedCars
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        /// <summary>Car to the frame it was last pushed by any corridor.</summary>
        public readonly Dictionary<Entity, uint> Cars = new Dictionary<Entity, uint>();
        /// <summary>Car to the push direction that claimed it, so the overlapping corridors of a
        /// convoy cannot lerp the same car both ways within one tick.</summary>
        public readonly Dictionary<Entity, PushClaim> PushClaims = new Dictionary<Entity, PushClaim>();
        /// <summary>Shared scratch list for the removal passes.</summary>
        public readonly List<Entity> ReleaseBuffer = new List<Entity>();
        /// <summary>Blocker rig to the frame a lane change was last forced on it. Rate-limits
        /// the attempt per rig: re-issuing a change the game reverts floods the pathfinder, and
        /// a repath flood despawns unrelated vehicles.</summary>
        public readonly Dictionary<Entity, uint> BlockerShift = new Dictionary<Entity, uint>();

        // Local aliases so the relocated bodies below read as they always did.
        private Dictionary<Entity, uint> m_PushedCars => Cars;
        private Dictionary<Entity, PushClaim> m_PushClaims => PushClaims;
        private List<Entity> m_ReleaseBuffer => ReleaseBuffer;
        private Dictionary<Entity, uint> m_BlockerShift => BlockerShift;

        /// <summary>How many cars are currently displaced.</summary>
        public int PushedCarCount => Cars.Count;

        public PushedCars(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// Cars that are no longer in any corridor drift back to the middle of their lane
        /// once they are driving again. Cars still standing keep holding the corridor open,
        /// just like a real ClearTheWay in a jam.
        /// </summary>
        public void ReleasePushedCars(uint frame)
        {
            if (m_PushedCars.Count == 0)
            {
                return;
            }

            m_ReleaseBuffer.Clear();
            foreach (KeyValuePair<Entity, uint> entry in m_PushedCars)
            {
                Entity car = entry.Key;
                if (frame != uint.MaxValue && frame - entry.Value < kReleaseDelayFrames)
                {
                    continue;
                }
                if (!EntityManager.Exists(car) ||
                    !EntityManager.HasComponent<CarCurrentLane>(car) ||
                    EntityManager.HasComponent<ParkedCar>(car) ||
                    EntityManager.HasComponent<Deleted>(car))
                {
                    m_ReleaseBuffer.Add(car);
                    continue;
                }

                bool moving = EntityManager.HasComponent<Moving>(car) &&
                              math.lengthsq(EntityManager.GetComponentData<Moving>(car).m_Velocity) > 4f;
                if (!moving && frame != uint.MaxValue)
                {
                    continue;
                }

                CarCurrentLane lane = EntityManager.GetComponentData<CarCurrentLane>(car);
                if (lane.m_ChangeLane != Entity.Null)
                {
                    m_ReleaseBuffer.Add(car);
                    continue;
                }
                lane.m_LanePosition = math.lerp(lane.m_LanePosition, 0f, kReleaseRate);
                if (math.abs(lane.m_LanePosition) < 0.03f)
                {
                    lane.m_LanePosition = 0f;
                    m_ReleaseBuffer.Add(car);
                }
                EntityManager.SetComponentData(car, lane);
            }

            for (int i = 0; i < m_ReleaseBuffer.Count; i++)
            {
                m_PushedCars.Remove(m_ReleaseBuffer[i]);
            }

            // Expired push-direction claims: drop them in a slow sweep so the dictionary does
            // not grow with every car that ever crossed a corridor. Expiry alone already ends
            // a claim's effect (the checks above are frame-gated), this is pure bookkeeping.
            if ((frame & 0x3FFu) == 0u && m_PushClaims.Count > 0)
            {
                m_ReleaseBuffer.Clear();
                foreach (KeyValuePair<Entity, PushClaim> entry in m_PushClaims)
                {
                    if (frame - entry.Value.m_Frame > kPushClaimFrames)
                    {
                        m_ReleaseBuffer.Add(entry.Key);
                    }
                }
                for (int i = 0; i < m_ReleaseBuffer.Count; i++)
                {
                    m_PushClaims.Remove(m_ReleaseBuffer[i]);
                }
            }
        }
        /// <summary>Drops per-blocker lane-shift cooldowns for rigs that are long gone.
        /// Called on the same slow cadence as the other prunes (every 1024 frames).</summary>
        public void Prune(uint frame)
        {
            if (m_BlockerShift.Count == 0)
            {
                return;
            }
            m_ReleaseBuffer.Clear();
            foreach (KeyValuePair<Entity, uint> entry in m_BlockerShift)
            {
                if (frame - entry.Value > 2048u || !EntityManager.Exists(entry.Key))
                {
                    m_ReleaseBuffer.Add(entry.Key);
                }
            }
            for (int i = 0; i < m_ReleaseBuffer.Count; i++)
            {
                m_BlockerShift.Remove(m_ReleaseBuffer[i]);
            }
            m_ReleaseBuffer.Clear();
        }
    }
}

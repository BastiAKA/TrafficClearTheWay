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
// Game.Net has a LaneGeometry of its own - ours wins here, like the CarLaneFlags alias above.
using LaneGeometry = ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding.M_LaneGeometry;

namespace ClearTheWay
{
    /// <summary>
    /// Corridor shapes for lane GROUPS rather than single lanes.
    ///
    /// The roundabout ring - the other lane group with a shape of its own - lives in
    /// RoundaboutExtensions, together with the steering half that belongs to it.
    ///
    /// A master lane is an undecided lane group on a multi-lane road - the game has not yet
    /// picked which physical lane a vehicle will use. Expanding it to all its sub-lanes is what
    /// lets the corridor form across the whole carriageway ahead of the responder instead of only
    /// on the one lane it currently occupies.
    /// </summary>
    internal sealed class CorridorLaneGroups
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        public CorridorLaneGroups(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        public void AddMasterLaneGroup(Entity masterLane, float side, bool inverted, float minPos, float startOffset)
        {
            if (!EntityManager.HasComponent<Owner>(masterLane))
            {
                return;
            }
            MasterLane master = EntityManager.GetComponentData<MasterLane>(masterLane);
            Owner owner = EntityManager.GetComponentData<Owner>(masterLane);
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner.m_Owner))
            {
                return;
            }
            DynamicBuffer<Game.Net.SubLane> subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner.m_Owner, isReadOnly: true);
            int max = math.min(master.m_MaxIndex, subLanes.Length - 1);
            // German-style corridor on an undecided lane group: the corridor-side edge
            // lane (leftmost in right-hand traffic) evades outward, all others the other
            // way, opening the gap between the first and second lane.
            int lowestValid = -1;
            int highestValid = -1;
            for (int i = master.m_MinIndex; i <= max; i++)
            {
                Entity subLane = subLanes[i].m_SubLane;
                if (subLane == masterLane ||
                    !EntityManager.HasComponent<Game.Net.CarLane>(subLane) ||
                    EntityManager.HasComponent<MasterLane>(subLane))
                {
                    continue;
                }
                if (lowestValid < 0)
                {
                    lowestValid = i;
                }
                highestValid = i;
            }
            if (lowestValid < 0)
            {
                return;
            }
            bool laneInverted2 = (EntityManager.GetComponentData<Game.Net.CarLane>(subLanes[lowestValid].m_SubLane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
            int edgeIndex = ((side > 0f) != laneInverted2) ? lowestValid : highestValid;
            for (int i = lowestValid; i <= highestValid; i++)
            {
                Entity subLane = subLanes[i].m_SubLane;
                if (subLane == masterLane ||
                    !EntityManager.HasComponent<Game.Net.CarLane>(subLane) ||
                    EntityManager.HasComponent<MasterLane>(subLane))
                {
                    continue;
                }
                float pushDirection = (i == edgeIndex) ? -side : side;
                m_Ctx.Corridor.AddCorridorLane(subLane, minPos, pushDirection, inverted, startOffset, onPath: true);
            }
        }
    }
}

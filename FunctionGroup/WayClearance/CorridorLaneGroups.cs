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

            // ONLY THE MASTER LANE'S OWN DIRECTION. The SubLane buffer belongs to the edge and
            // holds BOTH carriageways, and a push direction is signed in the pushed car's OWN
            // travel frame - so handing an opposite-direction lane the responder's `side` sends
            // its traffic to the physically opposite side: towards the centre line, straight into
            // the path we are trying to clear. That is what Sebastian photographed, the oncoming
            // column pulling INTO the corridor instead of away from it.
            //
            // Every other pass that puts lanes into the corridor already guards this and says so:
            // AddCounterEvadeNeighbor ("never shove the rig onto the oncoming carriageway"),
            // ComputeChannelPlan and the roundabout ring sweep all filter on Invert. This one was
            // the single unguarded path.
            //
            // The reference is the MASTER lane's own flag - master lane and sub-lanes share the
            // owner, so their Invert flags are comparable (they would NOT be across two edges).
            // Where the master carries no CarLane we fall back to the first usable sub-lane, which
            // is what the old code did implicitly; the filter below then still keeps the group
            // internally consistent instead of mixing two carriageways.
            bool haveReference = EntityManager.HasComponent<Game.Net.CarLane>(masterLane);
            bool groupInverted = haveReference &&
                (EntityManager.GetComponentData<Game.Net.CarLane>(masterLane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;

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
                bool subInverted = (EntityManager.GetComponentData<Game.Net.CarLane>(subLane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
                if (!haveReference)
                {
                    groupInverted = subInverted; // first usable lane defines the group
                    haveReference = true;
                }
                else if (subInverted != groupInverted)
                {
                    continue; // other carriageway - not ours to part
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
            int edgeIndex = ((side > 0f) != groupInverted) ? lowestValid : highestValid;
            for (int i = lowestValid; i <= highestValid; i++)
            {
                Entity subLane = subLanes[i].m_SubLane;
                if (subLane == masterLane ||
                    !EntityManager.HasComponent<Game.Net.CarLane>(subLane) ||
                    EntityManager.HasComponent<MasterLane>(subLane))
                {
                    continue;
                }
                if (((EntityManager.GetComponentData<Game.Net.CarLane>(subLane).m_Flags & Game.Net.CarLaneFlags.Invert) != 0) != groupInverted)
                {
                    continue; // same filter as the pass above - both loops must agree
                }
                float pushDirection = (i == edgeIndex) ? -side : side;
                m_Ctx.Corridor.AddCorridorLane(subLane, minPos, pushDirection, inverted, startOffset, onPath: true);
            }
        }
    }
}

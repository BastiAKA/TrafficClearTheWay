using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;

namespace ClearTheWay
{
    /// <summary>
    /// DEBUG-ONLY single-vehicle state dump. When the WatchVehicle setting holds an entity index
    /// (a veh=/blocker=/car= number copied out of the log) and Verbose logging is on, this writes
    /// that one vehicle's full state a few times a second - the thing the ordinary logs never show
    /// for a plain civilian, so "why is X standing still" can be answered from data instead of a
    /// screenshot.
    ///
    /// Crucially it also reports whether WE are the reason it is stopped: its entry (if any) in the
    /// mod's own SpeedOverrides (a hold/slow/creep cap), whether it is on the near-wreck list, and
    /// whether it is a sacrificed lead blocker. An empty OURcap with a stopped vehicle means the
    /// mod is not holding it - look at the vanilla path/blocker fields instead.
    ///
    /// Cost: the only expensive part is resolving the index to a live Entity, which needs a scan of
    /// every car. That result is cached until the index changes or the vehicle despawns, so the
    /// scan runs rarely; the per-dump work is a handful of component reads on one entity, throttled
    /// to every 16th tick. Nothing here is ever run unless a watch index is actually set.
    /// </summary>
    internal sealed class VehicleWatch
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        private Entity m_Entity = Entity.Null;
        private int m_ResolvedIndex = int.MinValue;

        public VehicleWatch(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        public void Dump(Setting setting, uint frame)
        {
            if (!setting.VerboseLogging || string.IsNullOrWhiteSpace(setting.WatchVehicle))
            {
                return;
            }
            if (!int.TryParse(setting.WatchVehicle.Trim(), out int idx) || idx <= 0)
            {
                return;
            }
            // A few dumps per second, not every tick.
            if ((frame & 0xFu) != 0u)
            {
                return;
            }
            // Resolve index -> live Entity, cached until the index changes or it despawns.
            if (m_ResolvedIndex != idx || !EntityManager.Exists(m_Entity))
            {
                m_ResolvedIndex = idx;
                m_Entity = Entity.Null;
                NativeArray<Entity> cars = m_Ctx.WatchScanQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < cars.Length; i++)
                    {
                        if (cars[i].Index == idx)
                        {
                            m_Entity = cars[i];
                            break;
                        }
                    }
                }
                finally
                {
                    cars.Dispose();
                }
                if (m_Entity == Entity.Null)
                {
                    Mod.Log.Info($"[watch] veh={idx} not found (no live car with that index)");
                    return;
                }
            }

            Entity e = m_Entity;
            float speed = EntityManager.HasComponent<Moving>(e)
                ? math.length(EntityManager.GetComponentData<Moving>(e).m_Velocity) : 0f;

            string lane = "lane=-";
            if (EntityManager.HasComponent<CarCurrentLane>(e))
            {
                CarCurrentLane cl = EntityManager.GetComponentData<CarCurrentLane>(e);
                lane = $"lanePos={cl.m_LanePosition:F2} laneFlags={(uint)cl.m_LaneFlags:X} " +
                    $"obsolete={((cl.m_LaneFlags & CarLaneFlags.Obsolete) != 0 ? 1 : 0)} " +
                    $"ignoreBlk={((cl.m_LaneFlags & CarLaneFlags.IgnoreBlocker) != 0 ? 1 : 0)} " +
                    $"change={(cl.m_ChangeLane != Entity.Null ? 1 : 0)}";
            }

            string path = "path=-";
            if (EntityManager.HasComponent<PathOwner>(e))
            {
                PathFlags st = EntityManager.GetComponentData<PathOwner>(e).m_State;
                path = $"pathState={(uint)st:X} pend={((st & PathFlags.Pending) != 0 ? 1 : 0)} " +
                    $"stuck={((st & PathFlags.Stuck) != 0 ? 1 : 0)} fail={((st & PathFlags.Failed) != 0 ? 1 : 0)} " +
                    $"obs={((st & PathFlags.Obsolete) != 0 ? 1 : 0)}";
            }

            string blk = "blocker=-";
            if (EntityManager.HasComponent<Blocker>(e))
            {
                Blocker b = EntityManager.GetComponentData<Blocker>(e);
                blk = $"blocker={b.m_Blocker.Index} type={b.m_Type}";
            }

            string tgt = EntityManager.HasComponent<Target>(e)
                ? $"target={EntityManager.GetComponentData<Target>(e).m_Target.Index}" : "target=-";

            // The point of the dump: is the MOD the reason it is stopped?
            string ours = m_Ctx.VehicleControl.SpeedOverrides.TryGetValue(e, out SpeedOverride ov)
                ? $"OURcap={(ov.m_Mode == 0 ? "ceil" : "floor")}@{ov.m_Speed:F1}{(ov.m_HasTarget ? "+steer" : "")}"
                : "OURcap=none";
            bool nearWreck = m_Ctx.Accidents.NearWreckCarsFrame == frame &&
                m_Ctx.Accidents.NearWreckCars.Contains(e);
            bool sacrificed = m_Ctx.States.Sacrifice.ContainsKey(e);
            bool emergency = EntityManager.HasComponent<Car>(e) &&
                (EntityManager.GetComponentData<Car>(e).m_Flags & CarFlags.Emergency) != 0;
            bool wreck = EntityManager.HasComponent<Game.Events.InvolvedInAccident>(e) ||
                EntityManager.HasComponent<Damaged>(e);

            Mod.Log.Info($"[watch] veh={idx} frame={frame} speed={speed:F1} {lane} | {path} | {blk} {tgt} | " +
                $"{ours} nearWreck={(nearWreck ? 1 : 0)} sacrificed={(sacrificed ? 1 : 0)} " +
                $"emergency={(emergency ? 1 : 0)} wreck={(wreck ? 1 : 0)}");
        }
    }
}

using System.Collections.Generic;
using Game.Creatures;
using Game.Pathfind;
using Game.Vehicles;
using Unity.Entities;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Decides whether a hard-stuck responder is queued behind a PEDESTRIAN rather than behind a
    /// genuinely dead junction - and, if the pedestrian itself has stopped making progress, frees
    /// the pedestrian instead of deleting a car.
    ///
    /// Why this exists at all: the last-resort plug removal
    /// (<see cref="EmergencyEscalation.TryReleaseLeadBlocker"/>) flags the civilian car directly
    /// ahead Obsolete, which can end in a despawn. At an unsignalised zebra with heavy foot
    /// traffic that is pure waste - cars yield indefinitely, the jam resolves itself the moment
    /// the pedestrian pulse ebbs, and every replacement car stops at exactly the same spot. The
    /// "only civilian cars" rule does NOT catch it: only the FRONT-MOST car has the pedestrian as
    /// its blocker; every car behind it has a CAR as its blocker, so without this guard a busy
    /// crossing would loop-despawn the whole column, one car per throttle window.
    ///
    /// How the yield is detected (verified against Game.dll 1.6.0, CarLaneSpeedIterator):
    /// a crosswalk lane overlaps the car lane, so it appears in the car lane's LaneOverlap buffer;
    /// CheckOverlappingLanes walks that lane's occupants and routes creatures into CheckPedestrian,
    /// which writes m_Blocker = the pedestrian entity and BlockerType.Temporary. So a car yielding
    /// to someone on the zebra carries the CREATURE in its own Blocker component - no geometry
    /// search is needed, only a walk up the blocker chain to the head of the column.
    ///
    /// And the second half, which is the point Sebastian raised: pedestrians get stuck too, exactly
    /// like cars. A guard that says "never remove at a crossing" would turn a self-resolving jam
    /// into a permanent one. But deleting the car in front is the wrong lever for that case anyway
    /// - the next car stops in the same place. The right lever is the pedestrian, and for a
    /// pedestrian there is a SAFE one that does not exist for cars: setting
    /// CreatureLaneFlags.Obsolete makes HumanNavigationSystem re-find its lane, clear its path and
    /// request a fresh one (Game.dll: HumanNavigationSystem.UpdateNavigation, the
    /// m_Lane == Null || Obsolete branch, which sets PathFlags.Obsolete for a non-grouped, uncarried
    /// human). Nobody is deleted - the citizen simply re-routes, so its household and job survive.
    /// Deleting citizens is deliberately NOT an option here, unlike the car sacrifice.
    ///
    /// The escalation is therefore three-stage, per plugging pedestrian:
    ///   1. seen plugging          -> block the sacrifice, do nothing else (the normal pulse case)
    ///   2. still there after kPedPlugFrames  -> force it to re-path, keep blocking the sacrifice
    ///   3. still there after kPedPlugGiveUpFrames -> give up on it, release the guard and let the
    ///      car sacrifice run as before (something is wrong that we do not model, and the responder
    ///      has by then been standing for the light-override time PLUS this)
    /// </summary>
    internal sealed class PedestrianPlug
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;

        /// <summary>One plugging pedestrian's history. Keyed by the creature, not by the responder:
        /// the same pedestrian standing on the same zebra plugs every responder that arrives, and
        /// the question we need answered ("has THIS pedestrian stopped making progress?") is about
        /// the pedestrian.</summary>
        private struct PlugRecord
        {
            public uint m_SinceFrame;      // first tick it was seen plugging a column
            public uint m_LastSeenFrame;   // for pruning
            public uint m_RepathFrame;     // last tick we forced a re-path (0 = never)
        }

        private readonly Dictionary<Entity, PlugRecord> m_Plugs = new Dictionary<Entity, PlugRecord>();
        private readonly List<Entity> m_PruneScratch = new List<Entity>();

        public PedestrianPlug(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>
        /// True when the sacrifice must NOT fire because a pedestrian is holding the column.
        /// Walks the blocker chain forward from the responder; the first creature found is the
        /// reason the whole queue is standing. Returns false (sacrifice allowed) when no pedestrian
        /// is involved, or when this pedestrian has been given up on at stage 3.
        /// </summary>
        public bool BlocksSacrifice(Entity vehicle, uint frame, Setting setting)
        {
            Entity creature = FindPlugCreature(vehicle);
            if (creature == Entity.Null)
            {
                return false;
            }

            if (!m_Plugs.TryGetValue(creature, out PlugRecord record))
            {
                record = new PlugRecord { m_SinceFrame = frame };
            }
            record.m_LastSeenFrame = frame;
            uint plugFor = frame - record.m_SinceFrame;

            // Stage 3: the re-path did not help either. Stop protecting this crossing and let the
            // caller do what it did before the guard existed.
            if (plugFor >= kPedPlugGiveUpFrames)
            {
                m_Plugs[creature] = record;
                if (setting.VerboseLogging)
                {
                    Mod.Log.Info($"[pedplug] veh={vehicle.Index} ped={creature.Index} has plugged the column for " +
                        $"{plugFor}f despite a forced re-path - releasing the guard, the car sacrifice may run");
                }
                return false;
            }

            // Stage 2: not a pedestrian pulse any more. Free the PEDESTRIAN, not a car.
            if (plugFor >= kPedPlugFrames &&
                (record.m_RepathFrame == 0u || frame - record.m_RepathFrame >= kPedRepathCooldown) &&
                TryRepathPedestrian(creature))
            {
                record.m_RepathFrame = frame;
                if (setting.VerboseLogging)
                {
                    Mod.Log.Info($"[pedplug] veh={vehicle.Index} ped={creature.Index} stuck on the crossing for " +
                        $"{plugFor}f - forced it to re-path instead of sacrificing a car");
                }
            }
            else if (setting.VerboseLogging && plugFor < kPedPlugFrames && (frame & 0x7Fu) == 0u)
            {
                Mod.Log.Info($"[pedplug] veh={vehicle.Index} is yielding to ped={creature.Index} " +
                    $"({plugFor}f) - no sacrifice at a crossing");
            }

            m_Plugs[creature] = record;
            return true;
        }

        /// <summary>
        /// Walks up the blocker chain from the responder and returns the first CREATURE in it, i.e.
        /// the pedestrian the head of the column is yielding to. Entity.Null when the column ends
        /// in something else (a dead junction, a train, a vehicle with no blocker of its own).
        ///
        /// Bounded by kPlugChainHops rather than by a visited-set: the chain is walked every
        /// throttle window on the sim thread, so a HashSet per call would allocate for nothing. A
        /// cycle simply burns the hop budget and returns Null, which is the safe answer.
        /// </summary>
        private Entity FindPlugCreature(Entity vehicle)
        {
            Entity current = vehicle;
            for (int hop = 0; hop < kPlugChainHops; hop++)
            {
                if (!EntityManager.HasComponent<Blocker>(current))
                {
                    return Entity.Null;
                }
                Entity next = EntityManager.GetComponentData<Blocker>(current).m_Blocker;
                if (next == Entity.Null || next == current || next == vehicle || !EntityManager.Exists(next))
                {
                    return Entity.Null;
                }
                if (EntityManager.HasComponent<Creature>(next))
                {
                    return next;
                }
                // Only keep following CARS. A tram, a train or anything else at the head of the
                // queue is not a case this guard has an opinion about.
                if (!EntityManager.HasComponent<Car>(next))
                {
                    return Entity.Null;
                }
                current = next;
            }
            return Entity.Null;
        }

        /// <summary>
        /// Makes one pedestrian re-path. CreatureLaneFlags.Obsolete is the creature-side equivalent
        /// of the CarLaneFlags.Obsolete used on cars: HumanNavigationSystem re-finds the current
        /// lane, clears the path elements and raises PathFlags.Obsolete so a fresh route is
        /// requested. Nothing is deleted and nothing of ours is left behind on the entity - the
        /// game clears the flag itself in TryFindCurrentLane.
        ///
        /// Returns false when the pedestrian is one we must not touch, so the caller does not
        /// record a re-path that never happened.
        /// </summary>
        private bool TryRepathPedestrian(Entity pedestrian)
        {
            if (!EntityManager.HasComponent<Human>(pedestrian) ||
                !EntityManager.HasComponent<HumanCurrentLane>(pedestrian) ||
                !EntityManager.HasComponent<PathOwner>(pedestrian))
            {
                return false;
            }
            // Carried (a child being carried, a casualty) - the game skips the whole re-find
            // branch for these, so setting the flag would do nothing but linger.
            if ((EntityManager.GetComponentData<Human>(pedestrian).m_Flags & HumanFlags.Carried) != 0)
            {
                return false;
            }
            // Riding in a vehicle: not on the crossing at all, and its lane belongs to the vehicle.
            if (EntityManager.HasComponent<CurrentVehicle>(pedestrian) &&
                EntityManager.GetComponentData<CurrentVehicle>(pedestrian).m_Vehicle != Entity.Null)
            {
                return false;
            }
            // Part of a group (a family walking together, a tour): only the LEADER owns the path,
            // and the game re-paths a follower by copying the leader's route. Re-pathing a
            // follower would just have it re-find its lane and then fall back in line, so leave
            // groups to the leader - which is itself eligible if it is the one plugging.
            if (EntityManager.HasComponent<GroupMember>(pedestrian) &&
                EntityManager.GetComponentData<GroupMember>(pedestrian).m_Leader != Entity.Null)
            {
                return false;
            }
            HumanCurrentLane lane = EntityManager.GetComponentData<HumanCurrentLane>(pedestrian);
            lane.m_Flags |= CreatureLaneFlags.Obsolete;
            EntityManager.SetComponentData(pedestrian, lane);
            return true;
        }

        /// <summary>Forgets pedestrians that have not plugged anything for a while, and any that
        /// have despawned. Same cadence and generosity as the other escalation latches: dropping a
        /// record early restarts the three-stage clock and the pedestrian would never reach the
        /// re-path.</summary>
        public void Prune(uint frame)
        {
            if (m_Plugs.Count == 0)
            {
                return;
            }
            m_PruneScratch.Clear();
            foreach (KeyValuePair<Entity, PlugRecord> entry in m_Plugs)
            {
                if (frame - entry.Value.m_LastSeenFrame > 1024u || !EntityManager.Exists(entry.Key))
                {
                    m_PruneScratch.Add(entry.Key);
                }
            }
            for (int i = 0; i < m_PruneScratch.Count; i++)
            {
                m_Plugs.Remove(m_PruneScratch[i]);
            }
            m_PruneScratch.Clear();
        }
    }
}

using System.Collections.Generic;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay.FunctionGroup.GeneralImprovements
{
    /// <summary>
    /// How long a wreck is allowed to stay, and what is done to it while it waits.
    ///
    /// Vanilla deletes a car wreck a fixed number of frames after the crash regardless of whether
    /// a responder ever arrived - which is the original problem this mod exists for. So a settled,
    /// uncleared wreck has its delete deadline pushed forward while it waits. Bicycles need their
    /// own, much shorter hold: the game deletes those after 300 frames rather than 14400, so the
    /// car value would let them dissolve within seconds.
    ///
    /// The extension is bounded, and the bound depends on whether anyone is coming: an unclaimed
    /// wreck is let go after ~10 minutes, while one with a recovery vehicle actually dispatched is
    /// held far longer. When the time is up the wreck is not deleted here - its InvolvedFrame is
    /// back-dated so the GAME removes it through its own path, which also clears the accident site
    /// and its icon. Touching that machinery directly has historically been crash-prone.
    ///
    /// Two other things happen while a wreck waits: its blocked lanes are marked secured (vanilla
    /// otherwise re-broadcasts PathfindUpdated every tick, and that endless repath is what
    /// evaporates the traffic), and a wreck still jiggling from collision impulses has its
    /// velocity zeroed so it stops firing impacts into the queue head.
    /// </summary>
    internal sealed class WreckLifetime
    {
        private readonly WayClearanceContext m_Ctx;
        private EntityManager EntityManager => m_Ctx.EntityManager;
        private Dictionary<Entity, uint> m_WreckSeenInvolvedFrame => m_Ctx.Accidents.m_WreckSeenInvolvedFrame;

        public WreckLifetime(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Applies the lifetime policy to one wreck. Returns its TRUE age in frames
        /// (since it first crashed), which the caller needs for the settle-time gates.</summary>
        public uint Apply(Entity wreck, Setting setting, uint frame, ref WreckSweepStats stats)
        {
                // --- Wreck lifetime: bound-extend so it waits for the responders ---
                Game.Events.InvolvedInAccident involved =
                    EntityManager.GetComponentData<Game.Events.InvolvedInAccident>(wreck);
                if (!m_WreckSeenInvolvedFrame.TryGetValue(wreck, out uint originalInvolved))
                {
                    originalInvolved = involved.m_InvolvedFrame;
                    m_WreckSeenInvolvedFrame[wreck] = originalInvolved;
                }
                uint trueAge = frame - originalInvolved;
                stats.m_MaxWreckAge = math.max(stats.m_MaxWreckAge, trueAge);
                bool destroyed = EntityManager.HasComponent<Destroyed>(wreck);
                if (destroyed) stats.m_Destroyed++;
                bool cleared = destroyed &&
                    EntityManager.GetComponentData<Destroyed>(wreck).m_Cleared >= 1f;
                // Only touch settled (no longer Moving) wrecks so we never disturb the
                // game's come-to-rest detection; skip already-cleared ones; stop once the
                // wreck has genuinely waited longer than the cap. Bicycles/motorcycles
                // have a 300-frame (not 14400) delete deadline once the site is secured,
                // so they must be held much younger or they dissolve within seconds.
                uint holdAge = EntityManager.HasComponent<Game.Vehicles.Bicycle>(wreck)
                    ? kWreckHoldAgeBike : kWreckHoldAge;
                // Once a recovery vehicle has been dispatched to this wreck, the despawn is
                // effectively paused (much longer cap) so it is never deleted out from under
                // an approaching tow truck - the "vehicle vanished mid-tow" bug. A hooked
                // wreck loses InvolvedInAccident and leaves this query entirely; this covers
                // the EN-ROUTE phase, where the wreck is claimed but not yet coupled.
                bool recoveryClaimed = m_Ctx.WreckClearing.IsRecoveryClaimed(wreck);
                uint giveUpAge = recoveryClaimed ? kWreckClaimedGiveUpAge : kWreckGiveUpAge;
                if (setting.PreventAccidentDespawn && !cleared &&
                    !EntityManager.HasComponent<Moving>(wreck) &&
                    trueAge < giveUpAge && frame > holdAge)
                {
                    uint targetInvolved = frame - holdAge;
                    if (involved.m_InvolvedFrame < targetInvolved)
                    {
                        involved.m_InvolvedFrame = targetInvolved; // push the delete deadline forward
                        EntityManager.SetComponentData(wreck, involved);
                        stats.m_Extended++;
                    }
                }
                else if (setting.PreventAccidentDespawn && !cleared &&
                    !EntityManager.HasComponent<Moving>(wreck) &&
                    trueAge >= giveUpAge && frame > kVanillaDeleteAge)
                {
                    // Waited long enough and nobody recovered it - give up and let the game
                    // despawn it cleanly by back-dating the accident past its delete deadline
                    // (a towed wreck loses InvolvedInAccident at hookup, so it is never in this
                    // query - we only ever give up on wrecks that are NOT being towed).
                    uint deleteAge = EntityManager.HasComponent<Game.Vehicles.Bicycle>(wreck) ? 300u : kVanillaDeleteAge;
                    uint targetInvolved = frame - deleteAge - 1u;
                    if (involved.m_InvolvedFrame > targetInvolved) // only ever move it EARLIER
                    {
                        involved.m_InvolvedFrame = targetInvolved;
                        EntityManager.SetComponentData(wreck, involved);
                        if (setting.VerboseLogging)
                        {
                            Mod.Log.Info($"[wreckgiveup] wreck={wreck.Index} trueAge={trueAge} - no recovery, despawning");
                        }
                    }
                }
                // Silence the repath broadcast - THE actual despawn source: for an aged
                // wreck (site gone / 14400 elapsed) AccidentVehicleSystem re-adds
                // PathfindUpdated to every blocked lane EVERY tick unless the lane has
                // Game.Net.CarLaneFlags.IsSecured. Each broadcast invalidates the path of
                // every vehicle routed through the lane (map-wide!), the repath finds the
                // destination unreachable behind a full block, and the pathfinder hands
                // out a disposal path into the nearest building connection where the car
                // unspawns mid-drive. Setting IsSecured (the game's own "secured accident
                // site" lane state - it also lets traffic pass at 0.8x instead of 0.5x
                // speed) stops the spam; LaneDataSystem clears the flag again when the
                // wreck is gone and the lane is rebuilt.
                if (setting.PreventAccidentDespawn)
                {
                    bool hasBuf = EntityManager.HasBuffer<Game.Objects.BlockedLane>(wreck);
                    int bufLen = 0, carLanes = 0, alreadySecured = 0;
                    if (hasBuf)
                    {
                        DynamicBuffer<Game.Objects.BlockedLane> blockedLanes =
                            EntityManager.GetBuffer<Game.Objects.BlockedLane>(wreck, isReadOnly: true);
                        bufLen = blockedLanes.Length;
                        for (int b = 0; b < blockedLanes.Length; b++)
                        {
                            Entity blockedLane = blockedLanes[b].m_Lane;
                            if (EntityManager.HasComponent<Game.Net.CarLane>(blockedLane))
                            {
                                carLanes++;
                                Game.Net.CarLane carLane = EntityManager.GetComponentData<Game.Net.CarLane>(blockedLane);
                                if ((carLane.m_Flags & Game.Net.CarLaneFlags.IsSecured) == 0)
                                {
                                    carLane.m_Flags |= Game.Net.CarLaneFlags.IsSecured;
                                    EntityManager.SetComponentData(blockedLane, carLane);
                                    stats.m_SecuredLanes++;
                                }
                                else
                                {
                                    alreadySecured++;
                                }
                            }
                        }
                    }
                    // COMMENTED OUT 2026-07-18 (biggest verbose-log contributor, ~14k lines/4h;
                    // its "why is the silencer idle" investigation is done). Re-enable instantly
                    // by uncommenting the block below.
                    // Diagnose why the silencer may be idle: log the raw lane state of the
                    // first few wrecks (buffer present? lanes? already secured?).
                    // if (setting.VerboseLogging && frame % 120u == 0u && w < 3)
                    // {
                    //     Mod.Log.Info($"[wrecklanes] wreck={wreck.Index} hasBuf={(hasBuf ? 1 : 0)} " +
                    //         $"len={bufLen} carLanes={carLanes} alreadySecured={alreadySecured} " +
                    //         $"stopped={(EntityManager.HasComponent<Stopped>(wreck) ? 1 : 0)} moving={(EntityManager.HasComponent<Moving>(wreck) ? 1 : 0)}");
                    // }
                }

                // Freeze the jiggle: a slow OutOfControl wreck keeps drifting into the
                // queue head and "infects" it via collision impacts (see constants). Zero
                // its velocity so the pile stays where it is and the game settles it.
                if (setting.PreventAccidentDespawn &&
                    EntityManager.HasComponent<OutOfControl>(wreck) &&
                    EntityManager.HasComponent<Moving>(wreck))
                {
                    Moving wreckMoving = EntityManager.GetComponentData<Moving>(wreck);
                    if (math.lengthsq(wreckMoving.m_Velocity) < kWreckFreezeSpeed * kWreckFreezeSpeed &&
                        (math.lengthsq(wreckMoving.m_Velocity) > 0f || math.lengthsq(wreckMoving.m_AngularVelocity) > 0f))
                    {
                        wreckMoving.m_Velocity = float3.zero;
                        wreckMoving.m_AngularVelocity = float3.zero;
                        EntityManager.SetComponentData(wreck, wreckMoving);
                        stats.m_Frozen++;
                    }
                }
            return trueAge;
        }
    }
}

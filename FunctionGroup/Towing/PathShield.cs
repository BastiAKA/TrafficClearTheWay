using System.Collections.Generic;
using Game.Pathfind;
using Unity.Entities;
using static ClearTheWay.Tuning;

namespace ClearTheWay
{
    /// <summary>
    /// Keeping a recovery vehicle alive when its route fails, without leaving it rudderless.
    ///
    /// Two vanilla facts collide here. MaintenanceVehicleAISystem deletes a vehicle outright on
    /// <c>PathfindFailed &amp;&amp; (IsStuck || Returning)</c> - and a truck hauling a wreck has
    /// Returning pinned every tick by KeepTruckReturning, so the failed path alone is a death
    /// sentence (truck 543656: hooked 21:22:52.9, gone by 21:22:53.6, wreck deleted with it).
    /// Clearing the flag prevents that. But a vehicle whose path failed and whose flag we then
    /// wiped has NO route at all - it just carries straight on, over the kerb and across the park.
    ///
    /// So the flag must be cleared AND a new path asked for. The ask is what needs throttling, not
    /// the clearing: repathing every tick is what once flooded the pathfinder and made unrelated
    /// vehicles despawn. Hence: request immediately the first time - the vehicle is routeless right
    /// now and every frame of delay is a frame of driving blind - then at most once per
    /// kTowPathRetryFrames while it keeps failing.
    ///
    /// The earlier version staggered by <c>(frame + index) % 256</c>, which has no "first time" at
    /// all: whatever the moment of failure, the vehicle waited on average ~128 frames for the next
    /// slot to come round. That is the straight-ahead drive Sebastian caught in the screenshot.
    /// </summary>
    internal static class PathShield
    {
        /// <summary>
        /// Clear a failed/stuck path so the AI cannot delete the vehicle, and get it a new route.
        /// Returns true when the path had actually failed (worth logging by the caller).
        /// </summary>
        public static bool Shield(EntityManager entityManager, Dictionary<Entity, uint> retryFrames,
            Entity vehicle, uint frame)
        {
            if (!entityManager.HasComponent<PathOwner>(vehicle))
            {
                return false;
            }
            PathOwner pathOwner = entityManager.GetComponentData<PathOwner>(vehicle);
            if ((pathOwner.m_State & (PathFlags.Failed | PathFlags.Stuck)) == 0)
            {
                // Healthy: forget it, so the next failure counts as a first one and is repathed
                // at once, and so the map cannot grow with every vehicle that ever hiccuped.
                retryFrames.Remove(vehicle);
                return false;
            }
            pathOwner.m_State &= ~(PathFlags.Failed | PathFlags.Stuck);
            if (!retryFrames.TryGetValue(vehicle, out uint lastRetry) ||
                frame - lastRetry >= kTowPathRetryFrames)
            {
                pathOwner.m_State |= PathFlags.Obsolete;
                retryFrames[vehicle] = frame;
            }
            entityManager.SetComponentData(vehicle, pathOwner);
            return true;
        }
    }
}

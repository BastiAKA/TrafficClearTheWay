using System.Runtime.InteropServices;
using Colossal.Serialization.Entities;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// Marks a leftover "relic" wreck we have re-armed for regular recovery instead of deleting it.
    ///
    /// Relics are damaged/destroyed cars stranded from a failed tow (InvolvedInAccident stripped,
    /// a dead null Controller left behind, and - for Destroyed ones - marked cleared so vanilla
    /// stopped requesting recovery). We re-arm them (drop the stale Controller, reset the cleared
    /// flag, drop the stale MaintenanceConsumer) so Game.Simulation.DamagedVehicleSystem raises a
    /// fresh maintenance request; a recovery vehicle (preferably our Abschleppwagen) then hauls or
    /// clears it through the normal, working chain. This tag both prevents re-arming the same wreck
    /// twice and lets TryHookup couple it (it has no InvolvedInAccident, so the tag is the
    /// alternative "this is a legitimate recovery target" signal). A watchdog deletes it as a
    /// fallback if no vehicle recovers it within a timeout (fully boxed in, no route).
    ///
    /// Persistent (IEmptySerializable) so a save/load mid-recovery keeps the wreck tracked.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    public struct RelicRecovery : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
    }
}

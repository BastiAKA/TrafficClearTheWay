using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// One member of an accident cluster's cached near-wreck list (see
    /// <see cref="FunctionGroup.Accidents.AccidentGuard"/>). Carries what the membership sweep
    /// already had to look up and what cannot change before the next sweep, so the per-tick
    /// protection loop does not ask for it again.
    ///
    /// Only the component-level fact is cached. CarFlags.Emergency is NOT - a police car can
    /// switch its siren on between two sweeps, and it must be exempt from the shields on that
    /// very tick, so that one is still read live.
    /// </summary>
    internal struct NearWreckCar
    {
        public Entity m_Entity;
        public bool m_Maintenance; // has Game.Vehicles.MaintenanceVehicle
    }
}

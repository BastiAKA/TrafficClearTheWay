using Unity.Entities;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes.Geometry
{
    /// <summary>
    /// The measured body of one vehicle PREFAB - not of one vehicle. Every car of the same model
    /// shares these numbers, which is what makes them cacheable at all (see
    /// <see cref="GeometryProvider"/>).
    ///
    /// Defaults are those of an ordinary car and are used when a prefab carries no geometry data,
    /// so a measurement never returns 0 and silently collapses a division.
    /// </summary>
    public class VehicleGeometry
    {
        /// <summary>What was measured: normally the prefab, or the entity itself when it has no
        /// prefab to measure and only the defaults below apply.</summary>
        public Entity source { get; }

        /// <summary>Across the vehicle (m).</summary>
        public float width { get; set; } = 2.0f;

        /// <summary>Nose to tail (m).</summary>
        public float length { get; set; } = 4.5f;

        /// <summary>Ground to roof (m).</summary>
        public float height { get; set; } = 1.5f;

        public VehicleGeometry(Entity source)
        {
            this.source = source;
        }
    }
}

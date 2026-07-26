using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes.Geometry;
using Unity.Entities;
using Unity.Mathematics;
using static ClearTheWay.Tuning;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes
{
    /// <summary>
    /// Mid-size vehicles - delivery vans, garbage trucks, buses: everything longer than a car but
    /// still a single body (a tractor with a trailer is a different problem, see
    /// <see cref="VehicleTrailerExt"/>).
    ///
    /// They have to clear FURTHER aside than a car, and the reason is the geometry of how a
    /// vehicle actually moves over: it does not step sideways, it is pushed diagonally forward.
    /// At roughly 45 degrees, the lateral room a vehicle opens up is about a QUARTER OF ITS OWN
    /// LENGTH - so the offset that frees the same amount of lane scales with length, not with a
    /// fixed number of metres. A car makes its ~1.1 m and the corridor works; a 9 m garbage truck
    /// given that same 1.1 m still has its body across the gap, which is why long vehicles look
    /// like they are ignoring the corridor.
    ///
    /// Deliberately no vehicle-type list. Type-based detection means a threshold argument for
    /// every asset (is this van a van?), while the length is exactly the quantity the geometry
    /// above is about - and it scales continuously, so nothing jumps at the boundary. The
    /// measurement itself comes from <see cref="GeometryProvider"/>, cached per prefab.
    /// </summary>
    internal sealed class Delivery_MidSize
    {
        private readonly WayClearanceContext m_Ctx;
        private GeometryProvider m_Geometry => m_Ctx.PrefabGeometry;

        public Delivery_MidSize(WayClearanceContext ctx)
        {
            m_Ctx = ctx;
        }

        /// <summary>Longer than an ordinary car - a van, a truck, a bus.</summary>
        public bool IsBiggerThanCar(Entity vehicle)
        {
            return m_Geometry.VehicleLength(vehicle) > kLongVehicleLength;
        }

        /// <summary>
        /// The lateral offset (m) this vehicle should aim for, given what the corridor lane asks
        /// for. A long body gets length/4 (see the class summary), never less than what the lane
        /// already demanded and never more than the cap - a bus is asked to clear the lane, not to
        /// park on the far kerb.
        /// </summary>
        public float PushMeters(Entity vehicle, float baseMeters)
        {
            float length = m_Geometry.VehicleLength(vehicle);
            if (length <= kLongVehicleLength)
            {
                return baseMeters;
            }
            // A BONUS on top of whatever stage the caller asked for - deliberately not a floor.
            // As a floor (max(baseMeters, length/4)) the size rule silently swallowed the whole
            // escalation once the stage values grew: with soft 1.4 and hard 2.0 both landed on the
            // same 2.2, so a bus got an identical offset whether the corridor was gentle or the
            // responder had been stuck for half a minute - and its SOFT offset exceeded a car's
            // HARD one. Sebastian: big vehicles behave oddly and never soft-evade properly.
            // Measured from kLongVehicleLength up, so a vehicle just over car length gets almost
            // nothing extra and the bonus grows with how much road the body actually covers.
            float bonus = math.min((length - kLongVehicleLength) * kLongVehicleFraction, kLongVehicleMaxMeters);
            return baseMeters + bonus;
        }
    }
}

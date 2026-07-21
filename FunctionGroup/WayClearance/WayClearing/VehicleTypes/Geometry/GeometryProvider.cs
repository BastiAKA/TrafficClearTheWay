using System.Collections.Generic;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes.Geometry
{
    /// <summary>
    /// Every measurement this mod takes off a PREFAB - vehicle bodies and lane widths - in one
    /// place, measured once and then cached.
    ///
    /// Caching is not an optimisation here, it is the difference between the corridor being
    /// affordable and not. The lateral conversion is needed for every pushed car of every corridor
    /// lane of every responder, every tick; with ~170 sirens that is thousands of calls per tick,
    /// and the raw form costs several component lookups each. Two properties make the cache sound:
    /// the numbers are constant for the lifetime of a prefab, and they are shared by every vehicle
    /// of that model - so the cache is keyed BY PREFAB, never by vehicle. A per-vehicle cache would
    /// grow with the city (every car ever spawned) and still miss on the very next car of the same
    /// model.
    ///
    /// AXES: ObjectGeometryData.m_Size is (x = width, y = height, z = length). The width mapping is
    /// the one the mod has always used and is confirmed in-game; if length and height ever look
    /// swapped (a garbage truck reported shorter than a car), y/z is the place to correct it - here,
    /// once, instead of at every call site.
    /// </summary>
    internal sealed class GeometryProvider
    {
        private readonly EntityManager EntityManager;

        /// <summary>vehicle prefab -> measured body.</summary>
        private readonly Dictionary<Entity, VehicleGeometry> m_Geometries = new Dictionary<Entity, VehicleGeometry>();

        /// <summary>lane prefab -> lane width (m).</summary>
        private readonly Dictionary<Entity, float> m_LaneWidths = new Dictionary<Entity, float>();

        public GeometryProvider(EntityManager entityManager)
        {
            EntityManager = entityManager;
        }

        /// <summary>
        /// The measured body of a vehicle (via its prefab). Never null - an entity without prefab
        /// or geometry data gets the ordinary-car defaults.
        /// </summary>
        public VehicleGeometry GetGeometry(Entity vehicle)
        {
            if (!EntityManager.HasComponent<PrefabRef>(vehicle))
            {
                // Nothing to measure and nothing to cache it under: hand back a fresh set of
                // car defaults. A shared instance would be cheaper, but these are settable
                // properties - one caller writing to it would poison every later fallback.
                return new VehicleGeometry(vehicle);
            }
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            if (m_Geometries.TryGetValue(prefab, out VehicleGeometry geometry))
            {
                return geometry;
            }
            geometry = new VehicleGeometry(prefab);
            if (EntityManager.HasComponent<ObjectGeometryData>(prefab))
            {
                float3 size = EntityManager.GetComponentData<ObjectGeometryData>(prefab).m_Size;
                geometry.width = size.x;
                geometry.height = size.y;
                geometry.length = size.z;
            }
            m_Geometries[prefab] = geometry;
            return geometry;
        }

        /// <summary>Across the vehicle (m).</summary>
        public float VehicleWidth(Entity vehicle)
        {
            return GetGeometry(vehicle).width;
        }

        /// <summary>Nose to tail (m).</summary>
        public float VehicleLength(Entity vehicle)
        {
            return GetGeometry(vehicle).length;
        }

        /// <summary>Ground to roof (m).</summary>
        public float VehicleHeight(Entity vehicle)
        {
            return GetGeometry(vehicle).height;
        }

        /// <summary>Width (m) of the lane itself, 4 m when the lane prefab says nothing.</summary>
        public float LaneWidth(Entity lane)
        {
            if (!EntityManager.HasComponent<PrefabRef>(lane))
            {
                return 4f;
            }
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
            if (m_LaneWidths.TryGetValue(prefab, out float width))
            {
                return width;
            }
            width = EntityManager.HasComponent<NetLaneData>(prefab)
                ? EntityManager.GetComponentData<NetLaneData>(prefab).m_Width
                : 4f;
            m_LaneWidths[prefab] = width;
            return width;
        }

        /// <summary>
        /// How many metres one m_LanePosition unit moves this vehicle on this lane: the lane's
        /// width minus the vehicle's own, i.e. the room it has to move within the lane. Floored at
        /// 0.5 m because every caller divides by it.
        /// </summary>
        public float LateralSlack(Entity vehicle, Entity lane)
        {
            return math.max(0.5f, LaneWidth(lane) - VehicleWidth(vehicle));
        }

        /// <summary>The same slack when the lane width is already known - the corridor pass hoists
        /// it out of its per-car loop, so it must not be re-derived for every car.</summary>
        public float LateralSlack(Entity vehicle, float laneWidth)
        {
            return math.max(0.5f, laneWidth - VehicleWidth(vehicle));
        }
    }
}

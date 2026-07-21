using ClearTheWay.FunctionGroup.Accidents;
using ClearTheWay.FunctionGroup.GeneralImprovements;
using ClearTheWay.FunctionGroup.WayClearance;
using ClearTheWay.FunctionGroup.WayClearance.Squeezing;
using ClearTheWay.FunctionGroup.WayClearance.Squeezing.AtTarget;
using ClearTheWay.FunctionGroup.WayClearance.Squeezing.Roundabout;
using ClearTheWay.FunctionGroup.WayClearance.TrafficLights;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.LanePathFinding;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes;
using ClearTheWay.FunctionGroup.WayClearance.WayClearing.VehicleTypes.Geometry;
using Game.City;
using Game.Simulation;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// The collaborators the way-clearance passes share, in one place.
    ///
    /// The corridor work is one cooperating whole - the escalation chain reads lane geometry,
    /// writes speed overrides, consults the shared responder latches and calls into the corridor
    /// builder and the traffic shaper. Handing every class the same half-dozen references through
    /// its own constructor meant every new dependency rippled through every signature, so they
    /// are collected here instead and each pass takes the context.
    ///
    /// Fields rather than properties, and assigned in two phases by
    /// <see cref="ClearTheWaySystem"/>: the leaves (geometry, control, states) are constructed
    /// first, then the passes that depend on each other. A pass must therefore only dereference
    /// the context from its methods, never from its own constructor.
    /// </summary>
    internal sealed class WayClearanceContext
    {
        public EntityManager EntityManager;
        public SimulationSystem Simulation;
        public CityConfigurationSystem CityConfiguration;
        public Game.Objects.SearchSystem ObjectSearch;
        public Game.Net.SearchSystem NetSearch;

        // Leaves - no dependencies of their own.
        /// <summary>Every measurement taken off a prefab (body sizes, lane widths), cached.</summary>
        public GeometryProvider PrefabGeometry;
        public M_LaneGeometry Geometry;
        public M_LanePosition LanePosition;
        public VehicleControl VehicleControl;
        public ResponderStates States;

        // Passes.
        public PushedCars PushedCars;
        public CorridorBuilder Corridor;
        public CorridorLaneGroups LaneGroups;
        public RoundaboutExtensions Roundabout;
        public VehicleTrailerExt Trucks;
        public Delivery_MidSize MidSize;
        public GreenLightChain Lights;
        public ChannelPlanner Channel;
        public PushVehicles Push;
        public HoldVehicles Hold;
        public SqueezePast Squeeze;
        public LaneChange LaneChange;
        public LaneObstruction Obstruction;
        public ArrivalAssist Arrival;
        public ArrivalTarget ArrivalTarget;
        public DesperateBehaviour Desperate;
        public EmergencyEscalation Escalation;
        public EscalationCorridor CorridorRun;
        public EscalationSteering Steering;
        public EscalationSpeed SpeedStage;
        public NearWreckProtection NearWreck;
        public AccidentReporting Reporting;
        public WreckClearing WreckClearing;
        public WreckLifetime WreckLifetime;
        public AccidentGuard Accidents;
    }
}

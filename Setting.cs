using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;

namespace ClearTheWay
{
    [FileLocation(nameof(ClearTheWay))]
    [SettingsUIGroupOrder(kBehaviorGroup)]
    [SettingsUIShowGroupName(kBehaviorGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kBehaviorGroup = "Behavior";

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool Enabled { get; set; }

        /// <summary>How far ahead of an emergency vehicle cars start pulling aside (meters).</summary>
        [SettingsUISlider(min = 30f, max = 300f, step = 10f, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kBehaviorGroup)]
        public int CorridorDistance { get; set; }

        /// <summary>Allow emergency vehicles to squeeze past cars that have pulled aside.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool SqueezePastBlockers { get; set; }

        /// <summary>Also pull cars aside in reachable parallel lanes of multi-lane roads.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool ClearParallelLanes { get; set; }

        /// <summary>Stuck emergency vehicles change onto a free parallel lane to overtake the queue.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool OvertakeStuckTraffic { get; set; }

        /// <summary>Traffic lights along the corridor switch to green for the emergency vehicle.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool ForceGreenLights { get; set; }

        /// <summary>Stuck emergency vehicles may cross the center line and use the oncoming carriageway.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool UseOncomingLane { get; set; }

        /// <summary>In multi-lane roundabouts all traffic moves to the inner lanes and the outer lane is kept free for the responder.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool ClearRoundaboutInner { get; set; }

        /// <summary>Pedestrians wait at the kerb instead of stepping in front of a responder.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool StopPedestrians { get; set; }

        /// <summary>Give recovery/tow vehicles heading to an accident a gentle make-way corridor.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool AssistTowTrucks { get; set; }

        /// <summary>Last resort for a recovery vehicle wedged for minutes: remove the single
        /// vehicle standing in its way so the whole chain can move again.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool UnblockRecoveryVehicles { get; set; }

        /// <summary>Vehicles queued behind an accident wait instead of despawning.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool PreventAccidentDespawn { get; set; }

        /// <summary>Settled wrecks are moved onto one lane so traffic can pass and stops evaporating.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool ClearWrecksAside { get; set; }

        /// <summary>Traffic may clear onto tram beds, grass strips and medians - never onto an
        /// occupied tram bed or a platform.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool CrossMedians { get; set; }

        /// <summary>Adds the buildable tow depot (cloned road maintenance depot, vehicle recovery only).</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool TowDepot { get; set; }

        /// <summary>Recovery trucks couple the wreck as a trailer and haul it to the depot.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool TowWrecks { get; set; }

        /// <summary>Adds a dedicated "Abschleppwagen" prefab (vehicle-recovery only, with tow-tractor capability) that the tow depot spawns.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool TowTruckPrefab { get; set; }

        /// <summary>Write periodic diagnostics to Logs/ClearTheWay.log.</summary>
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool VerboseLogging { get; set; }

        /// <summary>DEBUG: entity index of a single vehicle to dump full state for a few times a
        /// second, taken from a veh=/blocker=/car= number in ClearTheWay.log. Empty or 0 = off.
        /// Only active while Verbose logging is on.</summary>
        [SettingsUITextInput]
        [SettingsUISection(kSection, kBehaviorGroup)]
        public string WatchVehicle { get; set; }

        public sealed override void SetDefaults()
        {
            Enabled = true;
            CorridorDistance = 120;
            SqueezePastBlockers = true;
            ClearParallelLanes = true;
            OvertakeStuckTraffic = true;
            ForceGreenLights = true;
            UseOncomingLane = true;
            ClearRoundaboutInner = true;
            StopPedestrians = true;
            AssistTowTrucks = true;
            UnblockRecoveryVehicles = true;
            PreventAccidentDespawn = true;
            ClearWrecksAside = true;
            CrossMedians = true;
            TowDepot = true;
            TowWrecks = true;
            TowTruckPrefab = true; // dedicated tow-truck prefab + flatbed towing (on by default per Sebastian)
            VerboseLogging = false;
            WatchVehicle = "";
        }
    }
}

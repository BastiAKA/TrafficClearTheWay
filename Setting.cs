using Colossal.IO.AssetDatabase;
using Colossal.Localization;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI;
using Game.UI.Localization;

namespace ClearTheWay
{
    [FileLocation(nameof(ClearTheWay))]
    [SettingsUITabOrder(kSection, kPerfSection)]
    [SettingsUIGroupOrder(kBehaviorGroup, kPerfGroup)]
    [SettingsUIShowGroupName(kBehaviorGroup, kPerfGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kBehaviorGroup = "Behavior";
        public const string kPerfSection = "Performance";
        public const string kPerfGroup = "Profiling";
        public const string kPerfButtonGroup = "ProfilingAll";

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

        // --- Performance tab: runtime switches for ModProfiler. All default off; while one is
        // on, Logs/ClearTheWay.log gets [perf] lines every 300 frames. Read once per tick by
        // ModProfiler.Refresh, so they can be flipped mid-session for an A/B cut.
        // The options UI has no multi-select or collapsible group, so this is built from what it
        // does have: two buttons that set all 13 toggles, and a toggle that shows/hides them.

        /// <summary>Button: switches every pass below on.</summary>
        [SettingsUIButton]
        [SettingsUIButtonGroup(kPerfButtonGroup)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileAllOn
        {
            set { SetAllProfiled(true); }
        }

        /// <summary>Button: switches every pass below off.</summary>
        [SettingsUIButton]
        [SettingsUIButtonGroup(kPerfButtonGroup)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileAllOff
        {
            set { SetAllProfiled(false); }
        }

        /// <summary>Stands in for an expander: shows the single-pass toggles. Its label carries
        /// the live "n of 13 on" count, so passes left on behind a collapsed list are not missed.</summary>
        [SettingsUIDisplayName(typeof(Setting), nameof(ShowProfiledPassesLabel))]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ShowProfiledPasses { get; set; }

        /// <summary>Time the main corridor pass (ClearTheWaySystem), including everything it calls.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileMainSystem { get; set; }

        /// <summary>Time the second pass before car movement (speed and steering overrides).</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfilePostSystem { get; set; }

        /// <summary>Time the path-end guard.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfilePathEndGuard { get; set; }

        /// <summary>Time the pedestrian hold.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfilePedestrians { get; set; }

        /// <summary>Time the escalation chain per responder.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileEmergencyEscalation { get; set; }

        /// <summary>Time the corridor builder.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileCorridorBuilder { get; set; }

        /// <summary>Time the lane change.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileLaneChange { get; set; }

        /// <summary>Time pulling cars aside.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfilePushVehicles { get; set; }

        /// <summary>Time the oncoming-lane state machine.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileDesperateBehaviour { get; set; }

        /// <summary>Time the arrival assist.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileArrivalAssist { get; set; }

        /// <summary>Time the accident pass (plus its Lifetime/ClearAside/Sweep/Protect sub-timers).</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileAccidentGuard { get; set; }

        /// <summary>Time the recovery-vehicle assist.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileRecoveryAssist { get; set; }

        /// <summary>Time the tow coupling.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(ShowProfiledPasses), true)]
        [SettingsUISection(kPerfSection, kPerfGroup)]
        public bool ProfileTowCoupling { get; set; }

        private void SetAllProfiled(bool on)
        {
            ProfileMainSystem = on;
            ProfilePostSystem = on;
            ProfilePathEndGuard = on;
            ProfilePedestrians = on;
            ProfileEmergencyEscalation = on;
            ProfileCorridorBuilder = on;
            ProfileLaneChange = on;
            ProfilePushVehicles = on;
            ProfileDesperateBehaviour = on;
            ProfileArrivalAssist = on;
            ProfileAccidentGuard = on;
            ProfileRecoveryAssist = on;
            ProfileTowCoupling = on;
            // A plain button only calls the setter; toggles save themselves, this has to.
            ApplyAndSave();
        }

        /// <summary>
        /// Label of <see cref="ShowProfiledPasses"/>: the locale entry is a format string with
        /// {0} = passes on and {1} = passes in total. Re-evaluated by the options UI, so the
        /// count follows the buttons and toggles live.
        /// </summary>
        private LocalizedString ShowProfiledPassesLabel()
        {
            string id = GetOptionLabelLocaleID(nameof(ShowProfiledPasses));
            LocalizationDictionary dictionary = GameManager.instance?.localizationManager?.activeDictionary;
            if (dictionary == null || !dictionary.TryGetValue(id, out string format))
            {
                return LocalizedString.Id(id);
            }
            return LocalizedString.Value(string.Format(format, ModProfiler.CountOn(this), ModProfiler.kPassCount));
        }

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
            ShowProfiledPasses = false;
            ProfileMainSystem = false;
            ProfilePostSystem = false;
            ProfilePathEndGuard = false;
            ProfilePedestrians = false;
            ProfileEmergencyEscalation = false;
            ProfileCorridorBuilder = false;
            ProfileLaneChange = false;
            ProfilePushVehicles = false;
            ProfileDesperateBehaviour = false;
            ProfileArrivalAssist = false;
            ProfileAccidentGuard = false;
            ProfileRecoveryAssist = false;
            ProfileTowCoupling = false;
        }
    }
}

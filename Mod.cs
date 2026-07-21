using ClearTheWay.FunctionGroup.Towing;
using ClearTheWay.FunctionGroup.WayClearance.TrafficLights;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Simulation;

namespace ClearTheWay
{
    public class Mod : IMod
    {
        public static readonly ILog Log = LogManager.GetLogger(nameof(ClearTheWay))
            .SetShowsErrorsInUI(showsErrorsInUI: false);

        public static Setting Setting { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("Traffic: Clear the Way loading");

            Setting = new Setting(this);
            Setting.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(nameof(ClearTheWay), Setting, new Setting(this));

            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Setting));
            GameManager.instance.localizationManager.AddSource("de-DE", new LocaleDE(Setting));

            // Run right before the vanilla car navigation so our lane-position and
            // IgnoreBlocker writes are picked up in the same simulation tick.
            // EXACTLY ONE registration per system: every Update*<T>() call adds T to the phase,
            // so registering the corridor system twice did not give it two ordering constraints
            // but two EXECUTIONS per tick - two contradicting runs (different lane positions, the
            // oncoming state machine flipping 1<->2 in the same frame) that left responders
            // standing for minutes.
            updateSystem.UpdateBefore<ClearTheWaySystem, CarNavigationSystem>(SystemUpdatePhase.GameSimulation);
            // The traffic-light writes are the one thing that must land AFTER the light system
            // (and thus after C2VM Traffic Tool Essentials, which patches in before it), or the
            // emergency Go override is recomputed away in the same frame - the "no effect on
            // green phases" symptom, since a priority petition alone only takes effect at the
            // next phase switch and never preempts a running green. So the corridor pass QUEUES
            // its light decisions and this tiny system applies them here. See GreenLightSystem.
            updateSystem.UpdateAfter<GreenLightSystem, TrafficLightSystem>(SystemUpdatePhase.GameSimulation);
            // Second pass right before the car movement (i.e. after car navigation) so our
            // speed and steering overrides survive CarNavigationSystem's recompute.
            updateSystem.UpdateBefore<ClearTheWayPostSystem, CarMoveSystem>(SystemUpdatePhase.GameSimulation);
            // Between car navigation (which sets EndOfPath|EndReached) and the FIRST vehicle
            // AI (AmbulanceAISystem) - all the AIs that delete at path end (personal cars,
            // delivery/cargo/work trucks, garbage, taxi ...) run after Ambulance, so anchoring
            // before it covers every vehicle type. THIS anchor, not "after CarNavigationSystem":
            // with that one the guard landed behind the truck AIs, which deleted a rig before it
            // was ever scanned. Being before Ambulance also puts it after the navigation, since
            // the navigation runs before every AI - one registration covers both requirements,
            // and registering it twice would only make the pass run twice (see above).
            updateSystem.UpdateBefore<PathEndGuardSystem, AmbulanceAISystem>(SystemUpdatePhase.GameSimulation);

            // Pedestrians: hold them at the kerb while a responder passes. Anchored right
            // before HumanMoveSystem so it lands AFTER HumanNavigationSystem recomputed
            // their speed - a write before navigation would just be overwritten (same
            // reason the car speed overrides sit before CarMoveSystem).
            updateSystem.UpdateBefore<ClearTheWayPedestrianSystem, HumanMoveSystem>(SystemUpdatePhase.GameSimulation);

            // Couple wrecks to arriving recovery trucks as trailers BEFORE the maintenance
            // AI processes them (so vanilla repair-in-place never starts).
            updateSystem.UpdateBefore<TowHookupSystem, MaintenanceVehicleAISystem>(SystemUpdatePhase.GameSimulation);
            // Dispatch assist: nearest suitable van (or the tow depot) gets the wreck order,
            // and responders beeline instead of doing road work en route. Runs before the
            // maintenance AI so it accepts the reordered queue in the same tick.
            updateSystem.UpdateBefore<TowDispatchSystem, MaintenanceVehicleAISystem>(SystemUpdatePhase.GameSimulation);

            // Register the tow depot + tow-truck prefabs before each game load (pre-
            // deserialization, so saves containing placed depots load safely).
            GameManager.instance.onGamePreload += (purpose, mode) =>
            {
                TowTruckAsset.EnsureCreated();
                TowTrailerAsset.EnsureCreated();
                TowDepotAsset.EnsureCreated();
            };

            Log.Info("Traffic: Clear the Way loaded");
        }

        public void OnDispose()
        {
            Log.Info("Traffic: Clear the Way disposed");
            if (Setting != null)
            {
                Setting.UnregisterInOptionsUI();
                Setting = null;
            }
        }
    }
}

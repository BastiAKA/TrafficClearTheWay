using Game;

namespace ClearTheWay.FunctionGroup.WayClearance.TrafficLights
{
    /// <summary>
    /// Writes the traffic-light changes the corridor pass asked for, after the game's own light
    /// systems have run.
    ///
    /// It exists purely for ORDER. The corridor pass has to run before CarNavigationSystem so its
    /// lane-position and IgnoreBlocker writes are read in the same tick; a LaneSignal write from
    /// there, however, is recomputed away by TrafficLightSystem immediately afterwards (the "no
    /// effect on green phases" symptom). Both constraints cannot hold for one system, and the
    /// earlier attempt to have both - registering ClearTheWaySystem a second time, after the light
    /// system - did not add an ordering constraint but a second EXECUTION: the whole escalation ran
    /// twice per tick, the two runs wrote different lane positions and flipped the oncoming state
    /// machine back and forth, and responders wedged for minutes (2026-07-21).
    ///
    /// So the decision stays in the corridor pass (it needs the corridor it just built) and only
    /// the write moves here. Whether this lands before or after the corridor pass within the tick
    /// does not matter: it flushes whatever is queued, so a request is applied in the same tick or
    /// at worst in the next one - and the pass re-requests every tick anyway, because the light
    /// system would recompute the override.
    /// </summary>
    public partial class GreenLightSystem : GameSystemBase
    {
        private ClearTheWaySystem m_MainSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_MainSystem = World.GetOrCreateSystemManaged<ClearTheWaySystem>();
        }

        protected override void OnUpdate()
        {
            m_MainSystem?.Lights?.Flush();
        }
    }
}

namespace ClearTheWay
{
    /// <summary>
    /// How hard the traffic around a responder is being asked to clear out - one ordered value
    /// instead of the pair of bools it grew out of.
    ///
    /// The stages escalate with how long the responder has been stuck behind the SAME blocker, and
    /// each one is strictly further than the last (kEdgeMeters &lt; kEvadeMeters &lt; kDeepEvadeMeters).
    /// As two independent flags that ordering was only a convention, and it was possible to write
    /// "deep but not hard" - a state no code handled. Being ordered also makes the comparisons read
    /// like the thing they test (<c>&gt;= Hard</c>), and it prints straight into the log, where two
    /// bools showed up as a single <c>evade=1</c> that could not tell the last two stages apart.
    /// </summary>
    internal enum EvadeStage
    {
        /// <summary>The ordinary corridor: traffic eases aside within its own lane. What almost
        /// every responder gets for its whole run.</summary>
        Soft = 0,

        /// <summary>Stuck behind the same blocker past kEvadeAfterFrames: cars ahead go partly
        /// onto the kerb, green strip or parking strip.</summary>
        Hard = 1,

        /// <summary>Still stuck past kDeepEvadeAfterFrames (~30 s): cars ahead clear fully onto
        /// the pavement. The standard corridor has demonstrably failed by this point, so the cost
        /// of parking traffic on the kerb is worth paying.</summary>
        Deep = 2,
    }
}

namespace WAR2D.Sim
{
    /// <summary>
    /// Dev-only pause for the tick (the debug API's <c>pause</c>, <c>resume</c> and <c>step</c>). While paused the
    /// boundary still settles the previous tick but the tick doesn't run, unless a step is pending.
    /// </summary>
    public sealed class SimPause
    {
        /// <summary>True to stop the tick.</summary>
        public bool Paused;

        /// <summary>Ticks still allowed to run while paused.</summary>
        public int StepBudget { get; private set; }

        /// <summary>Lets <paramref name="ticks"/> more ticks run while paused.</summary>
        public void Step(int ticks) => StepBudget += System.Math.Max(0, ticks);

        /// <summary>Called once per boundary: true when this tick may run (consuming a step when paused).</summary>
        public bool TryRun()
        {
            if (!Paused) return true;
            if (StepBudget <= 0) return false;
            StepBudget--;
            return true;
        }

        /// <summary>True while paused with no step pending: the building systems skip their frame too.</summary>
        public bool Frozen => Paused && StepBudget <= 0;
    }
}

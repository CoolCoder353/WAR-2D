using System.Diagnostics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Named main-thread timings of the tick boundary's handlers, for the perf gate (<c>Dev/PerfMatch</c> writes each as
    /// <c>perf.split.&lt;name&gt;</c>). Each <see cref="Mark"/> stores the time since the previous mark of the same run.
    /// </summary>
    public static class PerfSplits
    {
        /// <summary>The split names, in the order they are written.</summary>
        public static readonly string[] Names =
        {
            "match.radars", "match.blasts", "match.deaths", "match.squads", "match.unpaid", "match.damageAlerts", "match.alertFlush",
            "repl.routes", "repl.fog", "repl.terrain", "repl.buildings", "repl.interest", "repl.attackTargets", "repl.encode", "repl.send",
            "cmd.work", "cmd.waypoints", "cmd.orders", "cmd.other", "cmd.autoWork", "cmd.alarms",
        };

        /// <summary>Indices of the command stage's splits (<c>cmd.*</c>).</summary>
        public const int CmdWork = 15, CmdWaypoints = 16, CmdOrders = 17, CmdOther = 18, CmdAutoWork = 19, CmdAlarms = 20;

        /// <summary>Milliseconds of the last run of each split, by index into <see cref="Names"/>.</summary>
        public static readonly double[] Ms = new double[Names.Length];

        private static readonly Stopwatch Watch = Stopwatch.StartNew();

        /// <summary>The current time, to start a run of marks.</summary>
        public static double Now => Watch.Elapsed.TotalMilliseconds;

        /// <summary>Stores the time since <paramref name="since"/> under <paramref name="index"/> and moves <paramref name="since"/> to now.</summary>
        public static void Mark(int index, ref double since)
        {
            double now = Now;
            Ms[index] = now - since;
            since = now;
        }
    }
}

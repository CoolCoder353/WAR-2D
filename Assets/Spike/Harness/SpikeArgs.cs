using System;
using System.Globalization;

namespace WAR2D.Spike
{
    /// <summary>
    /// Options for one spike run, read from the command line, e.g.
    /// <c>-spike sim -units 80000 -map 512 -teams 8 -seed 1 -ticks 600 -out SpikeResults -tag sim-80k -quit</c>.
    /// </summary>
    [Serializable]
    public struct SpikeArgs
    {
        public string Bench, OutDir, Tag;
        /// <summary>Flow-field design to benchmark: <c>full</c>, <c>hier</c> or <c>both</c> (flow only).</summary>
        public string Design;
        public int Units, MapSize, Teams, Seed, Ticks, Warmup, VisionRadius, BuildingVision, SliceTicks, CellSize, Clients;
        /// <summary>Percentage of units in the large size class (radius 0.7).</summary>
        public int LargePercent;
        public bool Quit, Async, Clump;

        /// <summary>Defaults from the reference scenario table in the v0.3 plan.</summary>
        public static SpikeArgs Defaults => new SpikeArgs
        {
            Bench = "", OutDir = "SpikeResults", Tag = "", Design = "both",
            Units = 80000, MapSize = 512, Teams = 8, Seed = 1, Ticks = 600, Warmup = 100,
            VisionRadius = 8, BuildingVision = 10, SliceTicks = 4, CellSize = 5, Clients = 8, LargePercent = 10,
        };

        public static SpikeArgs Parse(string[] args)
        {
            SpikeArgs a = Defaults;
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : "";
                switch (args[i])
                {
                    case "-spike": a.Bench = next; break;
                    case "-out": a.OutDir = next; break;
                    case "-tag": a.Tag = next; break;
                    case "-design": a.Design = next; break;
                    case "-units": a.Units = Int(next); break;
                    case "-map": a.MapSize = Int(next); break;
                    case "-teams": a.Teams = Int(next); break;
                    case "-seed": a.Seed = Int(next); break;
                    case "-ticks": a.Ticks = Int(next); break;
                    case "-warmup": a.Warmup = Int(next); break;
                    case "-vision": a.VisionRadius = Int(next); break;
                    case "-bvision": a.BuildingVision = Int(next); break;
                    case "-large": a.LargePercent = Int(next); break;
                    case "-slice": a.SliceTicks = Int(next); break;
                    case "-cell": a.CellSize = Int(next); break;
                    case "-clients": a.Clients = Int(next); break;
                    case "-quit": a.Quit = true; break;
                    case "-async": a.Async = true; break;
                    case "-clump": a.Clump = true; break;
                }
            }
            return a;
        }

        private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}

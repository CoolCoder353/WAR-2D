using System;
using System.Globalization;

namespace WAR2D.Spike
{
    /// <summary>
    /// Options for one spike run, read from the command line, e.g.
    /// <c>-spike sim -units 80000 -map 512 -teams 8 -seed 1 -ticks 600 -out SpikeResults -tag sim-80k -quit</c>.
    /// The sim's own knobs are <c>-fronts</c>, <c>-clump</c>, <c>-async</c>, <c>-sep</c>,
    /// <c>-sepk</c>, <c>-sepiter</c> and <c>-halves</c>.
    /// </summary>
    [Serializable]
    public struct SpikeArgs
    {
        public string Bench, OutDir, Tag;
        /// <summary>
        /// Flow-field design to benchmark (flow only): <c>full</c>, <c>hier</c> (hierarchical, one tile
        /// per cell), <c>hier2</c> (hierarchical, 2x2 tiles per cell), <c>both</c> (full + hier) or
        /// <c>all</c>.
        /// </summary>
        public string Design;

        /// <summary>Fields a tick may rebuild (flow only); 2 is the plan's reference load.</summary>
        public int RebuildCap;
        public int Units, MapSize, Teams, Seed, Ticks, Warmup, VisionRadius, BuildingVision, SliceTicks, CellSize, Clients;
        /// <summary>Render: sampled frames after the warm-up (the plan's protocol is 1,800).</summary>
        public int Frames;
        /// <summary>Render: frames discarded before the sample window (the plan's protocol is 300).</summary>
        public int FrameWarmup;
        /// <summary>Render: own units per scene; the plan's row is 10,000 (mixed adds as many enemies).</summary>
        public int RenderUnits;
        /// <summary>Render scene set: <c>own10k</c>, <c>mixed</c> or <c>both</c>.</summary>
        public string RenderScene;
        /// <summary>Percentage of units in the large size class (radius 0.7).</summary>
        public int LargePercent;
        public bool Quit, Async, Clump;

        /// <summary>Start the simulation's armies already engaged at their shared fronts (sim only).</summary>
        public bool Fronts;
        /// <summary>Ticks between separation passes; 2 is the sim ladder's step 2 (sim only).</summary>
        public int SepInterval;
        /// <summary>Separation strength k, 1 to 4 (sim only).</summary>
        public int SepStrength;
        /// <summary>Separation passes a tick, 1 or 2 (sim only).</summary>
        public int SepIterations;
        /// <summary>Integrate half the units on alternating ticks, the sim ladder's step 3 (sim only).</summary>
        public bool MoveHalves;

        /// <summary>Bandwidth: ticks between correction checks of an in-view unit (0 keeps the plan's 2).</summary>
        public int CorrInterval;
        /// <summary>Bandwidth: tiles of error before an in-view unit is corrected (0 keeps 0.25).</summary>
        public float CorrThreshold;
        /// <summary>Bandwidth: corrections as deltas against the prediction in 1/n tile; 0 is absolute.</summary>
        public int DeltaScale;
        /// <summary>Bandwidth: payload KB/s per client, corrections largest error first; 0 is unlimited.</summary>
        public int BudgetKBps;
        /// <summary>Bandwidth: camera width in tiles (16:9) for the view tier; 0 disables it.</summary>
        public int ViewTiles;
        /// <summary>Bandwidth: error threshold for off-screen units (0 keeps 2 tiles).</summary>
        public float OffThreshold;
        /// <summary>Bandwidth: ticks between checks of an off-screen unit (0 keeps 20).</summary>
        public int OffInterval;
        /// <summary>Bandwidth: corrections carry the unit's measured speed (one byte).</summary>
        public bool SendSpeed;
        /// <summary>Bandwidth: corrections resume at the waypoint after the nearest route segment.</summary>
        public bool ProjectResume;

        /// <summary>Defaults from the reference scenario table in the v0.3 plan.</summary>
        public static SpikeArgs Defaults => new SpikeArgs
        {
            Bench = "", OutDir = "SpikeResults", Tag = "", Design = "both", RebuildCap = 2,
            Units = 80000, MapSize = 512, Teams = 8, Seed = 1, Ticks = 600, Warmup = 100,
            VisionRadius = 8, BuildingVision = 10, SliceTicks = 4, CellSize = 5, Clients = 8, LargePercent = 10,
            Frames = 1800, FrameWarmup = 300, RenderUnits = 10000, RenderScene = "both",
            SepInterval = 1, SepStrength = (int)SpikeSimRules.SeparationStrength, SepIterations = SpikeSimRules.SeparationIterations,
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
                    case "-rebuild": a.RebuildCap = Int(next); break;
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
                    case "-frames": a.Frames = Int(next); break;
                    case "-fwarmup": a.FrameWarmup = Int(next); break;
                    case "-own": a.RenderUnits = Int(next); break;
                    case "-scene": a.RenderScene = next; break;
                    case "-quit": a.Quit = true; break;
                    case "-async": a.Async = true; break;
                    case "-clump": a.Clump = true; break;
                    case "-fronts": a.Fronts = true; break;
                    case "-sep": a.SepInterval = Int(next); break;
                    case "-sepk": a.SepStrength = Int(next); break;
                    case "-sepiter": a.SepIterations = Int(next); break;
                    case "-halves": a.MoveHalves = true; break;
                    case "-corri": a.CorrInterval = Int(next); break;
                    case "-corrt": a.CorrThreshold = Float(next); break;
                    case "-cq": a.DeltaScale = Int(next); break;
                    case "-budget": a.BudgetKBps = Int(next); break;
                    case "-view": a.ViewTiles = Int(next); break;
                    case "-offt": a.OffThreshold = Float(next); break;
                    case "-offi": a.OffInterval = Int(next); break;
                    case "-cspeed": a.SendSpeed = true; break;
                    case "-cproj": a.ProjectResume = true; break;
                }
            }
            return a;
        }

        private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static float Float(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

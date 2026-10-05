using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace WAR2D.Spike
{
    /// <summary>Appends one CSV row per metric to &lt;OutDir&gt;/results.csv.</summary>
    public static class SpikeResults
    {
        private const string Header =
            "utc,bench,tag,metric,unit,units,map,teams,vision,bvision,large,slice,cell,samples,mean,p50,p95,p99,max,cpu,gpu,debugBuild";

        public static void Write(SpikeArgs a, string metric, string unit, SpikeStats s)
        {
            Directory.CreateDirectory(a.OutDir);
            string path = Path.Combine(a.OutDir, "results.csv");
            if (!File.Exists(path)) File.WriteAllText(path, Header + "\n");
            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            string row = string.Join(",",
                DateTime.UtcNow.ToString("o"), a.Bench, a.Tag, metric, unit,
                a.Units, a.MapSize, a.Teams, a.VisionRadius, a.BuildingVision, a.LargePercent, a.SliceTicks, a.CellSize, s.Count,
                F(s.Mean), F(s.Percentile(50)), F(s.Percentile(95)), F(s.Percentile(99)), F(s.Max),
                Quote(SystemInfo.processorType), Quote(SystemInfo.graphicsDeviceName), Debug.isDebugBuild);
            File.AppendAllText(path, row + "\n");
            Debug.Log($"[Spike] {a.Bench}/{metric}: p50 {F(s.Percentile(50))} p95 {F(s.Percentile(95))} max {F(s.Max)} {unit}");
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "'") + "\"";
    }
}

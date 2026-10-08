using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>Sample series with percentiles, written as the gate's CSV (metric, mean, p50, p95, p99, max, budget, pass).</summary>
public sealed class PerfStats
{
    private readonly Dictionary<string, List<double>> series = new Dictionary<string, List<double>>();
    private readonly List<string> order = new List<string>();

    public void Add(string metric, double value)
    {
        if (!series.TryGetValue(metric, out List<double> list))
        {
            series[metric] = list = new List<double>();
            order.Add(metric);
        }
        list.Add(value);
    }

    public int CountOf(string metric) => series.TryGetValue(metric, out List<double> list) ? list.Count : 0;

    public double Mean(string metric)
    {
        if (!series.TryGetValue(metric, out List<double> list) || list.Count == 0) return double.NaN;
        double sum = 0;
        foreach (double v in list) sum += v;
        return sum / list.Count;
    }

    public double Percentile(string metric, double p)
    {
        if (!series.TryGetValue(metric, out List<double> list) || list.Count == 0) return double.NaN;
        var sorted = new List<double>(list);
        sorted.Sort();
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Count - 1, rank))];
    }

    public double Max(string metric) => Percentile(metric, 100);

    /// <summary>
    /// Writes the CSV. <paramref name="budgets"/> maps a metric to (statistic, limit, upper): the gate
    /// compares that statistic ("mean", "p95", "max") against the limit (upper = must be below).
    /// </summary>
    public void WriteCsv(string path, IReadOnlyDictionary<string, (string stat, double limit, bool upper)> budgets)
    {
        var sb = new StringBuilder("metric,samples,mean,p50,p95,p99,max,budget_stat,budget,pass\n");
        foreach (string metric in order)
        {
            string stat = "", limit = "", pass = "";
            if (budgets != null && budgets.TryGetValue(metric, out var b))
            {
                double value = b.stat == "mean" ? Mean(metric) : b.stat == "max" ? Max(metric) : Percentile(metric, 95);
                stat = b.stat;
                limit = b.limit.ToString("0.###", CultureInfo.InvariantCulture);
                pass = (b.upper ? value <= b.limit : value >= b.limit) ? "PASS" : "FAIL";
            }
            sb.AppendLine(string.Join(",", metric, CountOf(metric).ToString(CultureInfo.InvariantCulture),
                F(Mean(metric)), F(Percentile(metric, 50)), F(Percentile(metric, 95)), F(Percentile(metric, 99)), F(Max(metric)), stat, limit, pass));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, sb.ToString());
    }

    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
}

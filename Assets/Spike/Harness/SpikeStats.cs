using System;
using System.Collections.Generic;

namespace WAR2D.Spike
{
    /// <summary>Collects samples (ms, KB/s, fps...) and reports mean, percentiles and max.</summary>
    public sealed class SpikeStats
    {
        private readonly List<double> samples = new List<double>();

        public int Count => samples.Count;

        public void Add(double value) => samples.Add(value);

        public double Mean
        {
            get
            {
                if (samples.Count == 0) return double.NaN;
                double sum = 0;
                foreach (double v in samples) sum += v;
                return sum / samples.Count;
            }
        }

        public double Max
        {
            get
            {
                double max = double.NaN;
                foreach (double v in samples) if (double.IsNaN(max) || v > max) max = v;
                return max;
            }
        }

        /// <summary>Linear-interpolated percentile, p in [0, 100].</summary>
        public double Percentile(double p)
        {
            if (samples.Count == 0) return double.NaN;
            var sorted = new List<double>(samples);
            sorted.Sort();
            double rank = p / 100.0 * (sorted.Count - 1);
            int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
        }
    }
}

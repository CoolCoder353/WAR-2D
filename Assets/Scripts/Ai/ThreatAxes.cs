using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Threat axes (v0.21 §5): for each of its bases (its HQ's region, and forward bases), a score per lane into it. The
    /// predicted part is the lane the shortest way (over the map plan's region graph) from each enemy base it knows comes in
    /// by, weighted by that enemy's remembered army; before it knows one, every start site that isn't its own counts
    /// alike. The observed part is the enemy units it saw come at its buildings, by the lane nearest where they were seen,
    /// halving every <c>AxisHalfLifeSeconds</c>. Insight weighs what it saw against what it predicts.
    /// </summary>
    public sealed class ThreatAxes
    {
        /// <summary>One lane into one of its bases.</summary>
        public struct Axis
        {
            /// <summary>The base's region and the lane (indices into the view's regions and lanes).</summary>
            public int Region, Lane;
            /// <summary>Where the lane leaves the base's region, and the direction out of the base there.</summary>
            public float2 Spot, Dir;
            public float Predicted, Observed, Score;
        }

        /// <summary>The axes at the last <see cref="Update"/>, highest score first.</summary>
        public readonly List<Axis> Axes = new List<Axis>(8);

        private readonly Dictionary<long, float> heat = new Dictionary<long, float>();
        private readonly List<long> keys = new List<long>(16);
        private double lastDecay = double.NaN;
        private readonly HashSet<long> counted = new HashSet<long>();

        // Region graph scratch.
        private float[] dist = System.Array.Empty<float>();
        private int[] via = System.Array.Empty<int>();
        private bool[] done = System.Array.Empty<bool>();

        private static long Key(int region, int lane) => ((long)region << 32) | (uint)lane;

        /// <summary>The axis to face first: the top-scoring one's spot, or false when it has none (a scene map, no lanes).</summary>
        public bool TryTop(out Axis top)
        {
            top = default;
            if (Axes.Count == 0) return false;
            top = Axes[0];
            return true;
        }

        /// <summary>Each think: decays what it saw, and counts new threats at its bases by the lane they came from.</summary>
        public void Observe(AiContext ai, IReadOnlyList<int> bases)
        {
            TeamView view = ai.View;
            if (view.Lanes.Length == 0) return;
            double now = ai.Now;
            if (!double.IsNaN(lastDecay) && now > lastDecay && heat.Count > 0)
            {
                float f = math.pow(0.5f, (float)((now - lastDecay) / math.max(1f, ai.Config.Warpath.AxisHalfLifeSeconds)));
                keys.Clear();
                keys.AddRange(heat.Keys);
                foreach (long k in keys) heat[k] *= f;
            }
            lastDecay = now;
            foreach (KeyValuePair<int, Threat> t in ai.Board.Threats)
            {
                Threat threat = t.Value;
                long seen = ((long)threat.BuildingId << 20) ^ (long)(threat.FirstSeen * 16.0);
                if (!counted.Add(seen)) continue;
                int region = ai.State.RegionAt(threat.Near, 8f);
                if (region < 0 || !Contains(bases, region)) continue;
                int lane = NearestLane(view, region, threat.Position);
                if (lane < 0) continue;
                long k = Key(region, lane);
                heat.TryGetValue(k, out float h);
                heat[k] = h + threat.Count;
            }
            if (counted.Count > 4096) counted.Clear();
        }

        /// <summary>Recomputes the axes of <paramref name="bases"/> (regions) and scores them.</summary>
        public void Update(AiContext ai, IReadOnlyList<int> bases)
        {
            Axes.Clear();
            TeamView view = ai.View;
            if (view.Lanes.Length == 0 || view.Regions.Length == 0) return;
            float defenceDistance = ai.Config.Play.DefenceDistance;
            foreach (int region in bases)
            {
                if (region < 0 || region >= view.Regions.Length) continue;
                AiRegion home = view.Regions[region];
                for (int l = 0; l < view.Lanes.Length; l++)
                {
                    AiLane lane = view.Lanes[l];
                    int other = lane.A == region ? lane.B : lane.B == region ? lane.A : -1;
                    if (other < 0 || other >= view.Regions.Length) continue;
                    float2 dir = math.normalizesafe((float2)(view.Regions[other].Centre - home.Centre));
                    heat.TryGetValue(Key(region, l), out float observed);
                    Axes.Add(new Axis { Region = region, Lane = l, Spot = (float2)home.Centre + dir * (home.Radius + defenceDistance), Dir = dir, Observed = observed });
                }
            }
            if (Axes.Count == 0) return;

            // Predicted: from each enemy base it knows (else each start site not its own), the lane its shortest way comes in by.
            bool any = false;
            foreach (EnemyBase b in ai.Board.EnemyBases.Values)
            {
                if (!view.IsEnemy(b.OwnerId)) continue;
                float weight = 1f;
                if (ai.Board.Armies.TryGetValue(b.OwnerId, out ArmySighting army)) weight += StrengthEstimate.Of(ai.Game, army) / 100f;
                Predict(ai, b.Anchor, weight);
                any = true;
            }
            if (!any)
            {
                int own = ai.State.RegionAt(ai.State.Home);
                int n = 0;
                foreach (int2 site in view.HqSites) if (ai.State.RegionAt(site) != own) n++;
                foreach (int2 site in view.HqSites) if (ai.State.RegionAt(site) != own) Predict(ai, site, 1f / math.max(1, n));
            }

            float maxP = 0f, maxO = 0f;
            foreach (Axis a in Axes) { maxP = math.max(maxP, a.Predicted); maxO = math.max(maxO, a.Observed); }
            float seen = maxO > 0f ? math.saturate(ai.Difficulty.Insight) : 0f;
            for (int i = 0; i < Axes.Count; i++)
            {
                Axis a = Axes[i];
                a.Score = (1f - seen) * (maxP > 0f ? a.Predicted / maxP : 0f) + seen * (maxO > 0f ? a.Observed / maxO : 0f);
                Axes[i] = a;
            }
            Axes.Sort((x, y) => y.Score.CompareTo(x.Score));
        }

        /// <summary>Adds <paramref name="weight"/> to the axis the shortest region-graph way from <paramref name="from"/> enters a base by.</summary>
        private void Predict(AiContext ai, int2 from, float weight)
        {
            TeamView view = ai.View;
            int start = ai.State.RegionAt(from, 16f);
            if (start < 0) return;
            int n = view.Regions.Length;
            if (dist.Length != n) { dist = new float[n]; via = new int[n]; done = new bool[n]; }
            for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; via[i] = -1; done[i] = false; }
            dist[start] = 0f;
            for (int step = 0; step < n; step++)
            {
                int u = -1;
                float best = float.MaxValue;
                for (int i = 0; i < n; i++) if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
                if (u < 0) break;
                done[u] = true;
                // A base reached: the lane it came in by takes the weight; ways on through a base don't count again.
                if (u != start && AxisOf(u, via[u], out int index))
                {
                    Axis a = Axes[index];
                    a.Predicted += weight;
                    Axes[index] = a;
                    continue;
                }
                for (int l = 0; l < view.Lanes.Length; l++)
                {
                    AiLane lane = view.Lanes[l];
                    int v = lane.A == u ? lane.B : lane.B == u ? lane.A : -1;
                    if (v < 0 || v >= n || done[v]) continue;
                    float d = best + math.distance((float2)view.Regions[u].Centre, (float2)view.Regions[v].Centre);
                    if (d < dist[v]) { dist[v] = d; via[v] = l; }
                }
            }
        }

        private bool AxisOf(int region, int lane, out int index)
        {
            for (int i = 0; i < Axes.Count; i++)
                if (Axes[i].Region == region && Axes[i].Lane == lane) { index = i; return true; }
            index = -1;
            return false;
        }

        private static int NearestLane(TeamView view, int region, float2 at)
        {
            AiRegion home = view.Regions[region];
            float2 to = math.normalizesafe(at - (float2)home.Centre);
            int best = -1;
            float bestDot = -2f;
            for (int l = 0; l < view.Lanes.Length; l++)
            {
                AiLane lane = view.Lanes[l];
                int other = lane.A == region ? lane.B : lane.B == region ? lane.A : -1;
                if (other < 0 || other >= view.Regions.Length) continue;
                float dot = math.dot(to, math.normalizesafe((float2)(view.Regions[other].Centre - home.Centre)));
                if (dot > bestDot) { bestDot = dot; best = l; }
            }
            return best;
        }

        private static bool Contains(IReadOnlyList<int> list, int value)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == value) return true;
            return false;
        }
    }
}

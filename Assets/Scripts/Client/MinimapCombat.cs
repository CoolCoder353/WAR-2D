using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Client
{
    /// <summary>
    /// Fights on the minimap (v0.16, Figma C): a known unit or building whose health drops reports a hit; hits are binned into
    /// cells, each cell pulses once for <c>PulseSeconds</c> and then stays quiet for <c>QuietSeconds</c>. A pulse is "own"
    /// (the alert colour) when any of its hits was on the local player's things. Built only from health the client already
    /// gets for every unit and building it knows, so it shows nothing it couldn't already see.
    /// </summary>
    public sealed class MinimapCombat
    {
        /// <summary>One pulse: its cell, where it is drawn, whether the player's own things were hit, and when it started.</summary>
        public struct Pulse { public int2 Cell; public float2 Centre; public bool Own; public float Start; }

        /// <summary>The local client's pulses (replaced when a client world starts).</summary>
        public static MinimapCombat Current { get; set; }

        private readonly int cellTiles, max;
        private readonly float pulseSeconds, quietSeconds;
        private readonly List<Pulse> pulses = new List<Pulse>();
        private readonly Dictionary<int2, float> quietUntil = new Dictionary<int2, float>();

        /// <summary>Pulses showing now.</summary>
        public IReadOnlyList<Pulse> Pulses => pulses;

        /// <summary>Seconds a pulse lasts.</summary>
        public float PulseSeconds => pulseSeconds;

        public MinimapCombat(int cellTiles, float pulseSeconds, float quietSeconds, int max)
        {
            this.cellTiles = math.max(1, cellTiles); this.pulseSeconds = pulseSeconds; this.quietSeconds = quietSeconds; this.max = max;
        }

        /// <summary>A hit at <paramref name="position"/>; <paramref name="own"/> when the thing hit is the local player's.</summary>
        public void Hit(float2 position, bool own, float now)
        {
            int2 cell = (int2)math.floor(position / cellTiles);
            for (int i = 0; i < pulses.Count; i++)
            {
                if (!pulses[i].Cell.Equals(cell)) continue;
                if (own && !pulses[i].Own) { Pulse p = pulses[i]; p.Own = true; pulses[i] = p; }
                return;
            }
            if (quietUntil.TryGetValue(cell, out float until) && now < until) return;
            if (pulses.Count >= max) return;
            pulses.Add(new Pulse { Cell = cell, Centre = ((float2)cell + 0.5f) * cellTiles, Own = own, Start = now });
            quietUntil[cell] = now + pulseSeconds + quietSeconds;
        }

        /// <summary>Drops finished pulses.</summary>
        public void Update(float now)
        {
            for (int i = pulses.Count - 1; i >= 0; i--)
                if (now - pulses[i].Start >= pulseSeconds) pulses.RemoveAt(i);
        }
    }
}

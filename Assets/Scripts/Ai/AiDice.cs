using System.Collections.Generic;

namespace WAR2D.Ai
{
    /// <summary>A key decision that can slip (AI overhaul §2.3).</summary>
    public enum AiSlip : byte
    {
        /// <summary>A producer isn't refilled this think.</summary>
        Production,
        /// <summary>One purchase skips the upkeep check.</summary>
        Upkeep,
        /// <summary>An attack starts without the advantage it wants.</summary>
        AttackTiming,
        /// <summary>One retreat check is missed.</summary>
        Retreat,
        /// <summary>The second-best target is picked.</summary>
        Target,
    }

    /// <summary>
    /// One brain's random stream (AI overhaul §2.3, §6.1), seeded per seat: slips, estimate noise, the attention roll and
    /// the per-match variation. Seed 0 (tests) still rolls (from a fixed stream) but <see cref="Varies"/> is false.
    /// </summary>
    public sealed class AiDice
    {
        private Unity.Mathematics.Random random;
        private readonly float mistakes, slipSeconds;
        private readonly Dictionary<AiSlip, double> nextSlipRoll = new Dictionary<AiSlip, double>();

        /// <param name="seed">The seat's variation seed (0: no variation).</param>
        /// <param name="mistakes">The difficulty's chance (0..1) that a slip roll slips.</param>
        /// <param name="slipSeconds">A slip of one kind is rolled at most once this often.</param>
        public AiDice(uint seed, float mistakes, float slipSeconds)
        {
            Seed = seed;
            random = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            this.mistakes = mistakes;
            this.slipSeconds = slipSeconds;
        }

        public uint Seed { get; }

        /// <summary>True when the brain's style numbers are varied (a non-zero seed).</summary>
        public bool Varies => Seed != 0;

        /// <summary>Slips so far (dev reads).</summary>
        public int Slips { get; private set; }

        /// <summary>A number in [0, 1).</summary>
        public float Next() => random.NextFloat();

        /// <summary>A number in [<paramref name="min"/>, <paramref name="max"/>).</summary>
        public float Range(float min, float max) => random.NextFloat(min, max);

        /// <summary>An index in [0, <paramref name="count"/>).</summary>
        public int Index(int count) => count <= 1 ? 0 : random.NextInt(count);

        /// <summary>
        /// True when this kind of decision slips now: rolled at most once per <c>SlipSeconds</c> per kind (the first ask in
        /// a window rolls; later asks in it are false), with the difficulty's <c>Mistakes</c> chance.
        /// </summary>
        public bool Slip(AiSlip kind, double now)
        {
            if (mistakes <= 0f) return false;
            if (nextSlipRoll.TryGetValue(kind, out double next) && now < next) return false;
            nextSlipRoll[kind] = now + slipSeconds;
            bool slip = Next() < mistakes;
            if (slip) Slips++;
            return slip;
        }

        /// <summary>A factor in 1 ± (1 − <paramref name="insight"/>) × <paramref name="amount"/>: how far off an estimate is.</summary>
        public float Noise(float insight, float amount)
        {
            float spread = (1f - insight) * amount;
            return spread <= 0f ? 1f : 1f + Range(-spread, spread);
        }
    }
}

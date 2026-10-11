namespace WAR2D.Art
{
    /// <summary>A small deterministic RNG (xorshift32), so generated art never depends on the runtime's Random.</summary>
    public struct ArtRandom
    {
        private uint state;

        public ArtRandom(int seed, string salt)
        {
            uint h = 2166136261u; // FNV-1a over the salt, mixed with the seed
            foreach (char c in salt) h = (h ^ c) * 16777619u;
            state = h ^ (uint)seed * 2654435761u;
            if (state == 0) state = 0x9E3779B9u;
        }

        public uint Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        /// <summary>0 ≤ value &lt; <paramref name="max"/>.</summary>
        public int Range(int max) => (int)(Next() % (uint)max);

        /// <summary>True with probability <paramref name="p"/>.</summary>
        public bool Chance(float p) => Next() % 10000 < p * 10000f;
    }
}

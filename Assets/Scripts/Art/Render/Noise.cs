using System;

namespace WAR2D.Art.Render
{
    /// <summary>
    /// Deterministic value noise. <see cref="Periodic"/> repeats every <c>period</c> units in x and y, so a
    /// tile textured with it joins its neighbours seamlessly.
    /// </summary>
    public static class Noise
    {
        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        private static float Smooth(float t) => t * t * (3 - 2 * t);

        /// <summary>Value noise in [0, 1] repeating every <paramref name="period"/> lattice cells.</summary>
        public static float Periodic(float x, float y, int period, int seed)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = Smooth(x - x0), fy = Smooth(y - y0);
            int Wrap(int v) => ((v % period) + period) % period;
            float a = Hash(Wrap(x0), Wrap(y0), seed), b = Hash(Wrap(x0 + 1), Wrap(y0), seed);
            float c = Hash(Wrap(x0), Wrap(y0 + 1), seed), d = Hash(Wrap(x0 + 1), Wrap(y0 + 1), seed);
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }

        /// <summary>
        /// Fractal noise in about [0, 1] over a square of side <paramref name="size"/> (in pixels) that tiles:
        /// <paramref name="cells"/> lattice cells across at the first octave, doubling per octave.
        /// </summary>
        public static float Fbm(float x, float y, float size, int cells, int octaves, int seed)
        {
            float sum = 0, amp = 0.5f, norm = 0;
            for (int o = 0; o < octaves; o++)
            {
                int period = cells << o;
                sum += amp * Periodic(x / size * period, y / size * period, period, seed + o * 101);
                norm += amp;
                amp *= 0.5f;
            }
            return sum / norm;
        }
    }
}

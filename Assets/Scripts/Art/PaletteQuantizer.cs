using System;
using UnityEngine;

namespace WAR2D.Art
{
    /// <summary>
    /// Cleans an image (AI image tool output, method prototype (b)) down to the art palette and size: every
    /// source pixel is mapped to its nearest opaque palette colour in OKLab (or to transparent below the alpha
    /// cutoff), then each destination pixel takes the most common result in its box of source pixels (ties go
    /// to the lower palette index). Deterministic; rows are bottom-up, as Unity textures.
    /// </summary>
    public static class PaletteQuantizer
    {
        public static Color32[] Quantize(Color32[] src, int srcW, int srcH, int dstW, int dstH, Color32[] palette, byte alphaCutoff = 128)
        {
            if (src == null || src.Length != srcW * srcH) throw new ArgumentException("src must be srcW × srcH");
            if (dstW <= 0 || dstH <= 0 || dstW > srcW || dstH > srcH) throw new ArgumentException("the destination must be no bigger than the source");
            if (palette == null || palette.Length == 0) throw new ArgumentException("empty palette");

            int transparent = Array.FindIndex(palette, c => c.a == 0);
            Color32 clear = transparent >= 0 ? palette[transparent] : new Color32(0, 0, 0, 0);
            var labs = new Vector3[palette.Length];
            for (int i = 0; i < palette.Length; i++) labs[i] = OkLab(palette[i]);

            // Nearest palette index per source pixel; -1 = transparent.
            var nearest = new int[src.Length];
            for (int i = 0; i < src.Length; i++) nearest[i] = src[i].a < alphaCutoff ? -1 : Nearest(OkLab(src[i]), palette, labs);

            var votes = new int[palette.Length + 1]; // slot 0 = transparent, slot i + 1 = palette index i
            var dst = new Color32[dstW * dstH];
            for (int dy = 0; dy < dstH; dy++)
            for (int dx = 0; dx < dstW; dx++)
            {
                Array.Clear(votes, 0, votes.Length);
                int x0 = dx * srcW / dstW, x1 = (dx + 1) * srcW / dstW;
                int y0 = dy * srcH / dstH, y1 = (dy + 1) * srcH / dstH;
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    votes[nearest[y * srcW + x] + 1]++;
                int best = 0;
                for (int v = 1; v < votes.Length; v++) if (votes[v] > votes[best]) best = v;
                dst[dy * dstW + dx] = best == 0 ? clear : palette[best - 1];
            }
            return dst;
        }

        private static int Nearest(Vector3 lab, Color32[] palette, Vector3[] labs)
        {
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                if (palette[i].a == 0) continue; // opaque pixels never become transparent
                float d = (labs[i] - lab).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }

        /// <summary>sRGB to OKLab (Björn Ottosson, 2020).</summary>
        public static Vector3 OkLab(Color32 c)
        {
            float r = Linear(c.r), g = Linear(c.g), b = Linear(c.b);
            float l = Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
            float m = Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
            float s = Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
            return new Vector3(
                0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
        }

        private static float Linear(byte channel)
        {
            float v = channel / 255f;
            return v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        private static float Cbrt(float v) => v < 0f ? -Mathf.Pow(-v, 1f / 3f) : Mathf.Pow(v, 1f / 3f);
    }
}

using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art.Style;

namespace WAR2D.Art.Render
{
    /// <summary>
    /// Turns a lit render into pixel art (owner, 2026-10-11: "more pixel art"): downsample by majority vote
    /// onto a limited palette (shade ramps of the style's colours, no blending), then a 1 px dark outline
    /// around sprites. Deterministic.
    /// </summary>
    public static class PixelFinish
    {
        /// <summary>Shades per colour in the pixel palette (dark to light).</summary>
        public static readonly float[] Shades = { 0.55f, 0.75f, 1.0f, 1.25f, 1.55f };

        /// <summary>Extra base colours the renderer uses outside the style roles (sandbags, drums, crates).</summary>
        public static readonly Color[] Extras =
        {
            new Color(0.42f, 0.39f, 0.32f), new Color(0.55f, 0.18f, 0.14f), new Color(0.42f, 0.33f, 0.22f), Color.white,
        };

        /// <summary>The limited palette: every style role (and the extras, and the team colour) in <see cref="Shades"/>, plus transparent.</summary>
        public static Color32[] Palette(StylePalette style, Color team)
        {
            var colours = new List<Color32> { new Color32(0, 0, 0, 0) };
            void Ramp(Color c)
            {
                foreach (float s in Shades)
                    colours.Add(new Color(Mathf.Clamp01(c.r * s), Mathf.Clamp01(c.g * s), Mathf.Clamp01(c.b * s), 1f));
            }
            for (int i = StylePalette.Outline; i < StylePalette.Team0; i++) Ramp(style.Colours[i]);
            foreach (Color c in Extras) Ramp(c);
            Ramp(team);
            colours.Add(style.Colours[StylePalette.Outline]);
            return colours.ToArray();
        }

        /// <summary>
        /// Downsamples <paramref name="src"/> (<paramref name="w"/> × <paramref name="h"/>) by
        /// <paramref name="factor"/> onto <paramref name="palette"/>; with <paramref name="outline"/>, every
        /// transparent pixel next to an opaque one becomes the outline colour.
        /// </summary>
        public static Color32[] Finish(Color32[] src, int w, int h, int factor, Color32[] palette, Color32? outline)
        {
            int dw = w / factor, dh = h / factor;
            Color32[] dst = PaletteQuantizer.Quantize(src, w, h, dw, dh, palette, alphaCutoff: 140);
            if (outline == null) return dst;
            var result = (Color32[])dst.Clone();
            bool Opaque(int x, int y) => (uint)x < (uint)dw && (uint)y < (uint)dh && dst[y * dw + x].a > 0;
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
                if (!Opaque(x, y) && (Opaque(x - 1, y) || Opaque(x + 1, y) || Opaque(x, y - 1) || Opaque(x, y + 1)))
                    result[y * dw + x] = outline.Value;
            return result;
        }

        /// <summary>
        /// Cleanup after quantising (asset-generation spec §4.4): an opaque pixel whose colour none of its 8 neighbours share
        /// takes the commonest colour among its opaque neighbours (the first found on a tie, scanning from the lower left).
        /// One pass, read from the input, so the result doesn't depend on scan order.
        /// </summary>
        public static Color32[] Cleanup(Color32[] src, int w, int h)
        {
            var dst = (Color32[])src.Clone();
            var seen = new Color32[8];
            var count = new int[8];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color32 c = src[y * w + x];
                if (c.a != 255) continue;
                int kinds = 0;
                bool shared = false;
                for (int dy = -1; dy <= 1 && !shared; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    Color32 n = src[ny * w + nx];
                    if (n.a != 255) continue;
                    if (n.r == c.r && n.g == c.g && n.b == c.b) { shared = true; break; }
                    int k = 0;
                    while (k < kinds && (seen[k].r != n.r || seen[k].g != n.g || seen[k].b != n.b)) k++;
                    if (k == kinds) { seen[kinds] = n; count[kinds++] = 0; }
                    count[k]++;
                }
                if (shared || kinds == 0) continue;
                int best = 0;
                for (int k = 1; k < kinds; k++) if (count[k] > count[best]) best = k;
                dst[y * w + x] = seen[best];
            }
            return dst;
        }

        /// <summary>
        /// The atlas finish (style guide): <paramref name="team"/> pixels snap to <paramref name="teamRamp"/> and
        /// the rest to <paramref name="palette"/>, then the outline and a baked hard shadow
        /// (<see cref="ArtStyle.ShadowAlpha"/> black, <see cref="ArtStyle.ShadowOffset"/> px right and down).
        /// Without <paramref name="outline"/> (terrain) there is no outline and no shadow.
        /// </summary>
        /// <param name="cleanup">Finish 2: run <see cref="Cleanup"/> after quantising, before the outline and shadow.</param>
        public static Color32[] FinishTeam(Color32[] lit, bool[] team, int w, int h, Color32[] palette, Color32[] teamRamp, Color32? outline,
            bool cleanup = false)
        {
            Color32[] rest = PaletteQuantizer.Quantize(lit, w, h, w, h, palette, alphaCutoff: 140);
            Color32[] keyed = PaletteQuantizer.Quantize(lit, w, h, w, h, teamRamp, alphaCutoff: 140);
            var dst = new Color32[w * h];
            for (int i = 0; i < dst.Length; i++) dst[i] = team[i] ? keyed[i] : rest[i];
            if (cleanup) dst = Cleanup(dst, w, h);
            if (outline == null) return dst;

            bool Opaque(Color32[] px, int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h && px[y * w + x].a == 255;
            var outlined = (Color32[])dst.Clone();
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (!Opaque(dst, x, y) && (Opaque(dst, x - 1, y) || Opaque(dst, x + 1, y) || Opaque(dst, x, y - 1) || Opaque(dst, x, y + 1)))
                    outlined[y * w + x] = outline.Value;
            var result = (Color32[])outlined.Clone();
            int d = ArtStyle.ShadowOffset;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (result[y * w + x].a == 0 && Opaque(outlined, x - d, y + d))
                    result[y * w + x] = new Color32(0, 0, 0, ArtStyle.ShadowAlpha);
            return result;
        }
    }
}

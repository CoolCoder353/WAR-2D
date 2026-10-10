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
    }
}

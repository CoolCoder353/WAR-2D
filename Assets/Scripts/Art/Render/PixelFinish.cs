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
        /// The atlas finish (style guide): <paramref name="team"/> pixels snap to <paramref name="teamRamp"/> and
        /// the rest to <paramref name="palette"/>, then the outline and a baked hard shadow
        /// (<see cref="ArtStyle.ShadowAlpha"/> black, <see cref="ArtStyle.ShadowOffset"/> px right and down).
        /// Without <paramref name="outline"/> (terrain) there is no outline and no shadow.
        /// </summary>
        public static Color32[] FinishTeam(Color32[] lit, bool[] team, int w, int h, Color32[] palette, Color32[] teamRamp, Color32? outline)
        {
            Color32[] rest = PaletteQuantizer.Quantize(lit, w, h, w, h, palette, alphaCutoff: 140);
            Color32[] keyed = PaletteQuantizer.Quantize(lit, w, h, w, h, teamRamp, alphaCutoff: 140);
            var dst = new Color32[w * h];
            for (int i = 0; i < dst.Length; i++) dst[i] = team[i] ? keyed[i] : rest[i];
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

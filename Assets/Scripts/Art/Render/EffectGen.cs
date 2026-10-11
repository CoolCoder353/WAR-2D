using System;
using UnityEngine;
using WAR2D.Art.Style;

namespace WAR2D.Art.Render
{
    /// <summary>A finished effect frame: pixels in <see cref="ArtStyle.EffectPalette"/> (team parts in the team key), y up.</summary>
    public readonly struct EffectFrame
    {
        public readonly Color32[] Pixels;
        public readonly int Width, Height;

        public EffectFrame(Color32[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Effect and overlay frames (plan v0.8 Task 11), drawn straight in palette colours: fire from
    /// <see cref="ArtStyle.FireRamp"/>, smoke and debris from the machine and rock greys, fades by ordered
    /// dithering (no partial alpha, so they stay pixel art). Deterministic.
    /// </summary>
    public static class EffectGen
    {
        private const int T = RenderGen.TileSize;

        private static Color32 Role(byte role, int shade = 2)
        {
            Color c = StylePalettes.Charcoal.Colours[role];
            float s = PixelFinish.Shades[shade];
            return new Color(Mathf.Clamp01(c.r * s), Mathf.Clamp01(c.g * s), Mathf.Clamp01(c.b * s), 1f);
        }

        private static Color32 Outline => StylePalettes.Charcoal.Colours[StylePalette.Outline];

        /// <summary>4×4 ordered-dither threshold in (0, 1) for an art pixel.</summary>
        public static float Bayer(int x, int y)
        {
            int[] m = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
            return (m[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f;
        }

        private sealed class Img
        {
            public readonly Color32[] Px;
            public readonly int W, H;
            public Img(int w, int h) { W = w; H = h; Px = new Color32[w * h]; }
            public void Set(int x, int y, Color32 c) { if ((uint)x < (uint)W && (uint)y < (uint)H) Px[y * W + x] = c; }
            public Color32 Get(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H ? Px[y * W + x] : default;
            public EffectFrame Done() => new EffectFrame(Px, W, H);

            /// <summary>Every transparent pixel next to an opaque one becomes <paramref name="c"/>.</summary>
            public void OutlineWith(Color32 c)
            {
                var copy = (Color32[])Px.Clone();
                bool Opaque(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H && copy[y * W + x].a > 0;
                for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (!Opaque(x, y) && (Opaque(x - 1, y) || Opaque(x + 1, y) || Opaque(x, y - 1) || Opaque(x, y + 1))) Px[y * W + x] = c;
            }
        }

        /// <summary>
        /// A blast <paramref name="tiles"/> square at <paramref name="t"/> (0 = flash, 1 = last wisps): a
        /// noisy fireball cooling through the fire ramp, then dithered smoke and a few embers.
        /// </summary>
        public static EffectFrame Blast(int tiles, float t, int seed)
        {
            int s = tiles * T;
            var img = new Img(s, s);
            float c = s / 2f, fireR = s * (0.14f + 0.26f * Mathf.Sqrt(t)), smokeR = s * (0.22f + 0.24f * t);
            float heat = Mathf.Clamp01(1.15f - t * 1.6f), smoke = t < 0.15f ? 0f : Mathf.Clamp01(1.2f - t) * Mathf.Clamp01(t * 3f);
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
                float n = Noise.Fbm(x, y, s, 3, 3, seed) - 0.5f;
                float fire = (1f - d / fireR + n * 0.7f) * heat * 1.7f;
                if (fire > 0.12f)
                {
                    int i = fire > 1.1f ? 0 : fire > 0.9f ? 1 : fire > 0.72f ? 2 : fire > 0.55f ? 3 : fire > 0.4f ? 4 : fire > 0.26f ? 5 : 6;
                    img.Set(x, y, ArtStyle.FireRamp[i]);
                    continue;
                }
                float puff = (1f - d / smokeR + n * 0.9f) * smoke * 1.6f;
                if (puff > Bayer(x / 2, y / 2))
                    img.Set(x, y, Noise.Fbm(x, y, s, 4, 2, seed + 7) > 0.5f ? Role(StylePalette.Body2, 1) : Role(StylePalette.Body1, 1));
            }
            // Embers flung out while it burns down.
            if (t > 0.2f && t < 0.9f)
            {
                var rng = new ArtRandom(seed, "embers");
                for (int k = 0; k < 6 * tiles; k++)
                {
                    float a = rng.Range(360) * Mathf.Deg2Rad, r = s * (0.18f + 0.3f * t) * (0.7f + rng.Range(60) / 100f);
                    int ex = (int)(c + Mathf.Cos(a) * r), ey = (int)(c + Mathf.Sin(a) * r);
                    Color32 ember = ArtStyle.FireRamp[2 + Mathf.Min(4, (int)(t * 5))];
                    img.Set(ex, ey, ember);
                    img.Set(ex + 1, ey, ember);
                }
            }
            return img.Done();
        }

        /// <summary>A muzzle flash: a hot star, smaller in frame 1.</summary>
        public static EffectFrame MuzzleFlash(int frame)
        {
            var img = new Img(T, T);
            int c = T / 2, core = frame == 0 ? 3 : 2, ray = frame == 0 ? 10 : 6;
            for (int y = -core; y <= core; y++)
            for (int x = -core; x <= core; x++)
                if (x * x + y * y <= core * core) img.Set(c + x, c + y, ArtStyle.FireRamp[0]);
            for (int d = 0; d < 8; d++)
            {
                float a = d * Mathf.PI / 4f;
                int len = d % 2 == 0 ? ray : ray * 2 / 3;
                for (int r = core; r <= len; r++)
                    img.Set(c + Mathf.RoundToInt(Mathf.Cos(a) * r), c + Mathf.RoundToInt(Mathf.Sin(a) * r), ArtStyle.FireRamp[r < len * 2 / 3 ? 1 : 2]);
            }
            return img.Done();
        }

        /// <summary>A tracer streak along +x through the centre (rotated to its heading when drawn).</summary>
        public static EffectFrame Tracer()
        {
            var img = new Img(T, T);
            int c = T / 2;
            for (int x = c - 9; x <= c + 9; x++)
            {
                Color32 col = x > c + 4 ? ArtStyle.FireRamp[0] : x > c - 3 ? ArtStyle.FireRamp[1] : ArtStyle.FireRamp[2];
                img.Set(x, c, col);
                if (x > c - 3) img.Set(x, c - 1, col);
            }
            return img.Done();
        }

        /// <summary>The ring under a selected unit: four light arcs with a dark edge.</summary>
        public static EffectFrame SelectionRingSmall()
        {
            var img = new Img(T, T);
            float c = T / 2f;
            Color32 ring = Role(StylePalette.Lamp);
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Repeat(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg, 90f);
                if (d >= 26.5f && d <= 28.5f && a > 12f && a < 78f) img.Set(x, y, ring);
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>Corner brackets for a selected building, drawn 9-sliced (borders <see cref="RingBorder"/>) over its footprint.</summary>
        public static EffectFrame SelectionRingLarge()
        {
            int s = 2 * T;
            var img = new Img(s, s);
            Color32 ring = Role(StylePalette.Lamp);
            const int inset = 3, arm = 16, w = 2;
            for (int i = 0; i < arm; i++)
            for (int k = 0; k < w; k++)
            foreach (var (x0, y0, sx, sy) in new[] { (inset, inset, 1, 1), (s - 1 - inset, inset, -1, 1), (inset, s - 1 - inset, 1, -1), (s - 1 - inset, s - 1 - inset, -1, -1) })
            {
                img.Set(x0 + sx * i, y0 + sy * k, ring);
                img.Set(x0 + sx * k, y0 + sy * i, ring);
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>The 9-slice border of <see cref="SelectionRingLarge"/> in art pixels (the brackets fit inside it).</summary>
        public const int RingBorder = 24;

        /// <summary>A flag on a pole; <paramref name="team"/> draws the cloth in the team key (rally flags).</summary>
        public static EffectFrame Flag(int frame, bool team)
        {
            var img = new Img(T, T);
            int px = 26, bottom = 14, top = 46;
            for (int y = bottom; y <= top; y++) { img.Set(px, y, Role(StylePalette.Body3, 3)); img.Set(px + 1, y, Role(StylePalette.Body3, 2)); }
            for (int x = px - 2; x <= px + 3; x++) img.Set(x, bottom - 1, Role(StylePalette.Body1));
            for (int x = 0; x < 16; x++)
            {
                int wave = Mathf.RoundToInt(Mathf.Sin((x + frame * 4) * 0.45f) * 1.5f);
                int h = 10 - x * 6 / 16;
                for (int y = 0; y < h; y++)
                {
                    // Lit top edge, shaded bottom edge (Lamp shade 1 isn't in the palette: too close to a player grey).
                    int step = y < 2 ? 3 : y > h - 3 ? 0 : 2;
                    img.Set(px + 2 + x, top - 1 - y + wave, team ? ArtStyle.TeamKeyRamp[step + 1] : Role(StylePalette.Lamp, step));
                }
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>Rock chunks flung out of a dug tile.</summary>
        public static EffectFrame DigDebris(int frame)
        {
            var img = new Img(T, T);
            var rng = new ArtRandom(3, "debris");
            float t = (frame + 1) / 4f;
            for (int k = 0; k < 9; k++)
            {
                float a = rng.Range(360) * Mathf.Deg2Rad, r = 6 + t * (14 + rng.Range(10));
                int x = (int)(T / 2 + Mathf.Cos(a) * r), y = (int)(T / 2 + Mathf.Sin(a) * r - t * t * 6);
                int size = 1 + rng.Range(2);
                for (int j = 0; j <= size; j++)
                for (int i = 0; i <= size; i++)
                    img.Set(x + i, y + j, Role(i + j == 0 ? StylePalette.Rock3 : StylePalette.Rock1, 2));
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>Welding sparks for repairs.</summary>
        public static EffectFrame RepairSparks(int frame)
        {
            var img = new Img(T, T);
            var rng = new ArtRandom(5 + frame, "sparks");
            for (int k = 0; k < 10; k++)
            {
                float a = rng.Range(360) * Mathf.Deg2Rad, r = 3 + rng.Range(18);
                int x = (int)(T / 2 + Mathf.Cos(a) * r), y = (int)(T / 2 + Mathf.Sin(a) * r);
                img.Set(x, y, ArtStyle.FireRamp[r < 8 ? 0 : r < 14 ? 1 : 2]);
            }
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                img.Set(T / 2 + x, T / 2 + y, ArtStyle.FireRamp[0]);
            return img.Done();
        }

        /// <summary>Diagonal hazard stripes, half dithered, scrolling with the frame (tiles seamlessly).</summary>
        public static EffectFrame ConstructionOverlay(int frame)
        {
            var img = new Img(T, T);
            Color32 warn = Role(StylePalette.Warn, 1), dark = Role(StylePalette.Body0);
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                if (Bayer(x, y) > 0.5f) continue;
                img.Set(x, y, ((x + y + frame * 4) / 8 & 1) == 0 ? warn : dark);
            }
            return img.Done();
        }

        /// <summary>Health bar parts (frame 0 the back, frame 1 the white fill), <c>48×4</c> art pixels centred in a tile.</summary>
        public static EffectFrame HealthBar(int frame)
        {
            var img = new Img(T, T);
            const int w = 48, h = 4;
            int x0 = (T - w) / 2, y0 = (T - h) / 2;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                if (frame == 0) img.Set(x0 + x, y0 + y, edge ? Outline : Role(StylePalette.Body0));
                else if (!edge) img.Set(x0 + x, y0 + y, new Color32(255, 255, 255, 255));
            }
            return img.Done();
        }

        /// <summary>The frame for cell <paramref name="cell"/> of an effect id, or null when it has no generator.</summary>
        public static EffectFrame? For(string id, int cell, int frames)
        {
            float t = frames > 1 ? (float)cell / (frames - 1) : 0f;
            switch (id)
            {
                case "Explosion": return Blast(2, t, 11);
                case "BombBlast": return Blast(3, t, 23);
                case "MuzzleFlash": return MuzzleFlash(cell);
                case "Tracer": return Tracer();
                case "SelectionRingSmall": return SelectionRingSmall();
                case "SelectionRingLarge": return SelectionRingLarge();
                case "WaypointFlag": return Flag(cell, false);
                case "RallyFlag": return Flag(cell, true);
                case "DigDebris": return DigDebris(cell);
                case "RepairSparks": return RepairSparks(cell);
                case "ConstructionOverlay": return ConstructionOverlay(cell);
                case "HealthBar": return HealthBar(cell);
                default: return null;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art.Render;
using WAR2D.Art.Style;

namespace WAR2D.Art
{
    /// <summary>
    /// The style guide's values in code (<c>docs/art/style-guide.md</c>): tile size, pixels per unit, facings,
    /// the shipped pixel palette and the team key ramp. <c>Assets/Art/palette.png</c> is
    /// <see cref="Palette"/> at 1 px per colour.
    /// </summary>
    public static class ArtStyle
    {
        /// <summary>Art pixels per tile edge (one tile is one world unit).</summary>
        public const int TileSize = RenderGen.TileSize;

        /// <summary>The pixels-per-unit of every sprite and atlas.</summary>
        public const int PixelsPerUnit = TileSize;

        /// <summary>Rendered unit facings, 45° apart (units never rotate as quads).</summary>
        public const int DirectionCount = ArtManifest.UnitDirections;

        /// <summary>Animation rate of every sprite.</summary>
        public const int FramesPerSecond = 10;

        /// <summary>Palette colours closer than this (CIE76 ΔE) to any player colour in any palette mode are left out.</summary>
        public const float PlayerClearance = 10f;

        /// <summary>
        /// The team key in source art, darkest first: team parts render in these magentas (the
        /// <see cref="PixelFinish.Shades"/> of one key colour) and the importer turns them into the mask; the
        /// shader shades the owner's colour by step.
        /// </summary>
        public static readonly Color32[] TeamKeyRamp = Ramp(new Color(0.64f, 0f, 0.64f));

        /// <summary>
        /// What each team key step becomes in the colour atlas: a grey the shader multiplies by the owner's
        /// colour (the brightest step is white), so the step survives as brightness.
        /// </summary>
        public static readonly Color32[] TeamGrey = Greys();

        /// <summary>Alpha of the baked hard shadow (black, 45 %: the samples' 0.55 darkening).</summary>
        public const byte ShadowAlpha = 115;

        /// <summary>Shadow offset in art pixels: right and down, away from the top-left light.</summary>
        public const int ShadowOffset = 4;

        /// <summary>
        /// Splits source art into the colour atlas and the team mask: team key pixels become their
        /// <see cref="TeamGrey"/> step and get mask 255; every other pixel is unchanged with mask 0.
        /// </summary>
        public static Color32[] SplitTeamKey(Color32[] source, out Color32[] mask)
        {
            var colour = (Color32[])source.Clone();
            mask = new Color32[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                int step = Array.IndexOf(TeamKeyRamp, source[i]);
                if (step < 0)
                {
                    mask[i] = new Color32(0, 0, 0, 255);
                    continue;
                }
                colour[i] = TeamGrey[step];
                mask[i] = new Color32(255, 255, 255, 255);
            }
            return colour;
        }

        private static Color32[] Greys()
        {
            float[] shades = PixelFinish.Shades;
            var greys = new Color32[shades.Length];
            for (int i = 0; i < greys.Length; i++)
            {
                byte v = (byte)Mathf.RoundToInt(255f * shades[i] / shades[shades.Length - 1]);
                greys[i] = new Color32(v, v, v, 255);
            }
            return greys;
        }

        /// <summary>
        /// The shipped palette: the Charcoal roles and the renderer's extra colours in
        /// <see cref="PixelFinish.Shades"/>, without duplicates and without colours within
        /// <see cref="PlayerClearance"/> of a player colour; transparent first, the outline last.
        /// </summary>
        public static readonly Color32[] Palette = Build();

        private static Color32[] Build()
        {
            var colours = new List<Color32> { new Color32(0, 0, 0, 0) };
            var players = PlayerColours();
            StylePalette style = StylePalettes.Charcoal;
            var bases = new List<Color>();
            for (int i = StylePalette.Outline; i < StylePalette.Team0; i++) bases.Add(style.Colours[i]);
            bases.AddRange(PixelFinish.Extras);
            foreach (Color b in bases)
            foreach (Color32 c in Ramp(b))
            {
                if (colours.Contains(c)) continue;
                bool clashes = false;
                foreach (Color32 p in players) clashes |= DeltaE(c, p) < PlayerClearance;
                if (!clashes) colours.Add(c);
            }
            Color32 outline = style.Colours[StylePalette.Outline];
            colours.Remove(outline);
            colours.Add(outline);
            return colours.ToArray();
        }

        /// <summary>Every player colour in every palette mode.</summary>
        public static List<Color32> PlayerColours()
        {
            var list = new List<Color32>();
            foreach (string mode in Palettes.Names)
                for (int i = 0; i < 8; i++) list.Add(Palettes.Player(mode, i));
            return list;
        }

        private static Color32[] Ramp(Color c)
        {
            var ramp = new Color32[PixelFinish.Shades.Length];
            for (int i = 0; i < ramp.Length; i++)
            {
                float s = PixelFinish.Shades[i];
                ramp[i] = new Color(Mathf.Clamp01(c.r * s), Mathf.Clamp01(c.g * s), Mathf.Clamp01(c.b * s), 1f);
            }
            return ramp;
        }

        /// <summary>CIE76 colour difference (Euclidean distance in CIELAB, D65).</summary>
        public static float DeltaE(Color32 a, Color32 b)
        {
            Vector3 la = Lab(a), lb = Lab(b);
            return Vector3.Distance(la, lb);
        }

        private static Vector3 Lab(Color32 c)
        {
            static float Linear(byte v)
            {
                float u = v / 255f;
                return u > 0.04045f ? Mathf.Pow((u + 0.055f) / 1.055f, 2.4f) : u / 12.92f;
            }
            static float F(float t) => t > 0.008856f ? Mathf.Pow(t, 1f / 3f) : 7.787f * t + 16f / 116f;
            float r = Linear(c.r), g = Linear(c.g), b = Linear(c.b);
            float x = (0.4124f * r + 0.3576f * g + 0.1805f * b) / 0.95047f;
            float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            float z = (0.0193f * r + 0.1192f * g + 0.9505f * b) / 1.08883f;
            float fx = F(x), fy = F(y), fz = F(z);
            return new Vector3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }
    }
}

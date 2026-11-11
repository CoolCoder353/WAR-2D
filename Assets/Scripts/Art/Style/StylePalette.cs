using System;
using UnityEngine;

namespace WAR2D.Art.Style
{
    /// <summary>
    /// A named set of colours for the style generators, by role. The generators draw with role indices, so a
    /// palette swap restyles every sprite. The team ramp is the magenta key the importer turns into a mask;
    /// <see cref="WithTeam"/> fills it with a player colour for previews.
    /// </summary>
    public sealed class StylePalette
    {
        // Role indices (into Colours).
        public const byte Clear = 0, Outline = 1, Body0 = 2, Body1 = 3, Body2 = 4, Body3 = 5, Lamp = 6;
        public const byte Floor0 = 7, Floor1 = 8, Floor2 = 9, Rock0 = 10, Rock1 = 11, Rock2 = 12, Rock3 = 13;
        public const byte Gem0 = 14, Gem1 = 15, Gem2 = 16, Gem3 = 17, Warn = 18;
        public const byte Team0 = 19, Team1 = 20, Team2 = 21, Team3 = 22;
        public const int Roles = 23;

        /// <summary>Role names, in index order (for swatches and the style guide).</summary>
        public static readonly string[] RoleNames =
        {
            "Clear", "Outline", "Body 0", "Body 1", "Body 2", "Body 3", "Lamp", "Floor 0", "Floor 1", "Floor 2",
            "Rock 0", "Rock 1", "Rock 2", "Rock 3", "Gem 0", "Gem 1", "Gem 2", "Gem 3", "Warn", "Team 0", "Team 1", "Team 2", "Team 3",
        };

        /// <summary>The team key ramp in source art: pure magentas, darkest first.</summary>
        public static readonly Color32[] TeamKey = { Hex(0x400040), Hex(0x800080), Hex(0xC000C0), Hex(0xFF00FF) };

        public string Name { get; }
        public string Description { get; }
        public Color32[] Colours { get; }

        /// <param name="rgb">The colours for roles Outline … Warn (18 values); Clear and the team key are added.</param>
        public StylePalette(string name, string description, params int[] rgb)
        {
            if (rgb.Length != Team0 - 1) throw new ArgumentException($"{name}: {Team0 - 1} colours, Outline to Warn");
            Name = name;
            Description = description;
            Colours = new Color32[Roles];
            Colours[Clear] = new Color32(0, 0, 0, 0);
            for (int i = 0; i < rgb.Length; i++) Colours[1 + i] = Hex(rgb[i]);
            for (int i = 0; i < TeamKey.Length; i++) Colours[Team0 + i] = TeamKey[i];
        }

        /// <summary>A copy with the team ramp shaded from <paramref name="player"/> (as the team shader will).</summary>
        public Color32[] WithTeam(Color32 player)
        {
            var colours = (Color32[])Colours.Clone();
            float[] shade = { 0.45f, 0.65f, 0.85f, 1.0f };
            for (int i = 0; i < 4; i++)
            {
                float s = shade[i];
                // The top step lifts toward white a little so the highlight reads on dark players too.
                float lift = i == 3 ? 0.18f : 0f;
                colours[Team0 + i] = new Color32(
                    (byte)Mathf.Min(255, player.r * s + (255 - player.r) * lift),
                    (byte)Mathf.Min(255, player.g * s + (255 - player.g) * lift),
                    (byte)Mathf.Min(255, player.b * s + (255 - player.b) * lift), 255);
            }
            return colours;
        }

        public static Color32 Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);

        public static string ToHex(Color32 c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";
    }

    /// <summary>The candidate palettes, all in the current placeholder art's style (owner request, 2026-10-11).</summary>
    public static class StylePalettes
    {
        //                                 Outline   Body0     Body1     Body2     Body3     Lamp      Floor0    Floor1    Floor2    Rock0     Rock1     Rock2     Rock3     Gem0      Gem1      Gem2      Gem3      Warn
        public static readonly StylePalette Charcoal = new StylePalette("Charcoal",
            "The current placeholders, cleaned up: neutral charcoal machines and stone, red gems.",
            0x121314, 0x2A2C2E, 0x3A3D40, 0x4D5155, 0x6A6F74, 0xE8ECEF, 0x26282A, 0x2D2F31, 0x383A3D, 0x161718, 0x3A3D40, 0x44474B, 0x4C5054, 0x7A1414, 0xC62828, 0xF0524A, 0xFFC2B8, 0xF2C14E);

        public static readonly StylePalette Gunmetal = new StylePalette("Gunmetal",
            "Cooler blue-grey metal and slate, cyan gems: matches the v0.6 UI's steel panels.",
            0x0E1216, 0x252C33, 0x34404A, 0x485663, 0x6B7D8C, 0xDDF3FF, 0x222A32, 0x28313A, 0x333E49, 0x12171C, 0x36424E, 0x3F4C59, 0x475563, 0x0E5A63, 0x1FA6B0, 0x5FE3E6, 0xD6FFFF, 0xF2C14E);

        public static readonly StylePalette Olive = new StylePalette("Olive drab",
            "Military olive machines on dusty brown-grey ground, amber gems.",
            0x15140F, 0x33362A, 0x464A38, 0x5C6249, 0x7C8463, 0xF1EBD0, 0x302C25, 0x37332B, 0x433E35, 0x1C1A16, 0x4A4438, 0x544D40, 0x5D5647, 0x7A4A0C, 0xD08A1E, 0xF6BE4A, 0xFFE9B0, 0xE8E2B0);

        public static readonly StylePalette Rust = new StylePalette("Rust",
            "Warm brown-grey industrial with oxidised metal, teal gems for contrast.",
            0x150F0D, 0x382C27, 0x4C3B33, 0x644D41, 0x87695A, 0xFFE8D6, 0x2C2522, 0x332B27, 0x3F3631, 0x1A1513, 0x4A3C35, 0x54453D, 0x5D4D44, 0x0E5E55, 0x1F9E8C, 0x5FE0C8, 0xD8FFF4, 0xF2A33A);

        public static readonly StylePalette NightOps = new StylePalette("Night ops",
            "High contrast: near-black ground and rock, lighter machines that pop, yellow gems.",
            0x060708, 0x40454B, 0x585E66, 0x737A83, 0x9AA2AB, 0xFFFFFF, 0x111315, 0x15171A, 0x1E2125, 0x050606, 0x2A2D31, 0x32363A, 0x3A3E43, 0x8A6A00, 0xE0B000, 0xFFE04A, 0xFFF6C4, 0xFF6B4A);

        public static readonly StylePalette[] All = { Charcoal, Gunmetal, Olive, Rust, NightOps };
    }
}

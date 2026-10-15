using System.Collections.Generic;
using UnityEngine;

namespace WAR2D.Art
{
    /// <summary>
    /// The approved v0.8 pixel icons and cursors (Figma page <c>v0.8 New Paint</c>, <c>docs/ux/v0.8-figma.md</c>):
    /// 16×16 bitmaps where <c>#</c> is the body, <c>+</c> the accent and <c>.</c> empty, with a 1 px dark outline
    /// added around every lit pixel. Built at runtime as point-filtered textures (no mipmaps), so they stay crisp
    /// at the UI's whole-number scales. Icon bodies are white so a USS tint colours them by button state.
    /// </summary>
    public static class PixelIcons
    {
        public const int Size = 16;

        /// <summary>The outline colour (the palette's outline role).</summary>
        public static readonly Color32 Outline = new Color32(0x11, 0x16, 0x1C, 255);

        private static readonly Dictionary<string, string[]> Bitmaps = new Dictionary<string, string[]>
        {
            ["IconMove"] = new[]
            {
                "................",
                "................",
                ".......#........",
                ".......##.......",
                ".......###......",
                ".......#####....",
                ".############...",
                ".#############..",
                ".#############..",
                ".############...",
                ".......#####....",
                ".......###......",
                ".......##.......",
                ".......#........",
                "................",
                "................",
            },
            ["IconAttackMove"] = new[]
            {
                "................",
                ".......##.......",
                ".......##.......",
                ".....######.....",
                "....##.##.##....",
                "...##......##...",
                "...#........#...",
                ".####..++..####.",
                ".####..++..####.",
                "...#........#...",
                "...##......##...",
                "....##.##.##....",
                ".....######.....",
                ".......##.......",
                ".......##.......",
                "................",
            },
            ["IconStop"] = new[]
            {
                "................",
                "................",
                "................",
                "................",
                "....########....",
                "....########....",
                "....########....",
                "....########....",
                "....########....",
                "....########....",
                "....########....",
                "....########....",
                "................",
                "................",
                "................",
                "................",
            },
            ["IconHold"] = new[]
            {
                "................",
                "................",
                "...##########...",
                "...##########...",
                "...###++++###...",
                "...###++++###...",
                "...##########...",
                "...##########...",
                "...##########...",
                "....########....",
                "....########....",
                ".....######.....",
                "......####......",
                ".......##.......",
                "................",
                "................",
            },
            ["IconMiner"] = new[]
            {
                "................",
                "................",
                "................",
                ".....##++++.....",
                "....###+++++....",
                "...###+++++++...",
                "..++++++++++++..",
                "..++++++++++++..",
                "...++++++++++...",
                "....++++++++....",
                ".....++++++.....",
                "......++++......",
                ".......++.......",
                "................",
                "................",
                "................",
            },
            ["IconSpawner"] = new[]
            {
                "................",
                "................",
                "..##............",
                "..##............",
                "..##...#...#....",
                "..##..##..##....",
                "..##.###.###....",
                "..##########....",
                "..##############",
                "..##############",
                "..###++++#######",
                "..###++++#######",
                "..###++++#######",
                "..##############",
                "................",
                "................",
            },
            ["IconWall"] = new[]
            {
                "................",
                "................",
                "..############..",
                "..#####.######..",
                "..#####.######..",
                "................",
                "..##.######.##..",
                "..##.######.##..",
                "................",
                "..#####.######..",
                "..#####.######..",
                "................",
                "..##.######.##..",
                "..##.######.##..",
                "................",
                "................",
            },
            ["IconRadar"] = new[]
            {
                "................",
                "..#####.........",
                ".#######........",
                "##.....##.......",
                "#..###..#..++...",
                "#..###..#.++....",
                "##.###.##++.....",
                ".#######++......",
                "..#####.+.......",
                ".....#..........",
                ".....#..........",
                "....###.........",
                "...#####........",
                "..#######.......",
                "................",
                "................",
            },
            ["IconDig"] = new[]
            {
                "................",
                "......####......",
                ".....######.....",
                "....##.##.##....",
                "...##..##..##...",
                "..##...##...##..",
                "......####......",
                ".......##.......",
                ".......##.......",
                ".......##.......",
                ".......##.......",
                ".......##.......",
                "....++++++++....",
                "...++++++++++...",
                "................",
                "................",
            },
            ["IconCancel"] = new[]
            {
                "................",
                "..##........##..",
                "..###......###..",
                "...###....###...",
                "....###..###....",
                ".....######.....",
                "......####......",
                ".......##.......",
                "......####......",
                ".....######.....",
                "....###..###....",
                "...###....###...",
                "..###......###..",
                "..##........##..",
                "................",
                "................",
            },
            ["CursorDefault"] = new[]
            {
                "#...............",
                "##..............",
                "###.............",
                "####............",
                "#####...........",
                "######..........",
                "#######.........",
                "########........",
                "#########.......",
                "######..........",
                "##.###..........",
                "#...###.........",
                "....###.........",
                ".....###........",
                ".....##.........",
                "................",
            },
            ["CursorAttack"] = new[]
            {
                ".......++.......",
                ".......++.......",
                ".......++.......",
                ".....++++++.....",
                "....++....++....",
                "...++......++...",
                "...+........+...",
                "++++........++++",
                "++++........++++",
                "...+........+...",
                "...++......++...",
                "....++....++....",
                ".....++++++.....",
                ".......++.......",
                ".......++.......",
                ".......++.......",
            },
            ["CursorMove"] = new[]
            {
                "................",
                "................",
                ".......#........",
                "......###.......",
                ".....#####......",
                "....###.###.....",
                "...###...###....",
                "..###.....###...",
                "...###...###....",
                "....###.###.....",
                ".....#####......",
                "......###.......",
                ".......#........",
                "................",
                "................",
                "................",
            },
            ["CursorInvalid"] = new[]
            {
                "................",
                ".....++++++.....",
                "....++++++++....",
                "...++......++...",
                "..++......++++..",
                ".++......+++.++.",
                ".++.....+++..++.",
                ".++....+++...++.",
                ".++...+++....++.",
                ".++..+++.....++.",
                ".++.+++......++.",
                "..++++......++..",
                "...++......++...",
                "....++++++++....",
                ".....++++++.....",
                "................",
            },
            ["IconRallyFlag"] = new[]
            {
                "................",
                "...#............",
                "...#++++++......",
                "...#+++++++++...",
                "...#++++++++....",
                "...#+++++++.....",
                "...#+++++.......",
                "...#............",
                "...#............",
                "...#............",
                "...#............",
                "...#............",
                "..###...........",
                ".#####..........",
                "................",
                "................",
            },
        };

        private static readonly Dictionary<string, (Color32 body, Color32 accent)> Colours = new Dictionary<string, (Color32, Color32)>
        {
            ["IconAttackMove"] = (White, White),
            ["IconHold"] = (White, new Color32(0x2F, 0x3A, 0x46, 255)),
            ["IconMiner"] = (new Color32(0xFF, 0x8A, 0x9C, 255), new Color32(0xE0, 0x40, 0x5A, 255)),
            ["IconSpawner"] = (White, new Color32(0xF2, 0xC1, 0x4E, 255)),
            ["IconRallyFlag"] = (White, new Color32(0x4F, 0xC3, 0xF7, 255)),
            ["CursorAttack"] = (Cursor, new Color32(0xE0, 0x40, 0x5A, 255)),
            ["CursorInvalid"] = (Cursor, new Color32(0xE0, 0x40, 0x5A, 255)),
        };

        private static Color32 White => new Color32(255, 255, 255, 255);
        private static Color32 Cursor => new Color32(0xF2, 0xF5, 0xF8, 255);

        private static readonly Dictionary<(string, int), Texture2D> Cache = new Dictionary<(string, int), Texture2D>();

        /// <summary>Every icon and cursor id.</summary>
        public static IEnumerable<string> Ids => Bitmaps.Keys;

        /// <summary>True when there is a bitmap for <paramref name="id"/>.</summary>
        public static bool Contains(string id) => Bitmaps.ContainsKey(id);

        /// <summary>The bitmap rows of <paramref name="id"/>, top row first.</summary>
        public static string[] Bitmap(string id) => Bitmaps[id];

        /// <summary>The pixels of <paramref name="id"/> at <paramref name="scale"/>× (bottom row first, as textures store them).</summary>
        public static Color32[] Pixels(string id, int scale = 1)
        {
            string[] rows = Bitmaps[id];
            (Color32 body, Color32 accent) = Colours.TryGetValue(id, out var c) ? c : (id.StartsWith("Cursor") ? Cursor : White, White);
            int n = Size * scale;
            var pixels = new Color32[n * n];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                Color32 colour = default;
                char ch = rows[y][x];
                if (ch == '#') colour = body;
                else if (ch == '+') colour = accent;
                else if (Lit(rows, x, y)) colour = Outline;
                for (int sy = 0; sy < scale; sy++)
                for (int sx = 0; sx < scale; sx++)
                    pixels[((Size - 1 - y) * scale + sy) * n + x * scale + sx] = colour;
            }
            return pixels;
        }

        /// <summary>A cached, point-filtered texture of <paramref name="id"/> at <paramref name="scale"/>×; null when unknown.</summary>
        public static Texture2D Texture(string id, int scale = 1)
        {
            if (!Bitmaps.ContainsKey(id)) return null;
            if (Cache.TryGetValue((id, scale), out Texture2D cached) && cached != null) return cached;
            int n = Size * scale;
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = id };
            texture.SetPixels32(Pixels(id, scale));
            texture.Apply(false, false); // stays readable: cursors need it
            Cache[(id, scale)] = texture;
            return texture;
        }

        /// <summary>True when an 8-neighbour of empty pixel (x, y) is lit (so it takes the outline).</summary>
        private static bool Lit(string[] rows, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx != 0 || dy != 0) && nx >= 0 && ny >= 0 && nx < Size && ny < Size && rows[ny][nx] != '.') return true;
            }
            return false;
        }
    }
}

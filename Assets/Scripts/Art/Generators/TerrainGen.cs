using UnityEngine;
using static WAR2D.Art.Generators.PrototypeGenerators;

namespace WAR2D.Art.Generators
{
    /// <summary>
    /// The terrain tiles: floor and gems in random variants, rock walls in one variant per 4-neighbour mask
    /// (bit set = that neighbour is also rock: N 1, E 2, S 4, W 8), and the border's solid rock.
    /// </summary>
    public static class TerrainGen
    {
        public const int North = 1, East = 2, South = 4, West = 8;

        /// <summary>Floor: a mid tone with sparse dark and light specks and the odd pebble.</summary>
        public sealed class Ground : ISpriteGenerator
        {
            public string Id => nameof(TileType.Ground);

            public PixelCanvas[] Generate(int seed)
            {
                ArtEntry entry = Entry(Id);
                var frames = new PixelCanvas[entry.Frames];
                for (int v = 0; v < frames.Length; v++)
                {
                    var rng = new ArtRandom(seed, Id + v);
                    PixelCanvas c = Canvas(entry);
                    c.Rect(0, 0, c.Width, c.Height, Floor1);
                    Speckle(c, ref rng, Floor0, 0.08f);
                    Speckle(c, ref rng, Floor2, 0.06f);
                    for (int p = rng.Range(2); p >= 0; p--)
                    {
                        int x = 2 + rng.Range(c.Width - 4), y = 2 + rng.Range(c.Height - 4);
                        c.Set(x, y, Rock1);
                        c.Set(x + 1, y, Rock0);
                    }
                    frames[v] = c;
                }
                return frames;
            }
        }

        /// <summary>Rock, with a lit rim on sides open to the north and west and a shadowed rim to the south and east.</summary>
        public sealed class Wall : ISpriteGenerator
        {
            public string Id => nameof(TileType.Wall);

            public PixelCanvas[] Generate(int seed)
            {
                ArtEntry entry = Entry(Id);
                var frames = new PixelCanvas[entry.Frames];
                for (int mask = 0; mask < frames.Length; mask++) frames[mask] = Rock(entry, seed, mask, Id);
                return frames;
            }
        }

        /// <summary>Rock (as a fully surrounded wall) with gem crystals set into it.</summary>
        public sealed class Gem : ISpriteGenerator
        {
            public string Id => nameof(TileType.Gem);

            public PixelCanvas[] Generate(int seed)
            {
                ArtEntry entry = Entry(Id);
                var frames = new PixelCanvas[entry.Frames];
                for (int v = 0; v < frames.Length; v++)
                {
                    PixelCanvas c = Rock(entry, seed, North | East | South | West, Id + v);
                    var rng = new ArtRandom(seed, Id + "crystals" + v);
                    int crystals = 2 + rng.Range(2);
                    for (int k = 0; k < crystals; k++) Crystal(c, 3 + rng.Range(c.Width - 6), 3 + rng.Range(c.Height - 6), 1 + rng.Range(2));
                    frames[v] = c;
                }
                return frames;
            }

            private static void Crystal(PixelCanvas c, int x, int y, int size)
            {
                for (int dy = -size; dy <= size; dy++)
                for (int dx = -size; dx <= size; dx++)
                {
                    int d = Mathf.Abs(dx) + Mathf.Abs(dy);
                    if (d > size) continue;
                    c.Set(x + dx, y + dy, d == size ? Gem0 : dx < 0 || dy > 0 ? Gem2 : Gem1);
                }
                c.Set(x - 1 + (size > 1 ? 0 : 1), y + (size > 1 ? 1 : 0), Gem3); // glint
            }
        }

        /// <summary>The map's outer ring: darker rock with no rim.</summary>
        public sealed class Border : ISpriteGenerator
        {
            public string Id => nameof(TileType.Border);

            public PixelCanvas[] Generate(int seed)
            {
                ArtEntry entry = Entry(Id);
                var rng = new ArtRandom(seed, Id);
                PixelCanvas c = Canvas(entry);
                c.Rect(0, 0, c.Width, c.Height, Rock0);
                Speckle(c, ref rng, Outline, 0.1f);
                Speckle(c, ref rng, Rock1, 0.05f);
                return new[] { c };
            }
        }

        private static PixelCanvas Rock(ArtEntry entry, int seed, int mask, string salt)
        {
            var rng = new ArtRandom(seed, salt + mask);
            PixelCanvas c = Canvas(entry);
            int w = c.Width, h = c.Height;
            c.Rect(0, 0, w, h, Rock1);
            Speckle(c, ref rng, Rock0, 0.12f);
            Speckle(c, ref rng, Rock2, 0.1f);
            // Rims where the neighbour is open floor (rows are bottom-up: y = h - 1 is the north edge).
            if ((mask & North) == 0) { c.Rect(0, h - 2, w, 2, Rock2); c.Rect(0, h - 1, w, 1, Rock3); }
            if ((mask & West) == 0) { c.Rect(0, 0, 2, h, Rock2); c.Rect(0, 0, 1, h, Rock3); }
            if ((mask & South) == 0) { c.Rect(0, 0, w, 2, Rock0); c.Rect(0, 0, w, 1, Outline); }
            if ((mask & East) == 0) { c.Rect(w - 2, 0, 2, h, Rock0); c.Rect(w - 1, 0, 1, h, Outline); }
            // Round the outside corners where both sides are open: the floor shows through.
            if ((mask & (North | West)) == 0) Corner(c, 0, h - 1, 1, -1);
            if ((mask & (North | East)) == 0) Corner(c, w - 1, h - 1, -1, -1);
            if ((mask & (South | West)) == 0) Corner(c, 0, 0, 1, 1);
            if ((mask & (South | East)) == 0) Corner(c, w - 1, 0, -1, 1);
            return c;
        }

        private static void Corner(PixelCanvas c, int x, int y, int sx, int sy)
        {
            c.Set(x, y, Floor1);
            c.Set(x + sx, y, Floor1);
            c.Set(x, y + sy, Floor1);
        }

        private static void Speckle(PixelCanvas c, ref ArtRandom rng, byte index, float chance)
        {
            for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++)
                if (rng.Chance(chance)) c.Set(x, y, index);
        }
    }
}

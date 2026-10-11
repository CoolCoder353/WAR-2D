using System;
using WAR2D.Art;
using WAR2D.Art.Render;
using WAR2D.World;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// Which atlas frame each map tile draws: its <see cref="TileType"/> and a variant (the autotile mask of its
    /// rock neighbours, or a random pick from a hash of its position). <see cref="MapView"/> writes these into
    /// an RG8 index texture (R = type, G = variant) that the terrain shader reads.
    /// </summary>
    public static class TileAutotile
    {
        /// <summary>Random variants of floor and gem tiles.</summary>
        public const int RandomVariants = 4;

        /// <summary>
        /// The 4-neighbour mask (<see cref="RenderGen.North"/>, East, South, West) of the neighbours for which
        /// <paramref name="same"/> is true. <paramref name="same"/> is asked about off-map tiles too.
        /// </summary>
        public static byte Mask4(Func<int, int, bool> same, int x, int y)
        {
            int mask = 0;
            if (same(x, y + 1)) mask |= RenderGen.North;
            if (same(x + 1, y)) mask |= RenderGen.East;
            if (same(x, y - 1)) mask |= RenderGen.South;
            if (same(x - 1, y)) mask |= RenderGen.West;
            return (byte)mask;
        }

        /// <summary>A deterministic hash of a tile position in [0, <paramref name="count"/>).</summary>
        public static int Hash(int x, int y, int count)
        {
            unchecked
            {
                uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                return (int)(h % (uint)count);
            }
        }

        /// <summary>True for rock-like tiles (walls, gems, the border); off the map counts as rock, so the edge reads as solid.</summary>
        public static bool Solid(MapGrid grid, int x, int y)
        {
            if ((uint)x >= (uint)grid.Width || (uint)y >= (uint)grid.Height) return true;
            return grid.Tiles[y * grid.Width + x] != (byte)TileType.Ground;
        }

        /// <summary>The variant of tile (x, y): its frame within its type's sprite in the <c>Tiles</c> atlas.</summary>
        public static byte Variant(MapGrid grid, int x, int y)
        {
            var type = (TileType)grid.Tiles[y * grid.Width + x];
            switch (type)
            {
                case TileType.Wall:
                    return Mask4((tx, ty) => Solid(grid, tx, ty), x, y);
                case TileType.Gem:
                    // Variant-major, as ArtBuild lays the gem frames out: 4 variants × 16 masks.
                    return (byte)(Hash(x, y, RandomVariants) * ArtManifest.AutotileVariants + Mask4((tx, ty) => Solid(grid, tx, ty), x, y));
                case TileType.Ground:
                    return (byte)Hash(x, y, RandomVariants);
                default:
                    return 0;
            }
        }
    }
}

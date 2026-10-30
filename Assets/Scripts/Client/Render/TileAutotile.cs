using System;
using WAR2D.Art;
using WAR2D.Art.Render;
using WAR2D.World;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// Which atlas frame each map tile draws: its <see cref="TileType"/> and a variant (the autotile mask of its
    /// rock neighbours, or a random pick from a hash of its position). <see cref="MapView"/> writes these into
    /// an index texture (R = type, G = variant, B = a player wall's owner colour) that the terrain shader reads.
    /// </summary>
    public static class TileAutotile
    {
        /// <summary>Random variants of gem tiles.</summary>
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

        /// <summary>
        /// True for rock-like tiles (walls, gems, the border, player walls); off the map counts as rock, so the edge
        /// reads as solid. A wall blueprint is floor: it is see-through and walkable.
        /// </summary>
        public static bool Solid(MapGrid grid, int x, int y)
        {
            if ((uint)x >= (uint)grid.Width || (uint)y >= (uint)grid.Height) return true;
            return !TileRules.IsFloor(grid.Tiles[y * grid.Width + x]);
        }

        /// <summary>True when the tile on the map is one of the listed kinds (off the map: never).</summary>
        private static bool Is(MapGrid grid, int x, int y, TileType a, TileType b)
        {
            if ((uint)x >= (uint)grid.Width || (uint)y >= (uint)grid.Height) return false;
            byte t = grid.Tiles[y * grid.Width + x];
            return t == (byte)a || t == (byte)b;
        }

        /// <summary>The variant of tile (x, y): its frame within its type's sprite in the <c>Tiles</c> atlas.</summary>
        public static byte Variant(MapGrid grid, int x, int y)
        {
            var type = (TileType)grid.Tiles[y * grid.Width + x];
            switch (type)
            {
                case TileType.Wall:
                    // Variant-major, as ArtBuild lays the rock frames out: RockVariants × 16 masks.
                    return (byte)(Hash(x, y, RenderGen.RockVariants) * ArtManifest.AutotileVariants + Mask4((tx, ty) => Solid(grid, tx, ty), x, y));
                case TileType.Gem:
                    // Variant-major, as ArtBuild lays the gem frames out: 4 variants × 16 masks.
                    return (byte)(Hash(x, y, RandomVariants) * ArtManifest.AutotileVariants + Mask4((tx, ty) => Solid(grid, tx, ty), x, y));
                case TileType.Ground:
                    return (byte)Hash(x, y, RenderGen.FloorVariants);
                case TileType.PlayerWall:
                    // Built walls join built walls (the damage frame is added by MapView).
                    return Mask4((tx, ty) => Is(grid, tx, ty, TileType.PlayerWall, TileType.PlayerWall), x, y);
                case TileType.WallBlueprint:
                    // A blueprint's outline joins the line it belongs to, built or planned.
                    return Mask4((tx, ty) => Is(grid, tx, ty, TileType.PlayerWall, TileType.WallBlueprint), x, y);
                default:
                    return 0;
            }
        }
    }
}

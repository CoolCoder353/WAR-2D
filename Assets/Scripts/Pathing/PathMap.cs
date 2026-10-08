using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Pathing
{
    /// <summary>
    /// The pathing view of the map: one byte per tile, 0 where a unit may stand and non-zero where it
    /// may not (wall, gem, border or a building footprint). <see cref="SectorFieldCache"/> owns the array
    /// and keeps it in step with the <see cref="WAR2D.World.MapGrid"/>. Index = <c>y * Width + x</c>.
    /// </summary>
    public struct PathMap : System.IDisposable
    {
        /// <summary>Open: units pass.</summary>
        public const byte Floor = 0;
        /// <summary>Blocked.</summary>
        public const byte Rock = 1;
        /// <summary>Blocked (kept for the generator's vocabulary; pathing treats it like rock).</summary>
        public const byte Gem = 2;
        /// <summary>Blocked; also what off-grid reads as.</summary>
        public const byte Border = 3;

        public int Width;
        public int Height;
        public NativeArray<byte> Tiles;

        public PathMap(int width, int height, NativeArray<byte> tiles)
        {
            Width = width;
            Height = height;
            Tiles = tiles;
        }

        /// <summary>True when the tile is inside the grid.</summary>
        public bool Contains(int2 tile) => (uint)tile.x < (uint)Width && (uint)tile.y < (uint)Height;

        /// <summary>The tile's value; off-grid reads as <see cref="Border"/>.</summary>
        public byte TileAt(int2 tile) => Contains(tile) ? Tiles[tile.y * Width + tile.x] : Border;

        /// <summary>True when the tile blocks movement.</summary>
        public bool IsBlocked(int2 tile) => TileAt(tile) != Floor;

        /// <summary>Frees <see cref="Tiles"/>, for callers that own it.</summary>
        public void Dispose() => Tiles.Dispose();

        /// <summary>The blocked grid of a map: non-zero for every tile that is not free ground.</summary>
        public static NativeArray<byte> Combine(in WAR2D.World.MapGrid grid, Allocator allocator)
        {
            var tiles = new NativeArray<byte>(grid.Width * grid.Height, allocator);
            for (int i = 0; i < tiles.Length; i++) tiles[i] = Blocked(grid, i);
            return tiles;
        }

        /// <summary>The pathing value of one grid tile.</summary>
        public static byte Blocked(in WAR2D.World.MapGrid grid, int i) =>
            grid.Tiles[i] != (byte)TileType.Ground || grid.Used[i] != 0 ? Rock : Floor;
    }
}

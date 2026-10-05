using System;
using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// The spike's flat tile grid. Tile values are <see cref="Floor"/> and the three blocked kinds;
    /// anything non-zero blocks movement and sight. Index = <c>y * Width + x</c>, and tile (x, y)
    /// covers [x, x+1) x [y, y+1), so a continuous position floors to its tile.
    /// </summary>
    public struct SpikeMap : IDisposable
    {
        /// <summary>Open ground: units and sight pass.</summary>
        public const byte Floor = 0;
        /// <summary>Blocked cave wall.</summary>
        public const byte Rock = 1;
        /// <summary>Blocked like rock, but a landmark: the generator sprinkles it on rock next to floor.</summary>
        public const byte Gem = 2;
        /// <summary>The map's outer ring: blocked, and written last so nothing generates over it.</summary>
        public const byte Border = 3;

        /// <summary>Tiles across.</summary>
        public int Width;

        /// <summary>Tiles down.</summary>
        public int Height;

        /// <summary>
        /// Tile values, indexed <c>y * Width + x</c>. It is allocated with the allocator passed to
        /// <see cref="MapGenerator.Generate"/> and its owner disposes it.
        /// </summary>
        public NativeArray<byte> Tiles;

        public SpikeMap(int width, int height, NativeArray<byte> tiles)
        {
            Width = width;
            Height = height;
            Tiles = tiles;
        }

        /// <summary>True when the tile is inside the grid.</summary>
        public bool Contains(int2 tile) => (uint)tile.x < (uint)Width && (uint)tile.y < (uint)Height;

        /// <summary>The tile's value; off-grid reads as <see cref="Border"/>, which blocks.</summary>
        public byte TileAt(int2 tile) => Contains(tile) ? Tiles[tile.y * Width + tile.x] : Border;

        /// <summary>True when the tile blocks movement and sight (rock, gem, border or off-grid).</summary>
        public bool IsBlocked(int2 tile) => TileAt(tile) != Floor;

        /// <summary>Disposes <see cref="Tiles"/>.</summary>
        public void Dispose() => Tiles.Dispose();
    }
}

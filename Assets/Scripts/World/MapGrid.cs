using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>What a map tile is. Stored as a byte in <see cref="WAR2D.World.MapGrid.Tiles"/>.</summary>
public enum TileType : byte
{
    Ground = 0,
    Wall = 1,
    Gem = 2,
    /// <summary>The map's outer ring, and anything off the map. Blocks like a wall.</summary>
    Border = 3,
}

namespace WAR2D.World
{
    /// <summary>
    /// The match's tile grid: an ECS singleton and a plain struct jobs can copy. Tile (x, y) covers
    /// [x, x+1) x [y, y+1) and lives at index <c>y * Width + x</c>. <see cref="MapStore"/> owns the memory.
    /// </summary>
    public struct MapGrid : IComponentData
    {
        /// <summary>Tiles across.</summary>
        public int Width;

        /// <summary>Tiles down.</summary>
        public int Height;

        /// <summary><c>(byte)TileType</c> per tile, row-major.</summary>
        public NativeArray<byte> Tiles;

        /// <summary>1 where a building footprint covers the tile.</summary>
        public NativeArray<byte> Used;

        /// <summary>True when the tile is inside the grid.</summary>
        public bool Contains(int2 t) => (uint)t.x < (uint)Width && (uint)t.y < (uint)Height;

        /// <summary>The tile's array index. Only valid when <see cref="Contains"/> is true.</summary>
        public int Index(int2 t) => t.y * Width + t.x;

        /// <summary>The tile's kind; <see cref="TileType.Border"/> outside the map.</summary>
        public TileType TileAt(int2 t) => Contains(t) ? (TileType)Tiles[Index(t)] : TileType.Border;

        /// <summary>True when a building footprint covers the tile (false outside the map).</summary>
        public bool IsUsed(int2 t) => Contains(t) && Used[Index(t)] != 0;

        /// <summary>Ground with no building on it.</summary>
        public bool IsWalkable(int2 t) => Contains(t) && Tiles[Index(t)] == (byte)TileType.Ground && Used[Index(t)] == 0;

        /// <summary>The opposite of <see cref="IsWalkable"/>.</summary>
        public bool BlocksMovement(int2 t) => !IsWalkable(t);
    }
}

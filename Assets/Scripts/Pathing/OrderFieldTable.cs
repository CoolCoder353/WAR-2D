using System;
using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Pathing
{
    /// <summary>
    /// What the tick's jobs read instead of the managed <see cref="SectorFieldCache"/>: for each
    /// (order handle, sector) the block of directions the order's route built there, or -1. The cache
    /// publishes rebuilt sectors into it at the boundary (<see cref="SectorFieldCache.CompleteRebuilds"/>),
    /// so jobs never see a half-built field.
    /// </summary>
    public struct OrderFieldTable : IDisposable
    {
        /// <summary>Order handles the table addresses; the cache refuses orders past this.</summary>
        public const int MaxOrders = 256;

        public int SectorsX, SectorsY, CellSize, CellsPerSector; // 16 cells per side at cell size 2
        /// <summary>Sectors in the map.</summary>
        public int SectorCount;
        /// <summary><c>[handle * SectorCount + sector]</c> → block index, or -1.</summary>
        public NativeArray<int> BlockOf;
        /// <summary><c>CellsPerSector²</c> direction bytes per block (<see cref="FlowDirections"/> values).</summary>
        public NativeList<byte> Blocks;
        /// <summary>Times each block was written, for tests and diagnostics.</summary>
        public NativeList<int> BlockVersion;

        public OrderFieldTable(int sectorsX, int sectorsY, int cellSize, Allocator allocator)
        {
            SectorsX = sectorsX;
            SectorsY = sectorsY;
            CellSize = cellSize;
            CellsPerSector = SectorGraph.SectorSize / cellSize;
            SectorCount = sectorsX * sectorsY;
            BlockOf = new NativeArray<int>(MaxOrders * SectorCount, allocator);
            for (int i = 0; i < BlockOf.Length; i++) BlockOf[i] = -1;
            Blocks = new NativeList<byte>(CellsPerSector * CellsPerSector * 64, allocator);
            BlockVersion = new NativeList<int>(64, allocator);
        }

        /// <summary>The sector a tile lies in.</summary>
        public int SectorOf(int2 tile) => tile.y / SectorGraph.SectorSize * SectorsX + tile.x / SectorGraph.SectorSize;

        /// <summary>
        /// The order's direction at a position, or <see cref="FlowDirections.None"/> when the order's route
        /// does not cover the position's sector (or the handle or position is out of range).
        /// </summary>
        public byte DirectionAt(int handle, float2 position, int mapWidth)
        {
            int2 tile = (int2)math.floor(position);
            if ((uint)handle >= MaxOrders || tile.x < 0 || tile.y < 0 || tile.x >= mapWidth) return FlowDirections.None;
            int sx = tile.x / SectorGraph.SectorSize, sy = tile.y / SectorGraph.SectorSize;
            if (sx >= SectorsX || sy >= SectorsY) return FlowDirections.None;
            int block = BlockOf[handle * SectorCount + sy * SectorsX + sx];
            if (block < 0) return FlowDirections.None;
            int cx = tile.x % SectorGraph.SectorSize / CellSize, cy = tile.y % SectorGraph.SectorSize / CellSize;
            return Blocks[block * CellsPerSector * CellsPerSector + cy * CellsPerSector + cx];
        }

        public void Dispose()
        {
            if (BlockOf.IsCreated) BlockOf.Dispose();
            if (Blocks.IsCreated) Blocks.Dispose();
            if (BlockVersion.IsCreated) BlockVersion.Dispose();
        }
    }
}

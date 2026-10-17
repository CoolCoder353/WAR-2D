using System;
using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.World
{
    /// <summary>
    /// The map generator's entry point: builds the seeded region-graph map (<see cref="RegionMapGenerator"/>) and
    /// returns its tiles and start sites. A pure function of (size, seed) and the config's layout settings.
    /// Tile values are <c>(byte)TileType</c>.
    /// </summary>
    public static class MapGenerator
    {
        /// <summary>Radius of a start site's floor disc.</summary>
        public const int HqClearRadius = RegionMapGenerator.StartRadius;

        /// <summary>Smallest map the generator accepts; below this a start site would not fit inside the border.</summary>
        public const int MinSize = 128;

        /// <summary>
        /// Generates a square map and its start sites. <paramref name="allocator"/> backs the returned tiles
        /// (row-major, <c>(byte)TileType</c>); the caller disposes them.
        /// </summary>
        public static (NativeArray<byte> tiles, int2[] hqSites) Generate(int size, uint seed, float gemChance, Allocator allocator)
        {
            MapLayout layout = RegionMapGenerator.Generate(size, seed, gemChance, allocator);
            return (layout.Tiles, layout.StartSites);
        }

        /// <summary>The start sites of the map <see cref="Generate"/> builds for this size and seed (tests and tools).</summary>
        public static int2[] Sites(int size, uint seed)
        {
            MapLayout layout = RegionMapGenerator.Generate(size, seed, 0f, Allocator.Temp);
            layout.Tiles.Dispose();
            return layout.StartSites;
        }
    }
}

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace WAR2D.World
{
    /// <summary>
    /// Owns the native memory behind a <see cref="MapGrid"/>: builds it from the seeded generator or
    /// the scene's tilemaps, hashes it so clients can verify their copy, and records footprint changes
    /// for the flow-field cache.
    /// </summary>
    public sealed class MapStore : IDisposable
    {
        private MapGrid _grid;
        private bool _disposed;

        /// <summary>The grid. Its arrays stay valid until <see cref="Dispose"/>.</summary>
        public MapGrid Grid => _grid;

        /// <summary>Generator HQ clearings; empty for scene maps.</summary>
        public int2[] HqSites { get; }

        /// <summary>World tile of grid tile (0, 0). Zero for generated maps.</summary>
        public int2 Origin { get; }

        /// <summary>Tiles whose footprint flag changed since the flow-field cache last drained this list.</summary>
        public List<int2> ChangedTiles { get; } = new List<int2>();

        private MapStore(int width, int height, NativeArray<byte> tiles, int2[] hqSites, int2 origin)
        {
            _grid = new MapGrid
            {
                Width = width,
                Height = height,
                Tiles = tiles,
                Used = new NativeArray<byte>(width * height, Allocator.Persistent),
            };
            HqSites = hqSites;
            Origin = origin;
        }

        /// <summary>Generates a <paramref name="size"/>² cave map. A pure function of its arguments.</summary>
        public static MapStore Generate(int size, uint seed, float gemChance)
        {
            var (tiles, sites) = MapGenerator.Generate(size, seed, gemChance, Allocator.Persistent);
            return new MapStore(size, size, tiles, sites, int2.zero);
        }

        /// <summary>Wraps tile kinds the caller already built (scene maps and tests). Takes ownership of <paramref name="tiles"/>.</summary>
        public static MapStore FromTiles(int width, int height, NativeArray<byte> tiles, int2 origin = default)
        {
            if (tiles.Length != width * height) throw new ArgumentException("tile count does not match width × height", nameof(tiles));
            return new MapStore(width, height, tiles, Array.Empty<int2>(), origin);
        }

        /// <summary>
        /// Builds the grid from a scene's tilemaps (Map_2): a cell on <paramref name="unwalkable"/> is a Gem
        /// when its tile asset's name contains "Gems" and a Wall otherwise; the walkable bounds (plus the
        /// one-cell ring the old tile map kept) are Ground, and everything else is Wall. Grid tiles are world
        /// tiles (cells through the tilemap's transform), so the tilemaps must sit at non-negative world tiles.
        /// </summary>
        public static MapStore FromTilemaps(Tilemap walkable, Tilemap unwalkable)
        {
            BoundsInt bounds = walkable.cellBounds;
            Vector3 originWorld = walkable.CellToWorld(Vector3Int.zero);
            int2 offset = new int2((int)math.round(originWorld.x), (int)math.round(originWorld.y));
            int2 groundMin = new int2(bounds.xMin - 1, bounds.yMin - 1) + offset;
            if (groundMin.x < 0 || groundMin.y < 0)
                Debug.LogError($"[Map] scene tilemap {bounds} at offset {offset} reaches negative world tiles; those tiles are dropped. Move the tilemaps.");
            int width = math.max(1, bounds.xMax + offset.x), height = math.max(1, bounds.yMax + offset.y);
            var tiles = new NativeArray<byte>(width * height, Allocator.Persistent);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var cell = new Vector3Int(x - offset.x, y - offset.y, 0);
                TileType type = x >= groundMin.x && y >= groundMin.y ? TileType.Ground : TileType.Wall;
                TileBase blocker = unwalkable != null ? unwalkable.GetTile(cell) : null;
                if (blocker != null) type = blocker.name.Contains("Gems") ? TileType.Gem : TileType.Wall;
                tiles[y * width + x] = (byte)type;
            }
            return FromTiles(width, height, tiles);
        }

        /// <summary>
        /// Builds a map from an ASCII picture, top row first: '.' ground, '#' wall, 'g' gem, 'B' border.
        /// Test and tooling helper.
        /// </summary>
        public static MapStore FromAscii(params string[] rows)
        {
            int height = rows.Length, width = rows[0].Length;
            var tiles = new NativeArray<byte>(width * height, Allocator.Persistent);
            for (int r = 0; r < height; r++)
            {
                int y = height - 1 - r;
                for (int x = 0; x < width; x++)
                {
                    char c = rows[r][x];
                    tiles[y * width + x] = (byte)(c == '#' ? TileType.Wall : c == 'g' ? TileType.Gem : c == 'B' ? TileType.Border : TileType.Ground);
                }
            }
            return FromTiles(width, height, tiles);
        }

        /// <summary>FNV-1a over the tile kinds (not footprints), for client verification.</summary>
        public ulong Hash()
        {
            ulong h = 14695981039346656037UL;
            h = (h ^ (uint)_grid.Width) * 1099511628211UL;
            h = (h ^ (uint)_grid.Height) * 1099511628211UL;
            NativeArray<byte> tiles = _grid.Tiles;
            for (int i = 0; i < tiles.Length; i++) h = (h ^ tiles[i]) * 1099511628211UL;
            return h;
        }

        /// <summary>Sets or clears the footprint flag of a grid tile and records it in <see cref="ChangedTiles"/>.</summary>
        public void SetUsed(int2 tile, bool used)
        {
            if (!_grid.Contains(tile)) return;
            int i = _grid.Index(tile);
            byte value = used ? (byte)1 : (byte)0;
            if (_grid.Used[i] == value) return;
            NativeArray<byte> flags = _grid.Used;
            flags[i] = value;
            ChangedTiles.Add(tile);
        }

        /// <summary>Frees the native arrays. Safe to call twice.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_grid.Tiles.IsCreated) _grid.Tiles.Dispose();
            if (_grid.Used.IsCreated) _grid.Used.Dispose();
        }
    }
}

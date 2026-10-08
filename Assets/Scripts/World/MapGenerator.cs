using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace WAR2D.World
{
    /// <summary>
    /// Builds seeded cave maps: a 45 % rock fill smoothed by four cellular-automaton
    /// passes, one HQ clearing per player joined to the centre by straight corridors, unreachable
    /// floor filled back in, gems sprinkled on rock next to floor (plus a guaranteed vein beside each HQ clearing), and a
    /// blocked border ring last. Tile values are <c>(byte)TileType</c>.
    /// It runs once per map in plain managed code and is a pure function of (size, seed).
    /// </summary>
    public static class MapGenerator
    {
        /// <summary>HQ clearings, one per player (the eight sites <see cref="HqSites"/> returns).</summary>
        public const int HqCount = 8;

        /// <summary>Radius of the floor disc carved at each HQ site.</summary>
        public const int HqClearRadius = 12;

        /// <summary>Half-width of the corridor carved from each HQ to the centre, so it is 3 tiles wide.</summary>
        public const int CorridorHalfWidth = 1;

        /// <summary>Smallest map the generator accepts; below this an HQ clearing would not fit inside the border.</summary>
        public const int MinSize = 128;

        private const float RockChance = 0.45f;
        private const int SmoothingPasses = 4;
        private const int RockThreshold = 5; // rock when 5 or more of the 3x3 neighbourhood (itself included) is rock
        private const float HqCircleRadius = 0.38f;

        private static readonly int2[] Steps =
        {
            new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1),
        };

        private const byte Floor = (byte)TileType.Ground, Rock = (byte)TileType.Wall, Gem = (byte)TileType.Gem, BorderTile = (byte)TileType.Border;

        /// <summary>
        /// Generates a square cave map and its HQ sites. <paramref name="allocator"/> backs the
        /// returned tiles (row-major, <c>(byte)TileType</c>); the caller disposes them.
        /// </summary>
        public static (NativeArray<byte> tiles, int2[] hqSites) Generate(int size, uint seed, float gemChance, Allocator allocator)
        {
            if (size < MinSize)
                throw new ArgumentOutOfRangeException(nameof(size), size, $"a generated map must be at least {MinSize} tiles across");

            var map = new GenMap(size, new NativeArray<byte>(size * size, allocator, NativeArrayOptions.UninitializedMemory));
            var rng = new Random(seed);

            // 1. The interior at 45 % rock; the border ring starts as rock too and becomes Border last.
            for (int i = 0; i < map.Tiles.Length; i++) map.Tiles[i] = Rock;
            for (int y = 1; y < size - 1; y++)
            for (int x = 1; x < size - 1; x++)
                map.Tiles[y * size + x] = rng.NextFloat() < RockChance ? Rock : Floor;

            // 2. Smooth the noise into caves.
            Smooth(map, SmoothingPasses);

            // 3. HQ clearings, each joined to the centre by a straight corridor. The corridors meet at
            // the centre, so every clearing is reachable from every other one.
            int2[] sites = HqSites(size);
            int2 centre = new int2(size / 2, size / 2);
            foreach (int2 site in sites)
            {
                CarveDisc(map, site, HqClearRadius);
                CarveCorridor(map, site, centre);
            }

            // 4. The smoothing leaves pockets of floor cut off from HQ 0; fill them back in, so every
            // floor tile that remains is reachable from HQ 0 by orthogonal steps.
            KeepConnectedFloor(map, sites[0]);

            // 5. Gems mark rock next to floor. This runs before the border write, so the ring's inner
            // edge can pick up gems that the border then overwrites.
            SprinkleGems(map, ref rng, gemChance);

            // 6. Every clearing gets a 3-tile vein on its rim, beside the corridor rather than in it,
            // so a Miner always has a target near its HQ.
            foreach (int2 site in sites) AddHqVein(map, site, centre);

            // 7. The border ring, last.
            WriteBorder(map);

            return (map.Tiles, sites);
        }

        /// <summary>
        /// The eight HQ sites, 45 degrees apart on a circle of radius <c>0.38 * size</c> around the
        /// centre. They are numbered around the circle, so the front pairs the plan's battle layout
        /// marches at each other are (0,1), (2,3), (4,5) and (6,7): neighbouring sites, each pair
        /// facing across its own front.
        /// </summary>
        public static int2[] HqSites(int size)
        {
            if (size < MinSize)
                throw new ArgumentOutOfRangeException(nameof(size), size, $"a generated map must be at least {MinSize} tiles across");

            var sites = new int2[HqCount];
            int2 centre = new int2(size / 2, size / 2);
            float radius = HqCircleRadius * size;
            for (int i = 0; i < HqCount; i++)
            {
                float angle = i * math.PI * 0.25f;
                sites[i] = (int2)math.round((float2)centre + new float2(math.cos(angle), math.sin(angle)) * radius);
            }
            return sites;
        }

        /// <summary>Four synchronous passes of "rock when 5+ of the 3x3 neighbourhood is rock".</summary>
        private static void Smooth(GenMap map, int passes)
        {
            var next = new NativeArray<byte>(map.Tiles.Length, Allocator.Temp);
            for (int pass = 0; pass < passes; pass++)
            {
                NativeArray<byte>.Copy(map.Tiles, next, next.Length);
                for (int y = 1; y < map.Height - 1; y++)
                for (int x = 1; x < map.Width - 1; x++)
                {
                    int rock = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (map.Tiles[(y + dy) * map.Width + x + dx] == Rock) rock++;
                    next[y * map.Width + x] = rock >= RockThreshold ? Rock : Floor;
                }
                NativeArray<byte>.Copy(next, map.Tiles, next.Length);
            }
            next.Dispose();
        }

        /// <summary>Carves a Euclidean floor disc, clipped to the grid.</summary>
        private static void CarveDisc(GenMap map, int2 centre, int radius)
        {
            for (int y = centre.y - radius; y <= centre.y + radius; y++)
            for (int x = centre.x - radius; x <= centre.x + radius; x++)
            {
                int dx = x - centre.x, dy = y - centre.y;
                if (dx * dx + dy * dy > radius * radius) continue;
                Carve(map, new int2(x, y));
            }
        }

        /// <summary>Carves a 3-tile-wide straight corridor by clearing a 3x3 block on every line tile.</summary>
        private static void CarveCorridor(GenMap map, int2 from, int2 to)
        {
            int steps = math.max(math.abs(to.x - from.x), math.abs(to.y - from.y));
            for (int i = 0; i <= steps; i++)
            {
                float t = steps == 0 ? 0f : (float)i / steps;
                CarveBlock(map, (int2)math.round(math.lerp((float2)from, (float2)to, t)), CorridorHalfWidth);
            }
        }

        private static void CarveBlock(GenMap map, int2 centre, int halfWidth)
        {
            for (int dy = -halfWidth; dy <= halfWidth; dy++)
            for (int dx = -halfWidth; dx <= halfWidth; dx++)
                Carve(map, centre + new int2(dx, dy));
        }

        private static void Carve(GenMap map, int2 tile)
        {
            if (map.Contains(tile)) map.Tiles[tile.y * map.Width + tile.x] = Floor;
        }

        /// <summary>Flood-fills floor from the start (orthogonal steps) and turns every unreached floor tile into rock.</summary>
        private static void KeepConnectedFloor(GenMap map, int2 start)
        {
            var reached = new bool[map.Tiles.Length];
            var stack = new Stack<int>();
            int startIndex = start.y * map.Width + start.x;
            reached[startIndex] = true;
            stack.Push(startIndex);

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % map.Width, y = i / map.Width;
                foreach (int2 step in Steps)
                {
                    int nx = x + step.x, ny = y + step.y;
                    if ((uint)nx >= (uint)map.Width || (uint)ny >= (uint)map.Height) continue;
                    int n = ny * map.Width + nx;
                    if (reached[n] || map.Tiles[n] != Floor) continue;
                    reached[n] = true;
                    stack.Push(n);
                }
            }

            for (int i = 0; i < map.Tiles.Length; i++)
                if (map.Tiles[i] == Floor && !reached[i]) map.Tiles[i] = Rock;
        }

        /// <summary>Each rock tile next to floor becomes a gem with probability <paramref name="gemChance"/>, in row-major order.</summary>
        private static void SprinkleGems(GenMap map, ref Random rng, float gemChance)
        {
            for (int y = 1; y < map.Height - 1; y++)
            for (int x = 1; x < map.Width - 1; x++)
            {
                int i = y * map.Width + x;
                if (map.Tiles[i] != Rock) continue;
                bool floorNext = false;
                for (int k = 0; k < Steps.Length && !floorNext; k++)
                    floorNext = map.Tiles[(y + Steps[k].y) * map.Width + x + Steps[k].x] == Floor;
                if (floorNext && rng.NextFloat() < gemChance) map.Tiles[i] = Gem;
            }
        }

        /// <summary>
        /// Puts a 3-tile gem vein on the clearing's rim, perpendicular to its corridor (which runs
        /// toward the centre) so the vein never blocks it. Each vein tile is the first rock met by a
        /// ray walked outward from the site, so it always faces floor.
        /// </summary>
        private static void AddHqVein(GenMap map, int2 site, int2 centre)
        {
            float2 dir = math.normalizesafe((float2)(site - centre), new float2(1, 0));
            float2 perp = new float2(-dir.y, dir.x);
            for (int k = -1; k <= 1; k++)
            {
                float2 origin = (float2)site + dir * k;
                int2 previous = (int2)math.floor(origin);
                for (float t = 0.25f; t < HqClearRadius * 3; t += 0.25f)
                {
                    int2 tile = (int2)math.floor(origin + perp * t);
                    if (math.all(tile == previous)) continue;
                    if (!map.Contains(tile)) break;
                    byte value = map.Tiles[tile.y * map.Width + tile.x];
                    bool orthogonal = math.abs(tile.x - previous.x) + math.abs(tile.y - previous.y) == 1;
                    bool fromFloor = map.Tiles[previous.y * map.Width + previous.x] == Floor;
                    if (value != Floor)
                    {
                        if (value == Rock && orthogonal && fromFloor) map.Tiles[tile.y * map.Width + tile.x] = Gem;
                        break;
                    }
                    previous = tile;
                }
            }
        }

        private static void WriteBorder(GenMap map)
        {
            int last = map.Height - 1, lastX = map.Width - 1;
            for (int x = 0; x <= lastX; x++)
            {
                map.Tiles[x] = BorderTile;
                map.Tiles[last * map.Width + x] = BorderTile;
            }
            for (int y = 0; y <= last; y++)
            {
                map.Tiles[y * map.Width] = BorderTile;
                map.Tiles[y * map.Width + lastX] = BorderTile;
            }
        }

        /// <summary>The generator's working view of the tile array.</summary>
        private struct GenMap
        {
            public int Width, Height;
            public NativeArray<byte> Tiles;
            public GenMap(int size, NativeArray<byte> tiles) { Width = size; Height = size; Tiles = tiles; }
            public bool Contains(int2 tile) => (uint)tile.x < (uint)Width && (uint)tile.y < (uint)Height;
        }
    }
}

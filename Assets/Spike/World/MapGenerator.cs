using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace WAR2D.Spike
{
    /// <summary>
    /// Builds the spike's synthetic cave maps: a 45 % rock fill smoothed by four cellular-automaton
    /// passes, one HQ clearing per player joined to the centre by straight corridors, unreachable
    /// floor filled back in, gems sprinkled on rock next to floor, and a blocked border ring last.
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
        private const float GemChance = 0.05f;

        private static readonly int2[] Steps =
        {
            new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1),
        };

        /// <summary>
        /// Generates a square cave map. <paramref name="allocator"/> backs <see cref="SpikeMap.Tiles"/>;
        /// the caller disposes it through <see cref="SpikeMap.Dispose"/>.
        /// </summary>
        public static SpikeMap Generate(int size, uint seed, Allocator allocator)
        {
            if (size < MinSize)
                throw new ArgumentOutOfRangeException(nameof(size), size, $"a generated map must be at least {MinSize} tiles across");

            var map = new SpikeMap(size, size, new NativeArray<byte>(size * size, allocator, NativeArrayOptions.UninitializedMemory));
            var rng = new Random(seed);

            // 1. The interior at 45 % rock; the border ring starts as rock too and becomes Border last.
            for (int i = 0; i < map.Tiles.Length; i++) map.Tiles[i] = SpikeMap.Rock;
            for (int y = 1; y < size - 1; y++)
            for (int x = 1; x < size - 1; x++)
                map.Tiles[y * size + x] = rng.NextFloat() < RockChance ? SpikeMap.Rock : SpikeMap.Floor;

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
            SprinkleGems(map, ref rng);

            // 6. The border ring, last.
            WriteBorder(map);

            return map;
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
        private static void Smooth(SpikeMap map, int passes)
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
                        if (map.Tiles[(y + dy) * map.Width + x + dx] == SpikeMap.Rock) rock++;
                    next[y * map.Width + x] = rock >= RockThreshold ? SpikeMap.Rock : SpikeMap.Floor;
                }
                NativeArray<byte>.Copy(next, map.Tiles, next.Length);
            }
            next.Dispose();
        }

        /// <summary>Carves a Euclidean floor disc, clipped to the grid.</summary>
        private static void CarveDisc(SpikeMap map, int2 centre, int radius)
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
        private static void CarveCorridor(SpikeMap map, int2 from, int2 to)
        {
            int steps = math.max(math.abs(to.x - from.x), math.abs(to.y - from.y));
            for (int i = 0; i <= steps; i++)
            {
                float t = steps == 0 ? 0f : (float)i / steps;
                CarveBlock(map, (int2)math.round(math.lerp((float2)from, (float2)to, t)), CorridorHalfWidth);
            }
        }

        private static void CarveBlock(SpikeMap map, int2 centre, int halfWidth)
        {
            for (int dy = -halfWidth; dy <= halfWidth; dy++)
            for (int dx = -halfWidth; dx <= halfWidth; dx++)
                Carve(map, centre + new int2(dx, dy));
        }

        private static void Carve(SpikeMap map, int2 tile)
        {
            if (map.Contains(tile)) map.Tiles[tile.y * map.Width + tile.x] = SpikeMap.Floor;
        }

        /// <summary>Flood-fills floor from the start (orthogonal steps) and turns every unreached floor tile into rock.</summary>
        private static void KeepConnectedFloor(SpikeMap map, int2 start)
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
                    if (reached[n] || map.Tiles[n] != SpikeMap.Floor) continue;
                    reached[n] = true;
                    stack.Push(n);
                }
            }

            for (int i = 0; i < map.Tiles.Length; i++)
                if (map.Tiles[i] == SpikeMap.Floor && !reached[i]) map.Tiles[i] = SpikeMap.Rock;
        }

        /// <summary>Each rock tile next to floor becomes a gem with 5 % probability, in row-major order.</summary>
        private static void SprinkleGems(SpikeMap map, ref Random rng)
        {
            for (int y = 1; y < map.Height - 1; y++)
            for (int x = 1; x < map.Width - 1; x++)
            {
                int i = y * map.Width + x;
                if (map.Tiles[i] != SpikeMap.Rock) continue;
                bool floorNext = false;
                for (int k = 0; k < Steps.Length && !floorNext; k++)
                    floorNext = map.Tiles[(y + Steps[k].y) * map.Width + x + Steps[k].x] == SpikeMap.Floor;
                if (floorNext && rng.NextFloat() < GemChance) map.Tiles[i] = SpikeMap.Gem;
            }
        }

        private static void WriteBorder(SpikeMap map)
        {
            int last = map.Height - 1, lastX = map.Width - 1;
            for (int x = 0; x <= lastX; x++)
            {
                map.Tiles[x] = SpikeMap.Border;
                map.Tiles[last * map.Width + x] = SpikeMap.Border;
            }
            for (int y = 0; y <= last; y++)
            {
                map.Tiles[y * map.Width] = SpikeMap.Border;
                map.Tiles[y * map.Width + lastX] = SpikeMap.Border;
            }
        }
    }
}

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// The direction byte a flow field stores per cell, and the step each index means. The order is
    /// the one the v0.3 plan fixes for Tasks 6 and 9: 0-7 = E, NE, N, NW, W, SW, S, SE, plus
    /// <see cref="AtGoal"/> and <see cref="None"/>.
    /// </summary>
    public static class FlowDirections
    {
        /// <summary>The cell is a goal: units have arrived.</summary>
        public const byte AtGoal = 254;

        /// <summary>The cell is blocked, out of the field's size class, or unreachable.</summary>
        public const byte None = 255;

        /// <summary>Steps per direction index, in index order: E, NE, N, NW, W, SW, S, SE.</summary>
        private static readonly int2[] StepByDirection =
        {
            new int2(1, 0), new int2(1, 1), new int2(0, 1), new int2(-1, 1),
            new int2(-1, 0), new int2(-1, -1), new int2(0, -1), new int2(1, -1),
        };

        /// <summary>True when the byte is one of the eight steps.</summary>
        public static bool IsStep(byte direction) => direction < 8;

        /// <summary>The step a direction index means; only valid for <see cref="IsStep"/> values.</summary>
        public static int2 Step(int direction) => StepByDirection[direction];
    }

    /// <summary>
    /// Builds an integration field (cost to the nearest goal) over the tile grid. Straight steps cost 10,
    /// diagonals 14, diagonals may not cut a blocked corner. Dial's algorithm with a 16-bucket ring.
    /// </summary>
    [BurstCompile]
    public struct BuildIntegrationFieldJob : IJob
    {
        public const ushort Unreachable = ushort.MaxValue;
        private const int Straight = 10, Diagonal = 14, Ring = 16; // Ring > largest step

        public int Width, Height;
        [ReadOnly] public NativeArray<byte> Tiles;   // 0 = floor, anything else blocks
        [ReadOnly] public NativeArray<int> Goals;    // goal cell indices
        public NativeArray<ushort> Cost;             // output, length Width * Height

        public void Execute()
        {
            for (int i = 0; i < Cost.Length; i++) Cost[i] = Unreachable;

            var buckets = new NativeArray<UnsafeList<int>>(Ring, Allocator.Temp);
            for (int b = 0; b < Ring; b++) buckets[b] = new UnsafeList<int>(1024, Allocator.Temp);
            int pending = 0;

            for (int g = 0; g < Goals.Length; g++)
            {
                int goal = Goals[g];
                if (Tiles[goal] != 0 || Cost[goal] == 0) continue;
                Cost[goal] = 0;
                Push(ref buckets, 0, goal);
                pending++;
            }

            for (int c = 0; pending > 0; c++)
            {
                int slot = c & (Ring - 1);
                UnsafeList<int> list = buckets[slot]; // steps are 10 or 14, so nothing is added to this slot while we drain it
                for (int k = 0; k < list.Length; k++)
                {
                    int i = list[k];
                    pending--;
                    if (Cost[i] != c) continue; // stale entry, a cheaper route was found later
                    int x = i % Width, y = i / Width;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                        int n = ny * Width + nx;
                        if (Tiles[n] != 0) continue;
                        bool diagonal = dx != 0 && dy != 0;
                        if (diagonal && (Tiles[y * Width + nx] != 0 || Tiles[ny * Width + x] != 0)) continue;
                        int next = c + (diagonal ? Diagonal : Straight);
                        if (next >= Unreachable || next >= Cost[n]) continue;
                        Cost[n] = (ushort)next;
                        Push(ref buckets, next & (Ring - 1), n);
                        pending++;
                    }
                }
                list.Clear();
                buckets[slot] = list;
            }

            for (int b = 0; b < Ring; b++) buckets[b].Dispose();
            buckets.Dispose();
        }

        private static void Push(ref NativeArray<UnsafeList<int>> buckets, int slot, int cell)
        {
            UnsafeList<int> l = buckets[slot];
            l.Add(cell);
            buckets[slot] = l;
        }
    }

    /// <summary>
    /// Turns an integration field into a direction field: every reachable cell points at its cheapest
    /// neighbour, using the same no-corner-cutting rule the integration field was built with.
    /// </summary>
    [BurstCompile]
    public struct BuildDirectionFieldJob : IJobParallelFor
    {
        public int Width, Height;
        [ReadOnly] public NativeArray<byte> Tiles;
        [ReadOnly] public NativeArray<ushort> Cost;
        [WriteOnly] public NativeArray<byte> Direction;

        public void Execute(int index)
        {
            if (Tiles[index] != 0 || Cost[index] == BuildIntegrationFieldJob.Unreachable)
            {
                Direction[index] = FlowDirections.None;
                return;
            }
            if (Cost[index] == 0)
            {
                Direction[index] = FlowDirections.AtGoal;
                return;
            }

            int x = index % Width, y = index / Width;
            int best = int.MaxValue;
            byte bestDirection = FlowDirections.None;
            for (int d = 0; d < 8; d++)
            {
                int dx = Dx(d), dy = Dy(d);
                int nx = x + dx, ny = y + dy;
                if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                int n = ny * Width + nx;
                if (Tiles[n] != 0) continue;
                int cost = Cost[n];
                if (cost == BuildIntegrationFieldJob.Unreachable) continue;
                if (dx != 0 && dy != 0 && (Tiles[y * Width + nx] != 0 || Tiles[ny * Width + x] != 0)) continue;
                if (cost >= best) continue;
                best = cost;
                bestDirection = (byte)d;
            }
            Direction[index] = bestDirection;
        }

        /// <summary>East component of direction index d (E, NE, N, NW, W, SW, S, SE).</summary>
        private static int Dx(int d) => d == 0 || d == 1 || d == 7 ? 1 : d == 3 || d == 4 || d == 5 ? -1 : 0;

        /// <summary>North component of direction index d.</summary>
        private static int Dy(int d) => d == 1 || d == 2 || d == 3 ? 1 : d == 5 || d == 6 || d == 7 ? -1 : 0;
    }

    /// <summary>
    /// Gives every floor tile its clearance: the distance, in tiles, to the nearest blocked tile,
    /// capped at <see cref="MaxClearance"/>. Blocked tiles are 0. A unit-cost two-pass distance
    /// transform, so the distance is Chebyshev (a diagonal neighbour is 1 away, matching Task 3's
    /// "every tile in the 3x3 neighbourhood is floor" rule for large units). It can recompute a
    /// window only, which is what a single terrain change needs: the tiles 2 * MaxClearance square
    /// around it, padded by MaxClearance so no distance within the cap is missed. Cells outside the
    /// map are ignored (the spike's maps have a blocked border ring).
    /// </summary>
    [BurstCompile]
    public struct ClearanceJob : IJob
    {
        /// <summary>Clearance is capped here: no consumer needs to tell 8 tiles from 40.</summary>
        public const int MaxClearance = 8;

        private const byte Far = byte.MaxValue;

        public int Width, Height;
        [ReadOnly] public NativeArray<byte> Tiles;

        /// <summary>First cell of the window written; default means the whole map.</summary>
        public int2 WindowOrigin;

        /// <summary>Cells written; (0,0) or less means the whole map.</summary>
        public int2 WindowSize;

        /// <summary>The clearance grid, length Width * Height. Cells outside the window keep their value.</summary>
        public NativeArray<byte> Clearance;

        public void Execute()
        {
            bool full = WindowSize.x <= 0 || WindowSize.y <= 0;
            int x0 = full ? 0 : math.max(0, WindowOrigin.x);
            int y0 = full ? 0 : math.max(0, WindowOrigin.y);
            int w = full ? Width : WindowSize.x;
            int h = full ? Height : WindowSize.y;
            int x1 = math.min(x0 + w - 1, Width - 1), y1 = math.min(y0 + h - 1, Height - 1);

            // Pad by the cap so every cell in the window sees every blocked tile within MaxClearance
            // of it; distances beyond that are capped anyway.
            int px0 = math.max(0, x0 - MaxClearance), py0 = math.max(0, y0 - MaxClearance);
            int px1 = math.min(Width - 1, x1 + MaxClearance), py1 = math.min(Height - 1, y1 + MaxClearance);
            int pw = px1 - px0 + 1, ph = py1 - py0 + 1;

            var scratch = new NativeArray<byte>(pw * ph, Allocator.Temp);
            for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
                scratch[y * pw + x] = Tiles[(py0 + y) * Width + px0 + x] != 0 ? (byte)0 : Far;

            Transform(scratch, pw, ph);

            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                byte distance = scratch[(y - py0) * pw + (x - px0)];
                Clearance[y * Width + x] = distance > MaxClearance ? (byte)MaxClearance : distance;
            }
            scratch.Dispose();
        }

        /// <summary>
        /// The two-pass chessboard distance transform: a forward pass over the north-west neighbours,
        /// then a backward pass over the rest. Unit steps, so the result is the Chebyshev distance.
        /// </summary>
        private static void Transform(NativeArray<byte> distance, int width, int height)
        {
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (distance[i] == 0) continue;
                int best = distance[i];
                if (x > 0) best = math.min(best, distance[i - 1] + 1);
                if (y > 0)
                {
                    best = math.min(best, distance[i - width] + 1);
                    if (x > 0) best = math.min(best, distance[i - width - 1] + 1);
                    if (x < width - 1) best = math.min(best, distance[i - width + 1] + 1);
                }
                distance[i] = (byte)math.min(best, Far);
            }

            for (int y = height - 1; y >= 0; y--)
            for (int x = width - 1; x >= 0; x--)
            {
                int i = y * width + x;
                if (distance[i] == 0) continue;
                int best = distance[i];
                if (x < width - 1) best = math.min(best, distance[i + 1] + 1);
                if (y < height - 1)
                {
                    best = math.min(best, distance[i + width] + 1);
                    if (x < width - 1) best = math.min(best, distance[i + width + 1] + 1);
                    if (x > 0) best = math.min(best, distance[i + width - 1] + 1);
                }
                distance[i] = (byte)math.min(best, Far);
            }
        }
    }

    /// <summary>
    /// Writes the tile grid a size class paths on: a floor tile with less than
    /// <see cref="MinClearance"/> tiles of clearance counts as blocked, so 1 leaves the grid
    /// unchanged (small units) and 2 seals 1-tile gaps (large units). Runs over a window like
    /// <see cref="ClearanceJob"/>.
    /// </summary>
    [BurstCompile]
    public struct FilterTilesJob : IJobParallelFor
    {
        public int Width, Height;

        /// <summary>First cell filtered; default means the whole map.</summary>
        public int2 Origin;

        /// <summary>Cells filtered; (0,0) or less means the whole map.</summary>
        public int2 Size;

        [ReadOnly] public NativeArray<byte> Tiles;
        [ReadOnly] public NativeArray<byte> Clearance;
        public byte MinClearance;

        /// <summary>The filtered grid, length Width * Height. Cells outside the window keep their value.</summary>
        [NativeDisableParallelForRestriction] public NativeArray<byte> Filtered;

        public void Execute(int i)
        {
            bool full = Size.x <= 0 || Size.y <= 0;
            int width = full ? Width : Size.x;
            int x = (full ? 0 : Origin.x) + i % width;
            int y = (full ? 0 : Origin.y) + i / width;
            int n = y * Width + x;
            Filtered[n] = Tiles[n] != 0 || Clearance[n] < MinClearance ? SpikeMap.Rock : SpikeMap.Floor;
        }
    }

    /// <summary>The spike's two unit size classes and the grid each one paths on.</summary>
    public static class FlowSizeClass
    {
        /// <summary>Radius 0.35 tiles (today's Tank).</summary>
        public const int Small = 0;

        /// <summary>Radius 0.7 tiles, about 1.4 across.</summary>
        public const int Large = 1;

        /// <summary>Classes the spike paths for.</summary>
        public const int Count = 2;

        /// <summary>Body radius of the class, in tiles.</summary>
        public static float Radius(int sizeClass) =>
            sizeClass == Large ? SpikeScenario.LargeRadius : SpikeScenario.SmallRadius;

        /// <summary>
        /// The clearance a tile needs to hold the class: ceil(radius + 0.5). That is 1 for small
        /// units, which leaves the grid unchanged, and 2 for large units, which seals 1-tile gaps.
        /// </summary>
        public static byte MinClearance(int sizeClass) => (byte)math.ceil(Radius(sizeClass) + 0.5f);
    }

    /// <summary>
    /// Builds the whole-map filtered grid for a size class in one pass. The cache maintains its
    /// large-class grid incrementally instead; this is the full build the tests and setup use.
    /// </summary>
    public static class FlowFilter
    {
        /// <summary>Allocates and fills a filtered copy of <paramref name="tiles"/>.</summary>
        public static NativeArray<byte> Build(
            in SpikeMap map, NativeArray<byte> tiles, NativeArray<byte> clearance, byte minClearance, Allocator allocator)
        {
            var filtered = new NativeArray<byte>(map.Width * map.Height, allocator);
            new FilterTilesJob
            {
                Width = map.Width, Height = map.Height, Tiles = tiles, Clearance = clearance,
                MinClearance = minClearance, Filtered = filtered,
            }.Run(map.Width * map.Height);
            return filtered;
        }
    }
}

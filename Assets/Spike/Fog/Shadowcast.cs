using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// Symmetric shadowcasting (Albert Ford, 2020) with exact integer slopes, limited to a circle of the given radius.
    /// Marks visible tiles with 1. Blocking tiles are visible themselves but stop sight. Out-of-map counts as blocking.
    /// Writes only the value 1, so many casts may share one grid from parallel jobs.
    /// </summary>
    public static class Shadowcast
    {
        private struct Row { public int Depth, StartNum, StartDen, EndNum, EndDen; }

        /// <summary>
        /// Marks every tile <paramref name="origin"/> sees within <paramref name="radius"/> in
        /// <paramref name="visible"/>. <paramref name="tiles"/> is the map: <see cref="SpikeMap.Floor"/>
        /// (0) passes sight, anything else blocks it, and off-map tiles block like walls.
        /// </summary>
        public static void Cast(int2 origin, int radius, int width, int height,
                                in NativeArray<byte> tiles, ref NativeArray<byte> visible)
        {
            int radiusSq = radius * radius + radius; // a slightly rounder circle than r^2
            Mark(origin, width, height, ref visible);
            var stack = new NativeList<Row>(64, Allocator.Temp);
            for (int quadrant = 0; quadrant < 4; quadrant++)
            {
                stack.Add(new Row { Depth = 1, StartNum = -1, StartDen = 1, EndNum = 1, EndDen = 1 });
                while (stack.Length > 0)
                {
                    Row row = stack[stack.Length - 1];
                    stack.RemoveAt(stack.Length - 1);
                    if (row.Depth > radius) continue;

                    // round_ties_up(depth * start) and round_ties_down(depth * end), in integers
                    int minCol = FloorDiv(2 * row.Depth * row.StartNum + row.StartDen, 2 * row.StartDen);
                    int maxCol = CeilDiv(2 * row.Depth * row.EndNum - row.EndDen, 2 * row.EndDen);
                    int prev = -1; // -1 none yet, 0 floor, 1 wall
                    for (int col = minCol; col <= maxCol; col++)
                    {
                        int2 tile = Transform(quadrant, origin, row.Depth, col);
                        bool wall = IsBlocking(tile, width, height, tiles);
                        bool inCircle = row.Depth * row.Depth + col * col <= radiusSq;
                        if (inCircle && (wall || IsSymmetric(row, col))) Mark(tile, width, height, ref visible);
                        if (prev == 1 && !wall)
                        {
                            row.StartNum = 2 * col - 1;
                            row.StartDen = 2 * row.Depth;
                        }
                        if (prev == 0 && wall)
                        {
                            Row next = row;
                            next.Depth = row.Depth + 1;
                            next.EndNum = 2 * col - 1;
                            next.EndDen = 2 * row.Depth;
                            stack.Add(next);
                        }
                        prev = wall ? 1 : 0;
                    }
                    if (prev == 0)
                    {
                        Row next = row;
                        next.Depth = row.Depth + 1;
                        stack.Add(next);
                    }
                }
            }
            stack.Dispose();
        }

        private static bool IsSymmetric(Row row, int col) =>
            col * row.StartDen >= row.Depth * row.StartNum && col * row.EndDen <= row.Depth * row.EndNum;

        private static int2 Transform(int quadrant, int2 o, int depth, int col)
        {
            switch (quadrant)
            {
                // The four 90-degree quadrants around the origin; which one is called "north" doesn't matter.
                case 0: return new int2(o.x + col, o.y - depth);
                case 1: return new int2(o.x + depth, o.y + col);
                case 2: return new int2(o.x + col, o.y + depth);
                default: return new int2(o.x - depth, o.y + col);
            }
        }

        private static bool IsBlocking(int2 t, int width, int height, in NativeArray<byte> tiles) =>
            (uint)t.x >= (uint)width || (uint)t.y >= (uint)height || tiles[t.y * width + t.x] != 0;

        private static void Mark(int2 t, int width, int height, ref NativeArray<byte> visible)
        {
            if ((uint)t.x < (uint)width && (uint)t.y < (uint)height) visible[t.y * width + t.x] = 1;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b); // b > 0
        private static int CeilDiv(int a, int b) => -FloorDiv(-a, b);
    }
}

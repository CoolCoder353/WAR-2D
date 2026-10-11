using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Bresenham traces over the fog grid's <see cref="SimData.Opaque"/> cells, shared by vision and combat
    /// so a unit shoots only at what it could see. The two end cells are never tested: a unit standing in a
    /// half-rock cell can still see and fire out of it.
    /// </summary>
    public static class LineOfSight
    {
        /// <summary>True when no opaque cell lies strictly between cells a and b.</summary>
        public static bool Cells(NativeArray<byte> opaque, int fogW, int2 a, int2 b)
        {
            int2 d = math.abs(b - a);
            int2 step = new int2(a.x < b.x ? 1 : -1, a.y < b.y ? 1 : -1);
            int err = d.x - d.y;
            int2 p = a;
            while (p.x != b.x || p.y != b.y)
            {
                int e2 = 2 * err;
                if (e2 > -d.y) { err -= d.y; p.x += step.x; }
                if (e2 < d.x) { err += d.x; p.y += step.y; }
                if (p.x == b.x && p.y == b.y) return true;
                if (opaque[p.y * fogW + p.x] != 0) return false;
            }
            return true;
        }

        /// <summary>True when no opaque cell lies strictly between world positions a and b (tiles).</summary>
        public static bool Clear(NativeArray<byte> opaque, int fogW, int fogH, float invFogCellSize, float2 a, float2 b)
        {
            int2 max = new int2(fogW - 1, fogH - 1);
            return Cells(opaque, fogW, math.clamp((int2)math.floor(a * invFogCellSize), 0, max),
                math.clamp((int2)math.floor(b * invFogCellSize), 0, max));
        }
    }
}

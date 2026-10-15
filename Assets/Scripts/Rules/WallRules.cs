using System;
using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>The lifecycle of a player wall tile, in <c>SimData.WallState</c>.</summary>
public static class WallStates
{
    public const byte None = 0;
    /// <summary>Planned: walkable and see-through, no health yet.</summary>
    public const byte Blueprint = 1;
    /// <summary>A Builder started it: it blocks, health climbs with work.</summary>
    public const byte Rising = 2;
    /// <summary>Complete.</summary>
    public const byte Built = 3;
}

/// <summary>Player wall placement (v0.7). Pure.</summary>
public static class WallRules
{
    /// <summary>
    /// The 4-connected line from a to b, both included: a Bresenham line where every diagonal step is split
    /// into an x step then a y step, so neighbouring segments always share an edge (no corner gaps).
    /// </summary>
    public static void Line(int2 a, int2 b, List<int2> into)
    {
        int2 d = math.abs(b - a);
        int2 step = new int2(a.x < b.x ? 1 : -1, a.y < b.y ? 1 : -1);
        int2 p = a;
        into.Add(p);
        int err = 0;
        // Walk |dx| + |dy| unit steps, picking the axis that keeps the path closest to the true line.
        for (int i = 0; i < d.x + d.y; i++)
        {
            int ex = err + d.y, ey = err - d.x;
            if (p.x != b.x && (math.abs(ex) < math.abs(ey) || p.y == b.y)) { p.x += step.x; err = ex; }
            else { p.y += step.y; err = ey; }
            into.Add(p);
        }
    }

    /// <summary>True when a wall blueprint may go on the tile: plain ground, no building footprint.</summary>
    public static bool CanPlace(int2 tile, Func<int2, TileType> tileAt, Func<int2, bool> isUsed) =>
        tileAt(tile) == TileType.Ground && !isUsed(tile);
}

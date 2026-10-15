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
        var line = new Line4(a, b);
        while (line.Next(out int2 tile)) into.Add(tile);
    }

    /// <summary>True when a wall blueprint may go on the tile: plain ground, no building footprint.</summary>
    public static bool CanPlace(int2 tile, Func<int2, TileType> tileAt, Func<int2, bool> isUsed) =>
        tileAt(tile) == TileType.Ground && !isUsed(tile);
}

/// <summary>Walks the 4-connected line from a to b, both included (see <see cref="WallRules.Line"/>). Burst-safe.</summary>
public struct Line4
{
    private int2 p, b, d, step;
    private int err, left;
    private bool started;

    public Line4(int2 a, int2 b)
    {
        p = a;
        this.b = b;
        d = math.abs(b - a);
        step = new int2(a.x < b.x ? 1 : -1, a.y < b.y ? 1 : -1);
        err = 0;
        left = d.x + d.y;
        started = false;
    }

    /// <summary>The next tile, or false after b.</summary>
    public bool Next(out int2 tile)
    {
        if (!started) { started = true; tile = p; return true; }
        if (left == 0) { tile = p; return false; }
        int ex = err + d.y, ey = err - d.x;
        if (p.x != b.x && (math.abs(ex) < math.abs(ey) || p.y == b.y)) { p.x += step.x; err = ex; }
        else { p.y += step.y; err = ey; }
        left--;
        tile = p;
        return true;
    }
}

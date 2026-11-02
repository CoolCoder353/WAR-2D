using Unity.Collections;
using Unity.Mathematics;

/// <summary>The result of looking along a dig line.</summary>
public enum DigStep : byte
{
    /// <summary>Every tile on the line is floor: the tunnel is finished.</summary>
    Done,
    /// <summary>Dig <c>dig</c>, standing on <c>stand</c>.</summary>
    Dig,
    /// <summary>The first solid tile can't be dug (the border).</summary>
    Blocked,
}

/// <summary>Digger rules (v0.7). Pure and Burst-safe.</summary>
public static class DigRules
{
    /// <summary>
    /// Walks the 4-connected line from the Digger's tile to the target and finds the first tile that isn't
    /// floor: <see cref="DigStep.Dig"/> when it is diggable (rock, gem, player wall), with the floor tile
    /// before it to stand on; <see cref="DigStep.Blocked"/> at the border or off the map, and at a gem within
    /// <paramref name="rim"/> tiles of the map's edge (<see cref="InRim"/>).
    /// </summary>
    public static DigStep Next(NativeArray<byte> tiles, int width, int height, int2 from, int2 target, out int2 dig, out int2 stand, int rim = 0)
    {
        dig = stand = from;
        var line = new Line4(from, target);
        int2 previous = from;
        while (line.Next(out int2 t))
        {
            if ((uint)t.x >= (uint)width || (uint)t.y >= (uint)height) return DigStep.Blocked;
            TileType type = (TileType)tiles[t.y * width + t.x];
            if (TileRules.IsFloor(type)) { previous = t; continue; }
            if (!TileRules.IsDiggable(type) || (TileRules.IsGem(type) && InRim(t, width, height, rim))) return DigStep.Blocked;
            dig = t;
            stand = previous;
            return DigStep.Dig;
        }
        return DigStep.Done;
    }

    /// <summary>
    /// True for a tile within <paramref name="rim"/> tiles of the map's edge (<c>Map/EdgeTiles</c>): the undiggable rim. Its
    /// rock is border; the gems the generator keeps there (a start vein can reach it) can be mined but not dug (code review
    /// 2026-11-03: a run of rim gems dug out opened a path along the edge).
    /// </summary>
    public static bool InRim(int2 t, int width, int height, int rim) =>
        math.min(math.min(t.x, t.y), math.min(width - 1 - t.x, height - 1 - t.y)) < rim;

    /// <summary>The rim's width on a map this size: <c>Map/EdgeTiles</c>, at least 1 and at most a quarter of the shorter side (the generator's band).</summary>
    public static int RimWidth(int edgeTiles, int width, int height) => math.clamp(edgeTiles, 1, math.max(1, math.min(width, height) / 4));
}

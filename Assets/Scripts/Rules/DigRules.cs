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
    /// before it to stand on; <see cref="DigStep.Blocked"/> at the border or off the map.
    /// </summary>
    public static DigStep Next(NativeArray<byte> tiles, int width, int height, int2 from, int2 target, out int2 dig, out int2 stand)
    {
        dig = stand = from;
        var line = new Line4(from, target);
        int2 previous = from;
        while (line.Next(out int2 t))
        {
            if ((uint)t.x >= (uint)width || (uint)t.y >= (uint)height) return DigStep.Blocked;
            TileType type = (TileType)tiles[t.y * width + t.x];
            if (TileRules.IsFloor(type)) { previous = t; continue; }
            if (!TileRules.IsDiggable(type)) return DigStep.Blocked;
            dig = t;
            stand = previous;
            return DigStep.Dig;
        }
        return DigStep.Done;
    }
}

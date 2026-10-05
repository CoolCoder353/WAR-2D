using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>
/// Maps a building's anchor tile and size to the tiles it covers and where its sprite is drawn.
/// The anchor is the tile at index size/2 (integer division) from the bottom-left of the footprint.
/// </summary>
public static class Footprint
{
    public static int2 Start(int2 anchor, int2 size) => anchor - size / 2;

    public static List<int2> Tiles(int2 anchor, int2 size)
    {
        int2 start = Start(anchor, size);
        var tiles = new List<int2>(size.x * size.y);
        for (int x = 0; x < size.x; x++)
            for (int y = 0; y < size.y; y++)
                tiles.Add(start + new int2(x, y));
        return tiles;
    }

    /// <summary>World position of the footprint's centre (where the sprite pivot goes).</summary>
    public static float2 VisualCenter(int2 anchor, int2 size) => (float2)Start(anchor, size) + (float2)size / 2f;

    /// <summary>The anchor whose visual centre is nearest to a world point (for placement previews).</summary>
    public static int2 SnapAnchor(float2 worldPoint, int2 size)
    {
        float2 offset = (float2)size / 2f - (float2)(size / 2);
        return (int2)math.round(worldPoint - offset);
    }
}

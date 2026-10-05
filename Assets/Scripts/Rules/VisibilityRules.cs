using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

/// <summary>What a client is allowed to see, given its view rectangle.</summary>
public static class VisibilityRules
{
    public static bool InBox(float2 point, int2 cornerA, int2 cornerB)
    {
        int2 min = math.min(cornerA, cornerB);
        int2 max = math.max(cornerA, cornerB);
        return point.x >= min.x && point.x <= max.x && point.y >= min.y && point.y <= max.y;
    }

    public static List<Vector2> Filter(List<float2> points, int2 cornerA, int2 cornerB)
    {
        var kept = new List<Vector2>();
        foreach (float2 p in points)
        {
            if (InBox(p, cornerA, cornerB)) kept.Add(new Vector2(p.x, p.y));
        }
        return kept;
    }
}

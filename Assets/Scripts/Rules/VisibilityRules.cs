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

    /// <summary>
    /// Whether a death may play as an explosion for a viewer: only where the viewer's fog grid sees it,
    /// until the match is over (GameOver), when nothing is secret any more and the match-end wipe shows
    /// everywhere. The viewer's camera box still applies (<see cref="Filter"/>).
    /// </summary>
    public static bool ShowsDeath(GameState state, bool gridSees) => gridSees || state == GameState.GameOver;

    /// <summary>
    /// The blasts (bombs or Suicide bombers) a viewer is shown: on fog cells its grid sees now and inside its
    /// view box. Null when there are none, so nothing is sent.
    /// </summary>
    public static Vector2[] SeenBlasts(WAR2D.Sim.SimData data, int grid, List<float2> blasts, int2 cornerA, int2 cornerB)
    {
        if (blasts.Count == 0) return null;
        var seen = new List<float2>();
        foreach (float2 p in blasts) if (data.Sees(grid, p)) seen.Add(p);
        if (seen.Count == 0) return null;
        List<Vector2> visible = Filter(seen, cornerA, cornerB);
        return visible.Count > 0 ? visible.ToArray() : null;
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

using System;
using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>What a drag box keeps (v0.18; one building type, 2026-11-07). Pure.</summary>
public static class SelectionRules
{
    /// <summary>
    /// A box that catches Builders and any other unit keeps only the others; a box of nothing but Builders keeps them.
    /// Diggers and Bombers count as army. There is no override key: <c>.</c> and Ctrl + <c>.</c> select Builders, and
    /// a double-click selects those on screen.
    /// </summary>
    public static void PreferArmy(List<int> caught, Func<int, UnitType> typeOf)
    {
        bool army = false;
        foreach (int id in caught)
            if (typeOf(id) != UnitType.Builder) { army = true; break; }
        if (army) caught.RemoveAll(id => typeOf(id) == UnitType.Builder);
    }

    /// <summary>
    /// The ids of one building type out of a building box's <paramref name="candidates"/> (id, type and footprint corners): the
    /// <paramref name="keep"/> type when given (Shift adds to a selection of that type), else the type of the building nearest
    /// <paramref name="dragStart"/>, the first one the box reached (owner, 2026-11-07: a drag over two Heavy Factories, then a
    /// Barracks, takes only the Heavy Factories).
    /// </summary>
    public static List<int> OneBuildingType(IReadOnlyList<(int id, BuildingType type, float2 lo, float2 hi)> candidates, float2 dragStart, BuildingType keep = BuildingType.None)
    {
        BuildingType type = keep;
        float best = float.MaxValue;
        if (type == BuildingType.None)
            foreach (var c in candidates)
            {
                float d = math.distancesq(dragStart, math.clamp(dragStart, c.lo, c.hi));
                if (d < best) { best = d; type = c.type; }
            }
        var ids = new List<int>();
        foreach (var c in candidates) if (c.type == type) ids.Add(c.id);
        return ids;
    }
}

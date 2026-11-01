using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Formation slots (v0.16): a block centred on the goal, facing from the group's centre towards the goal, rows of about
    /// √(2n) (wider than deep), the front rows for Heavies, Tanks, Flamethrowers and bombers, the back rows for Snipers and
    /// Artillery, the rest between (roles never share a row). Within a role the units furthest ahead take the front rows, and within a row units take slots in the order of where
    /// they stand across the facing, so paths don't cross. Pure.
    /// </summary>
    public static class FormationRules
    {
        /// <summary>0 front, 1 middle, 2 back.</summary>
        public static int RoleOf(UnitType type) => type switch
        {
            UnitType.Heavy or UnitType.Tank or UnitType.Flamethrower or UnitType.SuicideBomber => 0,
            UnitType.Sniper or UnitType.Artillery => 2,
            _ => 1,
        };

        /// <summary>Fills <paramref name="offsets"/> (one per unit, same order) with each unit's slot relative to the goal.</summary>
        public static void Layout(IReadOnlyList<UnitType> types, IReadOnlyList<float2> positions, float2 goal, float spacing, List<float2> offsets)
        {
            int n = types.Count;
            offsets.Clear();
            for (int i = 0; i < n; i++) offsets.Add(float2.zero);
            if (n == 0) return;
            float2 centroid = 0;
            for (int i = 0; i < n; i++) centroid += positions[i];
            centroid /= n;
            float2 facing = math.normalizesafe(goal - centroid, new float2(0, 1));
            float2 across = new float2(facing.y, -facing.x);
            int cols = math.max(1, (int)math.ceil(math.sqrt(2f * n)));

            var order = new List<int>(n);
            for (int i = 0; i < n; i++) order.Add(i);
            // By role, then (within a role) those already furthest ahead take the front rows, so fewer paths cross.
            order.Sort((a, b) => RoleOf(types[a]) != RoleOf(types[b]) ? RoleOf(types[a]).CompareTo(RoleOf(types[b]))
                : math.dot(positions[b], facing) != math.dot(positions[a], facing) ? math.dot(positions[b], facing).CompareTo(math.dot(positions[a], facing))
                : a.CompareTo(b));

            // Rows front to back: up to cols units each, a new row whenever the role changes.
            var rowStarts = new List<int>();
            for (int k = 0; k < n; k++)
                if (k == 0 || k - rowStarts[rowStarts.Count - 1] == cols || RoleOf(types[order[k]]) != RoleOf(types[order[k - 1]])) rowStarts.Add(k);
            int rows = rowStarts.Count;

            var row = new List<int>(cols);
            for (int r = 0; r < rows; r++)
            {
                row.Clear();
                int end = r + 1 < rows ? rowStarts[r + 1] : n;
                for (int k = rowStarts[r]; k < end; k++) row.Add(order[k]);
                row.Sort((a, b) => math.dot(positions[a], across).CompareTo(math.dot(positions[b], across)));
                float depth = ((rows - 1) * 0.5f - r) * spacing; // row 0 is the front
                for (int c = 0; c < row.Count; c++)
                {
                    float side = (c - (row.Count - 1) * 0.5f) * spacing;
                    offsets[row[c]] = facing * depth + across * side;
                }
            }
        }
    }
}

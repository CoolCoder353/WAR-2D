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
        /// <summary>
        /// The offset of a formation member with no slot of its own (every pulled-in place was rock or another unit's slot,
        /// code review 2026-11-03): it keeps the group's pace, then stops around the goal like any group instead of steering
        /// to a slot (two units steering to one slot never both arrive).
        /// </summary>
        public static readonly float2 NoSeat = new float2(float.MaxValue, float.MaxValue);

        /// <summary>
        /// The group speed of an Attack-move formation: no cap, so each unit goes at its own speed to its slot (0 would mean
        /// no formation at all).
        /// </summary>
        public const float OwnSpeed = float.MaxValue;

        /// <summary>True for a real slot, false for <see cref="NoSeat"/>.</summary>
        public static bool HasSeat(float2 offset) => offset.x < float.MaxValue;

        /// <summary>The members' slots by cell, so a blocked slot's check reads only its neighbours, not every slot of a large group.</summary>
        public sealed class SeatGrid
        {
            private readonly Dictionary<int2, int> head = new Dictionary<int2, int>();
            private readonly List<(float2 at, int next)> seats = new List<(float2, int)>();
            private float cell, minSq;

            /// <summary>Forgets every seat. A cell is just over half a slot's spacing, so a seat that close is in the 3x3 cells around.</summary>
            public void Reset(float spacing)
            {
                head.Clear();
                seats.Clear();
                cell = math.max(spacing * 0.51f, 1e-3f);
                minSq = spacing * spacing * 0.25f;
            }

            public void Add(float2 offset)
            {
                int2 c = (int2)math.floor(offset / cell);
                seats.Add((offset, head.TryGetValue(c, out int next) ? next : -1));
                head[c] = seats.Count - 1;
            }

            /// <summary>True when <paramref name="offset"/> is within half a slot's spacing of a seat.</summary>
            public bool Taken(float2 offset)
            {
                int2 c = (int2)math.floor(offset / cell);
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                    if (head.TryGetValue(c + new int2(x, y), out int i))
                        for (; i >= 0; i = seats[i].next)
                            if (math.distancesq(seats[i].at, offset) < minSq) return true;
                return false;
            }
        }

        /// <summary>0 front, 1 middle, 2 back.</summary>
        public static int RoleOf(UnitType type) => type switch
        {
            UnitType.Heavy or UnitType.Tank or UnitType.Flamethrower or UnitType.Bomber => 0,
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
            var role = new int[n];
            var ahead = new float[n];
            var sideways = new float[n];
            for (int i = 0; i < n; i++)
            {
                order.Add(i);
                role[i] = RoleOf(types[i]);
                ahead[i] = math.dot(positions[i], facing);
                sideways[i] = math.dot(positions[i], across);
            }
            // By role, then (within a role) those already furthest ahead take the front rows, so fewer paths cross.
            order.Sort((a, b) => role[a] != role[b] ? role[a].CompareTo(role[b])
                : ahead[b] != ahead[a] ? ahead[b].CompareTo(ahead[a])
                : a.CompareTo(b));

            // Rows front to back: up to cols units each, a new row whenever the role changes.
            var rowStarts = new List<int>();
            for (int k = 0; k < n; k++)
                if (k == 0 || k - rowStarts[rowStarts.Count - 1] == cols || role[order[k]] != role[order[k - 1]]) rowStarts.Add(k);
            int rows = rowStarts.Count;

            var row = new List<int>(cols);
            for (int r = 0; r < rows; r++)
            {
                row.Clear();
                int end = r + 1 < rows ? rowStarts[r + 1] : n;
                for (int k = rowStarts[r]; k < end; k++) row.Add(order[k]);
                row.Sort((a, b) => sideways[a].CompareTo(sideways[b]));
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

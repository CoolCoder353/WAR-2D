using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
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

        /// <summary>
        /// Fills <paramref name="offsets"/> (one per unit, same order) with each unit's slot relative to the goal. The sorts run in a
        /// Burst job (2026-11-07: with managed comparers, the layouts of an order wave's eight 3,000-unit groups took 30 ms); units level
        /// with each other keep their list order.
        /// </summary>
        public static void Layout(IReadOnlyList<UnitType> types, IReadOnlyList<float2> positions, float2 goal, float spacing, List<float2> offsets)
        {
            int n = types.Count;
            offsets.Clear();
            if (n == 0) return;
            float2 centroid = 0;
            for (int i = 0; i < n; i++) centroid += positions[i];
            centroid /= n;
            float2 facing = math.normalizesafe(goal - centroid, new float2(0, 1));
            float2 across = new float2(facing.y, -facing.x);
            var units = new NativeArray<LayoutUnit>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++)
                units[i] = new LayoutUnit { Index = i, Role = RoleOf(types[i]), Ahead = math.dot(positions[i], facing), Sideways = math.dot(positions[i], across) };
            var slots = new NativeArray<float2>(n, Allocator.TempJob);
            new LayoutJob { Units = units, Facing = facing, Across = across, Spacing = spacing, Cols = math.max(1, (int)math.ceil(math.sqrt(2f * n))), Offsets = slots }.Run();
            for (int i = 0; i < n; i++) offsets.Add(slots[i]);
            units.Dispose();
            slots.Dispose();
        }

        private struct LayoutUnit
        {
            public int Index, Role;
            public float Ahead, Sideways;
        }

        /// <summary>By role (front first), then the units furthest ahead first.</summary>
        private struct FrontFirst : IComparer<LayoutUnit>
        {
            public int Compare(LayoutUnit a, LayoutUnit b) =>
                a.Role != b.Role ? a.Role.CompareTo(b.Role) : a.Ahead != b.Ahead ? b.Ahead.CompareTo(a.Ahead) : a.Index.CompareTo(b.Index);
        }

        /// <summary>Left to right across the facing.</summary>
        private struct LeftFirst : IComparer<LayoutUnit>
        {
            public int Compare(LayoutUnit a, LayoutUnit b) => a.Sideways != b.Sideways ? a.Sideways.CompareTo(b.Sideways) : a.Index.CompareTo(b.Index);
        }

        /// <summary>
        /// Rows front to back, up to <see cref="Cols"/> units each and a new row whenever the role changes; each row's units left to right
        /// in its slots, the block centred on the goal.
        /// </summary>
        [BurstCompile]
        private struct LayoutJob : IJob
        {
            public NativeArray<LayoutUnit> Units;
            public float2 Facing, Across;
            public float Spacing;
            public int Cols;
            public NativeArray<float2> Offsets; // by the unit's Index

            public void Execute()
            {
                int n = Units.Length;
                Units.Sort(new FrontFirst());
                var rowStarts = new NativeList<int>(n / Cols + 4, Allocator.Temp);
                for (int k = 0; k < n; k++)
                    if (k == 0 || k - rowStarts[rowStarts.Length - 1] == Cols || Units[k].Role != Units[k - 1].Role) rowStarts.Add(k);
                int rows = rowStarts.Length;
                for (int r = 0; r < rows; r++)
                {
                    int start = rowStarts[r], end = r + 1 < rows ? rowStarts[r + 1] : n;
                    NativeArray<LayoutUnit> row = Units.GetSubArray(start, end - start);
                    row.Sort(new LeftFirst());
                    float depth = ((rows - 1) * 0.5f - r) * Spacing; // row 0 is the front
                    for (int c = 0; c < row.Length; c++)
                    {
                        float side = (c - (row.Length - 1) * 0.5f) * Spacing;
                        Offsets[row[c].Index] = Facing * depth + Across * side;
                    }
                }
                rowStarts.Dispose();
            }
        }
    }
}

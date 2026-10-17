using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Server-side dig areas (a Dig drag, Figma <c>v0.12 Fixes</c>, <c>Dig – drag an area</c>). Each area is a box
    /// of tiles its Diggers share out: a member takes the nearest diggable tile no other member has claimed, and
    /// takes the next when its dig job finishes, until none is left. Main thread only, touched at the tick
    /// boundary by <see cref="SimCommandSystem"/>.
    /// </summary>
    public sealed class DigAreaBook
    {
        /// <summary>Areas kept per owner; a new one past this drops the owner's oldest area without members.</summary>
        public const int MaxAreasPerOwner = 32;

        private sealed class Area
        {
            public int Id, Owner, Members;
            public int2 Min, Max;
            /// <summary>Tiles a member gave up on while they were still diggable (blocked), so nobody loops on them.</summary>
            public readonly HashSet<int2> Skipped = new HashSet<int2>();
        }

        private readonly Dictionary<int, Area> areas = new Dictionary<int, Area>();
        private readonly Dictionary<int, (int area, int2 claim, bool claimed)> members = new Dictionary<int, (int, int2, bool)>();
        private readonly HashSet<int2> claims = new HashSet<int2>();
        private readonly List<int> scratch = new List<int>();
        private int nextId = 1;

        /// <summary>Live areas.</summary>
        public int Count => areas.Count;

        /// <summary>A new area over the inclusive box for the owner; returns its id (never 0).</summary>
        public int Create(int owner, int2 min, int2 max)
        {
            scratch.Clear();
            foreach (Area a in areas.Values) if (a.Owner == owner) scratch.Add(a.Id);
            if (scratch.Count >= MaxAreasPerOwner)
            {
                scratch.Sort(); // ids grow, so the first is the oldest
                foreach (int id in scratch)
                    if (areas[id].Members == 0) { areas.Remove(id); break; }
            }
            int areaId = nextId++;
            if (nextId <= 0) nextId = 1;
            areas[areaId] = new Area { Id = areaId, Owner = owner, Min = math.min(min, max), Max = math.max(min, max) };
            return areaId;
        }

        /// <summary>True while the area exists.</summary>
        public bool Contains(int areaId) => areas.ContainsKey(areaId);

        /// <summary>The unit's area, or 0.</summary>
        public int AreaOf(int unitId) => members.TryGetValue(unitId, out var m) ? m.area : 0;

        /// <summary>The area's inclusive box. False when it is gone.</summary>
        public bool TryGetBox(int areaId, out int2 min, out int2 max)
        {
            min = max = default;
            if (!areas.TryGetValue(areaId, out Area a)) return false;
            min = a.Min;
            max = a.Max;
            return true;
        }

        /// <summary>True when the tile is inside the area's box.</summary>
        public bool InBox(int areaId, int2 tile) =>
            areas.TryGetValue(areaId, out Area a) && math.all(tile >= a.Min) && math.all(tile <= a.Max);

        /// <summary>Makes the unit a member of the area (leaving any other). False when the area is gone.</summary>
        public bool Join(int unitId, int areaId)
        {
            if (!areas.TryGetValue(areaId, out Area area)) return false;
            if (members.TryGetValue(unitId, out var m) && m.area == areaId) return true;
            Leave(unitId);
            members[unitId] = (areaId, default, false);
            area.Members++;
            return true;
        }

        /// <summary>Takes the unit out of its area (a new order, death, or the area is done). An area left without members is dropped.</summary>
        public void Leave(int unitId)
        {
            if (!members.Remove(unitId, out var m)) return;
            if (areas.TryGetValue(m.area, out Area area) && --area.Members <= 0) areas.Remove(m.area);
        }

        /// <summary>
        /// Picks the member's next tile: the nearest tile in its area that is <paramref name="diggable"/>, not given up
        /// on, not claimed by another member and <paramref name="reachable"/> from where the unit stands, and claims
        /// it. A previous claim that is still diggable was given up on (its job finished without digging it) and is
        /// skipped from now on. False, and the unit leaves the area, when nothing is left (or the unit is in no area).
        /// </summary>
        public bool TryNext(int unitId, float2 position, Func<int2, bool> diggable, Func<int2, int2, bool> reachable, out int2 tile)
        {
            tile = default;
            if (!members.TryGetValue(unitId, out var me)) return false;
            if (!areas.TryGetValue(me.area, out Area area)) { members.Remove(unitId); return false; }
            if (me.claimed && diggable(me.claim)) area.Skipped.Add(me.claim);
            claims.Clear();
            foreach (KeyValuePair<int, (int area, int2 claim, bool claimed)> other in members)
                if (other.Key != unitId && other.Value.area == me.area && other.Value.claimed) claims.Add(other.Value.claim);
            float best = float.MaxValue;
            bool found = false;
            int2 from = (int2)math.floor(position);
            for (int y = area.Min.y; y <= area.Max.y; y++)
            for (int x = area.Min.x; x <= area.Max.x; x++)
            {
                var t = new int2(x, y);
                if (claims.Contains(t) || area.Skipped.Contains(t) || !diggable(t)) continue;
                float d = math.distancesq(position, (float2)t + 0.5f);
                if (d < best && reachable(from, t)) { best = d; tile = t; found = true; }
            }
            if (!found)
            {
                Leave(unitId);
                return false;
            }
            members[unitId] = (me.area, tile, true);
            return true;
        }
    }
}

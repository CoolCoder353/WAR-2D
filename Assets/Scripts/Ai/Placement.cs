using System;
using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>How a placement search ended this think.</summary>
    public enum SearchResult : byte
    {
        /// <summary>It has a spot <see cref="PlacementRules"/> accepts.</summary>
        Found,
        /// <summary>No spot within the radius.</summary>
        NotFound,
        /// <summary>It ran out of tries this think; it goes on next think from where it stopped.</summary>
        Searching,
    }

    /// <summary>One placement search's progress (rings of anchors around a centre, nearest first), kept between thinks.</summary>
    public struct PlacementSearch
    {
        public BuildingType Type;
        public int2 Centre;
        public int Radius, Gap;
        public int Ring, Index;
        public bool Active;

        public static PlacementSearch Around(BuildingType type, int2 centre, int radius, int gap) =>
            new PlacementSearch { Type = type, Centre = centre, Radius = math.max(0, radius), Gap = math.max(0, gap), Active = true };

        public bool Same(BuildingType type, int2 centre, int radius, int gap) =>
            Active && Type == type && Centre.Equals(centre) && Radius == radius && Gap == gap;
    }

    /// <summary>
    /// The AI's building placement (plan Task 7): the same rules a client's preview uses (<see cref="PlacementRules"/>
    /// over the map as it knows it, explored ground only, the HQ inside one of its own options), searched outwards from
    /// a centre over rings of anchors. A search tries at most a budget of anchors per think and goes on next think, so a
    /// large search never stalls a tick. Every spot it returns has passed <see cref="PlacementRules.Check"/> on what the
    /// AI knows; its own and known buildings (ghosts included) and the placements it is waiting on count as used.
    /// </summary>
    public sealed class Placement
    {
        private readonly AiState state;
        private byte[] used = Array.Empty<byte>();
        private readonly List<int> marked = new List<int>(1024);
        private TeamView view;
        private readonly Func<int2, TileType> tileAt;
        private readonly Func<int2, bool> isUsed, noUnit, inOwnOptions;

        internal Placement(AiState state)
        {
            this.state = state;
            tileAt = t => view.TileAt(t);
            isUsed = IsUsed;
            noUnit = _ => false; // units are pushed out of a new footprint (the server's rule too)
            inOwnOptions = InOwnOptions;
        }

        /// <summary>Marks every building it knows (and the placements it waits on) as used ground.</summary>
        public void Refresh(TeamView view)
        {
            this.view = view;
            int cells = view.Width * view.Height;
            if (used.Length != cells) { used = new byte[cells]; marked.Clear(); }
            foreach (int i in marked) used[i] = 0;
            marked.Clear();
            for (int i = 0; i < view.Buildings.Count; i++)
            {
                AiBuilding b = view.Buildings[i];
                Mark(b.Anchor, state.SizeOf(b.Type));
            }
            foreach (PendingBuilding p in state.Pending) Mark(p.Anchor, state.SizeOf(p.Type));
        }

        /// <summary>Reserves a footprint it is about to place (so this think's other searches avoid it).</summary>
        public void Reserve(BuildingType type, int2 anchor) => Mark(anchor, state.SizeOf(type));

        private void Mark(int2 anchor, int2 size)
        {
            int2 start = Footprint.Start(anchor, size);
            for (int x = 0; x < size.x; x++)
                for (int y = 0; y < size.y; y++)
                {
                    int2 t = start + new int2(x, y);
                    if (t.x < 0 || t.y < 0 || t.x >= view.Width || t.y >= view.Height) continue;
                    int i = t.y * view.Width + t.x;
                    if (used[i] != 0) continue;
                    used[i] = 1;
                    marked.Add(i);
                }
        }

        /// <summary>True when a building it knows (or one it waits on) stands on the tile.</summary>
        public bool IsUsed(int2 t) => t.x >= 0 && t.y >= 0 && t.x < view.Width && t.y < view.Height && used[t.y * view.Width + t.x] != 0;

        /// <summary>True when its side has ever seen the tile (the server's explored test, on its own fog).</summary>
        public bool Explored(int2 t)
        {
            if (view.FogCellSize <= 0 || view.FogW <= 0 || view.FogH <= 0) return false;
            int2 cell = math.clamp(t / view.FogCellSize, 0, new int2(view.FogW - 1, view.FogH - 1));
            return view.Fog[cell.y * view.FogW + cell.x] != 0;
        }

        private bool InOwnOptions(int2 tile)
        {
            foreach (AiStartOption o in view.StartOptions)
                if (o.OwnerId == view.Owner && PlacementRules.InClearing(tile, o.Centre, view.HqClearRadius)) return true;
            return false;
        }

        private bool HasOwnOptions()
        {
            foreach (AiStartOption o in view.StartOptions) if (o.OwnerId == view.Owner) return true;
            return false;
        }

        /// <summary>
        /// The full check a client's preview makes, on what the AI knows: explored ground (not the HQ), then
        /// <see cref="PlacementRules.Check"/> (state, the one HQ, free ground, a Miner facing a gem, the HQ inside its options).
        /// </summary>
        public PlacementResult Check(BuildingType type, int2 anchor, float rotation)
        {
            int2 size = state.SizeOf(type);
            if (type != BuildingType.Base)
            {
                int2 start = Footprint.Start(anchor, size);
                for (int x = 0; x < size.x; x++)
                    for (int y = 0; y < size.y; y++)
                        if (!Explored(start + new int2(x, y))) return PlacementResult.Unexplored;
            }
            bool hasHq = state.HasHq || (type != BuildingType.Base ? false : state.HqPending);
            return PlacementRules.Check(type, anchor, rotation, size, view.State, hasHq, tileAt, isUsed, noUnit,
                type == BuildingType.Base && HasOwnOptions() ? inOwnOptions : null);
        }

        /// <summary>
        /// Goes on with a search for up to <paramref name="tries"/> anchors (taking what it uses from it). Found: the anchor
        /// and rotation pass <see cref="Check"/>. A Miner is tried only where it faces a gem; other buildings keep
        /// <see cref="PlacementSearch.Gap"/> free tiles of building around them and stay off tiles beside a gem (Miners' ground).
        /// </summary>
        public SearchResult Search(ref PlacementSearch s, ref int tries, out int2 anchor, out float rotation)
        {
            anchor = default;
            rotation = 0f;
            if (view == null) return SearchResult.NotFound;
            int2 size = state.SizeOf(s.Type);
            bool hasOptions = s.Type == BuildingType.Base && HasOwnOptions();
            while (tries > 0)
            {
                if (s.Ring > s.Radius) { s.Active = false; return SearchResult.NotFound; }
                int count = s.Ring == 0 ? 1 : 8 * s.Ring;
                if (s.Index >= count) { s.Ring++; s.Index = 0; continue; }
                int2 a = s.Centre + RingOffset(s.Ring, s.Index);
                s.Index++;
                tries--;
                if (state.IsBlocked(a)) continue;
                if (s.Type == BuildingType.Miner)
                {
                    for (int q = 0; q < 4; q++)
                    {
                        float rot = q * 90f;
                        if (view.TileAt(a + MinerRules.FacingOffset(rot)) != TileType.Gem) continue;
                        if (!Quick(a, size, 0, false, false, false)) break;
                        if (Check(BuildingType.Miner, a, rot) != PlacementResult.Ok) continue;
                        anchor = a;
                        rotation = rot;
                        return SearchResult.Found;
                    }
                    continue;
                }
                if (!Quick(a, size, s.Gap, true, s.Type == BuildingType.Base, hasOptions)) continue;
                if (Check(s.Type, a, 0f) != PlacementResult.Ok) continue;
                anchor = a;
                return SearchResult.Found;
            }
            return SearchResult.Searching;
        }

        /// <summary>A cheap filter before the full check: free, explored ground in the footprint, the gap clear of buildings.</summary>
        private bool Quick(int2 anchor, int2 size, int gap, bool avoidGems, bool hq, bool options)
        {
            int2 start = Footprint.Start(anchor, size);
            for (int x = -gap; x < size.x + gap; x++)
                for (int y = -gap; y < size.y + gap; y++)
                {
                    int2 t = start + new int2(x, y);
                    bool inside = x >= 0 && y >= 0 && x < size.x && y < size.y;
                    if (!inside) { if (IsUsed(t)) return false; continue; }
                    if (view.TileAt(t) != TileType.Ground || IsUsed(t)) return false;
                    if (!hq && !Explored(t)) return false;
                    if (hq && options && !InOwnOptions(t)) return false;
                    if (avoidGems && (view.TileAt(t + new int2(1, 0)) == TileType.Gem || view.TileAt(t - new int2(1, 0)) == TileType.Gem ||
                                      view.TileAt(t + new int2(0, 1)) == TileType.Gem || view.TileAt(t - new int2(0, 1)) == TileType.Gem)) return false;
                }
            return true;
        }

        /// <summary>The <paramref name="index"/>-th tile of the square ring at Chebyshev radius <paramref name="ring"/> (8 × ring tiles; ring 0 is the centre).</summary>
        public static int2 RingOffset(int ring, int index)
        {
            if (ring <= 0) return int2.zero;
            int side = index / (2 * ring), k = index % (2 * ring);
            switch (side)
            {
                case 0: return new int2(-ring + k, -ring);
                case 1: return new int2(ring, -ring + k);
                case 2: return new int2(ring - k, ring);
                default: return new int2(-ring, ring - k);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Ai
{
    /// <summary>How an AI's units can get from one tile to another over the ground its side knows (v0.21 §3).</summary>
    public enum Reach : byte
    {
        /// <summary>A path over floor alone.</summary>
        Open,
        /// <summary>Only through player walls: a breach is needed.</summary>
        Walled,
        /// <summary>Not even through walls (rock, the border), or an end isn't near floor.</summary>
        Cut,
    }

    /// <summary>
    /// An AI's reach map (v0.21 §3): the floor tiles of the map as its side knows it (wall blueprints count as floor),
    /// labelled into 4-connected components (units can't cut a corner diagonally, so a component is exactly where they can
    /// walk), and the player wall tiles that join components. <see cref="Query"/> answers Open, Walled (with the cheapest
    /// breach: Dijkstra over components and wall tiles, a wall tile costing what it takes to break) or Cut. A relabel
    /// after the terrain changes is spread over calls to <see cref="Step"/>; queries use the last finished labels meanwhile.
    /// </summary>
    public sealed class ReachMap
    {
        private readonly int width, height, tilesPerStep;
        // The labels queries read, and the ones a relabel writes (swapped when it finishes).
        private int[] labels, building;
        private readonly Stack<int> stack = new Stack<int>(1024);
        private int scan;          // the next tile index the relabel looks at
        private int nextLabel;     // the next component id
        private bool relabelling, dirty;
        // The player wall tiles at the last finished relabel.
        private readonly List<int> walls = new List<int>(256), wallsBuilding = new List<int>(256);

        /// <param name="tilesPerStep">At most this many tiles labelled per <see cref="Step"/> (the flood fill's work).</param>
        public ReachMap(int width, int height, int tilesPerStep = 1 << 20)
        {
            this.width = width;
            this.height = height;
            this.tilesPerStep = math.max(1, tilesPerStep);
            labels = new int[width * height];
            building = new int[width * height];
            for (int i = 0; i < labels.Length; i++) labels[i] = -1;
        }

        /// <summary>The map size it labels.</summary>
        public int Width => width;
        public int Height => height;

        /// <summary>Bumps each time a relabel finishes (a cached answer from an older version is stale).</summary>
        public int Version { get; private set; }

        /// <summary>True while a relabel is under way or due.</summary>
        public bool Pending => relabelling || dirty;

        /// <summary>Components at the last finished relabel.</summary>
        public int Components { get; private set; }

        /// <summary>Relabels from <paramref name="tiles"/> at once.</summary>
        public void Rebuild(byte[] tiles)
        {
            dirty = false;
            Begin();
            while (!Continue(tiles, int.MaxValue)) { }
        }

        /// <summary>Marks a relabel due (a tile changed between floor and not); <see cref="Step"/> does it.</summary>
        public void MarkDirty() => dirty = true;

        /// <summary>Forgets cached answers (a wall's health or presence changed: its breach costs differ now).</summary>
        public void ForgetAnswers() => cache.Clear();

        /// <summary>Goes on with a due relabel for up to the step's tiles; true when one finished on this call.</summary>
        public bool Step(byte[] tiles)
        {
            if (!relabelling)
            {
                if (!dirty) return false;
                dirty = false;
                Begin();
            }
            return Continue(tiles, tilesPerStep);
        }

        private void Begin()
        {
            relabelling = true;
            scan = 0;
            nextLabel = 0;
            stack.Clear();
            wallsBuilding.Clear();
            for (int i = 0; i < building.Length; i++) building[i] = -1;
        }

        /// <summary>Labels up to <paramref name="budget"/> tiles; true when the relabel finished (and was swapped in).</summary>
        private bool Continue(byte[] tiles, int budget)
        {
            int n = width * height;
            while (budget > 0)
            {
                if (stack.Count > 0)
                {
                    int i = stack.Pop();
                    budget--;
                    int x = i % width, y = i / width, label = building[i];
                    if (x > 0) Push(tiles, i - 1, label);
                    if (x < width - 1) Push(tiles, i + 1, label);
                    if (y > 0) Push(tiles, i - width, label);
                    if (y < height - 1) Push(tiles, i + width, label);
                    continue;
                }
                if (scan >= n) break;
                int t = scan++;
                budget--;
                byte kind = tiles[t];
                if ((TileType)kind == TileType.PlayerWall) { wallsBuilding.Add(t); continue; }
                if (building[t] >= 0 || !TileRules.IsFloor(kind)) continue;
                building[t] = nextLabel++;
                stack.Push(t);
            }
            if (stack.Count > 0 || scan < n) return false;
            (labels, building) = (building, labels);
            walls.Clear();
            walls.AddRange(wallsBuilding);
            Components = nextLabel;
            relabelling = false;
            Version++;
            cache.Clear();
            return true;
        }

        private void Push(byte[] tiles, int i, int label)
        {
            if (building[i] >= 0 || !TileRules.IsFloor(tiles[i])) return;
            building[i] = label;
            stack.Push(i);
        }

        /// <summary>The floor component at a tile (last finished relabel), or -1 off the floor or the map.</summary>
        public int ComponentAt(int2 t) => t.x >= 0 && t.y >= 0 && t.x < width && t.y < height ? labels[t.y * width + t.x] : -1;

        /// <summary>The nearest labelled floor tile within <paramref name="radius"/> (square rings, nearest first), or <paramref name="t"/> when none.</summary>
        public int2 NearestFloor(int2 t, int radius)
        {
            if (ComponentAt(t) >= 0) return t;
            for (int r = 1; r <= radius; r++)
                for (int k = 0; k < 8 * r; k++)
                {
                    int2 c = t + Placement.RingOffset(r, k);
                    if (ComponentAt(c) >= 0) return c;
                }
            return t;
        }

        // ---- queries ----

        private readonly Dictionary<long, (Reach Reach, float Cost, int2[] Breach)> cache = new Dictionary<long, (Reach, float, int2[])>();
        private readonly Dictionary<int, float> dist = new Dictionary<int, float>();
        private readonly Dictionary<int, int> parent = new Dictionary<int, int>();
        private readonly HashSet<int> wallSet = new HashSet<int>();
        private readonly Dictionary<int, List<int>> wallsOf = new Dictionary<int, List<int>>();
        private readonly List<(float Cost, int Node)> heap = new List<(float, int)>(256);

        /// <summary>
        /// How its units get from <paramref name="from"/> to <paramref name="to"/> (each moved to the nearest floor within
        /// <paramref name="snap"/> tiles: a building's anchor, a unit off the grid). For <see cref="Reach.Walled"/>,
        /// <paramref name="breach"/> gets the wall tiles to break, from the <paramref name="from"/> side, and
        /// <paramref name="cost"/> their summed <paramref name="wallCost"/> (by tile index; at least a small positive cost
        /// each). <paramref name="tiles"/> are the known tiles now: a wall gone since the relabel isn't counted.
        /// </summary>
        public Reach Query(int2 from, int2 to, byte[] tiles, Func<int, float> wallCost, List<int2> breach, out float cost, int snap = 3)
        {
            breach?.Clear();
            cost = 0f;
            int a = ComponentAt(NearestFloor(from, snap)), b = ComponentAt(NearestFloor(to, snap));
            if (a < 0 || b < 0) return Reach.Cut;
            if (a == b) return Reach.Open;
            long key = ((long)a << 32) | (uint)b;
            if (!cache.TryGetValue(key, out var hit))
            {
                hit = Search(a, b, tiles, wallCost);
                cache[key] = hit;
            }
            cost = hit.Cost;
            if (breach != null && hit.Breach != null) breach.AddRange(hit.Breach);
            return hit.Reach;
        }

        // Node ids: a component c is -(c + 1); a wall tile is its tile index (>= 0).
        private (Reach, float, int2[]) Search(int from, int to, byte[] tiles, Func<int, float> wallCost)
        {
            wallSet.Clear();
            foreach (List<int> l in wallsOf.Values) l.Clear();
            // Every wall tile of the last relabel: one destroyed since is a free passage (its tile isn't labelled yet).
            foreach (int w in walls) if ((TileType)tiles[w] == TileType.PlayerWall || TileRules.IsFloor(tiles[w])) wallSet.Add(w);
            float Cost(int w) => (TileType)tiles[w] == TileType.PlayerWall ? math.max(0.01f, wallCost(w)) : 0f;
            if (wallSet.Count == 0) return (Reach.Cut, 0f, null);
            foreach (int w in wallSet)
            {
                int x = w % width, y = w / width;
                if (x > 0) Link(w, labels[w - 1]);
                if (x < width - 1) Link(w, labels[w + 1]);
                if (y > 0) Link(w, labels[w - width]);
                if (y < height - 1) Link(w, labels[w + width]);
            }
            dist.Clear();
            parent.Clear();
            heap.Clear();
            int start = -(from + 1), goal = -(to + 1);
            dist[start] = 0f;
            HeapPush(0f, start);
            while (heap.Count > 0)
            {
                (float d, int node) = HeapPop();
                if (d > dist[node]) continue;
                if (node == goal)
                {
                    var path = new List<int2>();
                    for (int n = parent.TryGetValue(goal, out int p0) ? p0 : start; n != start; n = parent[n])
                        if (n >= 0 && (TileType)tiles[n] == TileType.PlayerWall) path.Add(new int2(n % width, n / width));
                    path.Reverse();
                    // Every wall on the way destroyed since the relabel: open already.
                    return (path.Count > 0 ? Reach.Walled : Reach.Open, d, path.ToArray());
                }
                if (node < 0)
                {
                    if (!wallsOf.TryGetValue(-node - 1, out List<int> adj)) continue;
                    foreach (int w in adj) Relax(node, w, d + Cost(w));
                }
                else
                {
                    int x = node % width, y = node / width;
                    // Into a neighbouring wall tile (a thicker wall), or out into a floor component.
                    if (x > 0) Next(node, node - 1, d, Cost);
                    if (x < width - 1) Next(node, node + 1, d, Cost);
                    if (y > 0) Next(node, node - width, d, Cost);
                    if (y < height - 1) Next(node, node + width, d, Cost);
                }
            }
            return (Reach.Cut, 0f, null);
        }

        private void Next(int from, int tile, float d, Func<int, float> wallCost)
        {
            if (wallSet.Contains(tile)) { Relax(from, tile, d + wallCost(tile)); return; }
            int c = labels[tile];
            if (c >= 0) Relax(from, -(c + 1), d);
        }

        private void Link(int wall, int component)
        {
            if (component < 0) return;
            if (!wallsOf.TryGetValue(component, out List<int> l)) wallsOf[component] = l = new List<int>(8);
            if (l.Count == 0 || l[l.Count - 1] != wall) l.Add(wall);
        }

        private void Relax(int from, int to, float d)
        {
            if (dist.TryGetValue(to, out float old) && old <= d) return;
            dist[to] = d;
            parent[to] = from;
            HeapPush(d, to);
        }

        private void HeapPush(float cost, int node)
        {
            heap.Add((cost, node));
            int i = heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heap[p].Cost <= heap[i].Cost) break;
                (heap[p], heap[i]) = (heap[i], heap[p]);
                i = p;
            }
        }

        private (float, int) HeapPop()
        {
            var top = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < heap.Count && heap[l].Cost < heap[m].Cost) m = l;
                if (r < heap.Count && heap[r].Cost < heap[m].Cost) m = r;
                if (m == i) break;
                (heap[m], heap[i]) = (heap[i], heap[m]);
                i = m;
            }
            return top;
        }
    }
}

using System;
using System.Collections.Generic;
using Config;
using Unity.Collections;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace WAR2D.World
{
    /// <summary>What a region of the map is for.</summary>
    public enum RegionKind : byte
    {
        /// <summary>A base site: the HQ goes here.</summary>
        Start,
        /// <summary>A contested resource field.</summary>
        Expansion,
        /// <summary>A large open battlefield between bases.</summary>
        Central,
    }

    /// <summary>One open region of the planned map: a core disc of floor and seeded lobes within <see cref="Radius"/>.</summary>
    public readonly struct MapRegion
    {
        public readonly int2 Centre;
        public readonly int Radius;
        public readonly RegionKind Kind;
        public MapRegion(int2 centre, int radius, RegionKind kind) { Centre = centre; Radius = radius; Kind = kind; }
    }

    /// <summary>A lane between two regions (indices into <see cref="MapLayout.Regions"/>), with its planned chokepoint.</summary>
    public readonly struct MapLane
    {
        public readonly int A, B;
        /// <summary>Lane width in tiles outside the chokepoint.</summary>
        public readonly int Width;
        /// <summary>Chokepoint width (0 = none).</summary>
        public readonly int ChokeWidth;
        /// <summary>Path length in tiles.</summary>
        public readonly int Length;
        public MapLane(int a, int b, int width, int chokeWidth, int length) { A = a; B = b; Width = width; ChokeWidth = chokeWidth; Length = length; }
    }

    /// <summary>A generated map: the tiles (owned by the caller) and the plan they were carved from.</summary>
    public readonly struct MapLayout : IDisposable
    {
        public readonly NativeArray<byte> Tiles;
        public readonly MapRegion[] Regions;
        public readonly MapLane[] Lanes;
        /// <summary>Centres of the start sites, in generation order.</summary>
        public readonly int2[] StartSites;
        /// <summary>Centres of the expansions.</summary>
        public readonly int2[] Expansions;
        /// <summary>True when the layout met every fairness test (false: the best of the attempts was used).</summary>
        public readonly bool Fair;
        /// <summary>The attempt (0 = first) the map was built from.</summary>
        public readonly int Attempt;

        /// <summary>Disposes the tiles.</summary>
        public void Dispose() { if (Tiles.IsCreated) Tiles.Dispose(); }

        public MapLayout(NativeArray<byte> tiles, MapRegion[] regions, MapLane[] lanes, int2[] starts, int2[] expansions, bool fair, int attempt)
        {
            Tiles = tiles; Regions = regions; Lanes = lanes; StartSites = starts; Expansions = expansions; Fair = fair; Attempt = attempt;
        }
    }

    /// <summary>
    /// Builds seeded region-graph maps: start sites, rich expansions and central areas joined by a looped graph of
    /// lanes, some with narrow chokepoints. Every decision that shapes tiles is integer arithmetic on a
    /// <see cref="Random"/> seeded from the map seed, so every machine builds the same map. A pure function of
    /// (size, seed, config). The layout is planned and checked first (cheap); only the best plan is rasterised.
    /// </summary>
    public static class RegionMapGenerator
    {
        /// <summary>Radius of a start site: fits the 5x5 HQ and a base around it.</summary>
        public const int StartRadius = 12;

        /// <summary>Layout attempts before the best-scoring one is used.</summary>
        public const int MaxAttempts = 32;

        /// <summary>Test hook: when set, receives one line per start site scored.</summary>
        internal static List<string> Diagnostics;

        private const byte Floor = (byte)TileType.Ground, Rock = (byte)TileType.Wall, Gem = (byte)TileType.Gem, BorderTile = (byte)TileType.Border;
        private const int RoughenPercent = 20;

        private static readonly int2[] Steps = { new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1) };
        /// <summary>Sixteen compass directions, x1000 (integer, so every machine carves the same shapes).</summary>
        private static readonly int2[] Directions =
        {
            new int2(1000, 0), new int2(924, 383), new int2(707, 707), new int2(383, 924), new int2(0, 1000), new int2(-383, 924), new int2(-707, 707), new int2(-924, 383),
            new int2(-1000, 0), new int2(-924, -383), new int2(-707, -707), new int2(-383, -924), new int2(0, -1000), new int2(383, -924), new int2(707, -707), new int2(924, -383),
        };
        private static readonly int2[] Around =
        {
            new int2(1, 0), new int2(1, 1), new int2(0, 1), new int2(-1, 1), new int2(-1, 0), new int2(-1, -1), new int2(0, -1), new int2(1, -1),
        };

        /// <summary>The layout row (start sites, expansions, central areas) used for a map size.</summary>
        public static (int starts, int expansions, int centrals) Counts(MapConfig cfg, int size)
        {
            int row = 0;
            for (int i = 0; i < cfg.LayoutSizes.Length; i++) if (cfg.LayoutSizes[i] <= size) row = i;
            return (cfg.StartSites[row], cfg.Expansions[row], cfg.CentralAreas[row]);
        }

        /// <summary>Region-radius and lane-width scales (percent) for a map size; 100 when the config has none.</summary>
        public static (int region, int lane) Scales(MapConfig cfg, int size)
        {
            int row = 0;
            for (int i = 0; i < cfg.LayoutSizes.Length; i++) if (cfg.LayoutSizes[i] <= size) row = i;
            return (row < cfg.RegionScale.Length ? cfg.RegionScale[row] : 100, row < cfg.LaneScale.Length ? cfg.LaneScale[row] : 100);
        }

        /// <summary>Depth of the cave band for a map size; 0 when the config has none.</summary>
        public static int CaveDepth(MapConfig cfg, int size)
        {
            int row = 0;
            for (int i = 0; i < cfg.LayoutSizes.Length; i++) if (cfg.LayoutSizes[i] <= size) row = i;
            return row < cfg.CaveDepth.Length ? cfg.CaveDepth[row] : 0;
        }

        /// <summary>Radius of a start region for a region scale: the HQ's core plus room for a base, at most 1.75x the core.</summary>
        public static int StartRegionRadius(int regionScale) => math.clamp(StartRadius * regionScale / 100, StartRadius, StartRadius * 7 / 4);

        /// <summary>Generates the map with the config's layout settings. <paramref name="allocator"/> backs the tiles.</summary>
        public static MapLayout Generate(int size, uint seed, float gemChance, Allocator allocator) =>
            Generate(size, seed, gemChance, allocator, ConfigLoader.LoadConfig().Match.Map);

        /// <summary>Generates the map with explicit layout settings.</summary>
        public static MapLayout Generate(int size, uint seed, float gemChance, Allocator allocator, MapConfig cfg)
        {
            if (size < MapGenerator.MinSize)
                throw new ArgumentOutOfRangeException(nameof(size), size, $"a generated map must be at least {MapGenerator.MinSize} tiles across");

            // Plan until a layout is fair; the retry count is a pure function of the seed, so clients agree.
            Plan best = null;
            int bestAttempt = 0;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                Plan plan = MakePlan(size, seed, attempt, cfg);
                if (best == null || plan.Score < best.Score) { best = plan; bestAttempt = attempt; }
                if (plan.Score == 0) break;
            }
            return Rasterise(size, seed, gemChance, allocator, cfg, best, bestAttempt);
        }

        // ---- Planning -------------------------------------------------------------------------------------

        private sealed class Plan
        {
            public readonly List<MapRegion> Regions = new List<MapRegion>();
            public readonly List<LanePlan> Lanes = new List<LanePlan>();
            public int Score;
        }

        private struct LanePlan
        {
            public int A, B, Width, ChokeWidth, ChokeFrom, ChokeLen, Length, Dist;
            public int2 M1, M2;
            public bool HasM2;
        }

        private static uint SubSeed(uint seed, int attempt)
        {
            uint s = unchecked((seed == 0 ? 1u : seed) * 2654435761u + (uint)attempt * 40503u + 12345u);
            return s == 0 ? 1u : s;
        }

        private static Plan MakePlan(int size, uint seed, int attempt, MapConfig cfg)
        {
            var rng = new Random(SubSeed(seed, attempt));
            var plan = new Plan();
            (int starts, int expansions, int centrals) = Counts(cfg, size);

            int scale = Scales(cfg, size).region;
            int centralRadius = math.clamp(size / 8, 16, 40);
            // Start sites first: they are the most constrained.
            for (int i = 0; i < starts; i++) Place(plan.Regions, StartRegionRadius(scale), RegionKind.Start, ref rng, size, (long)(cfg.MinStartDistance * size));
            for (int i = 0; i < centrals; i++) Place(plan.Regions, (centralRadius + rng.NextInt(0, 5)) * scale / 100, RegionKind.Central, ref rng, size, 0);
            for (int i = 0; i < expansions; i++) Place(plan.Regions, (16 + rng.NextInt(0, 7)) * scale / 100, RegionKind.Expansion, ref rng, size, 0);

            BuildGraph(plan, size, cfg, ref rng);
            plan.Score = Score(plan, size, cfg);
            return plan;
        }

        /// <summary>Dart-throws a region centre, easing the spacing if the map is crowded.</summary>
        private static void Place(List<MapRegion> placed, int radius, RegionKind kind, ref Random rng, int size, long minStartGap)
        {
            int margin = radius + 3;
            int2 candidate = new int2(size / 2, size / 2);
            for (int t = 0; t < 600; t++)
            {
                candidate = new int2(rng.NextInt(margin, size - margin), rng.NextInt(margin, size - margin));
                long f = t < 300 ? 100 : 100 - (t - 300) * 40 / 300; // spacing eases from 100 % to 60 %
                bool ok = true;
                foreach (MapRegion r in placed)
                {
                    long gap = (r.Radius + radius) * f / 100 + 6;
                    // Two start sites keep the configured minimum distance (the straight-line gap never exceeds the lane distance).
                    if (kind == RegionKind.Start && r.Kind == RegionKind.Start) gap = math.max(gap, minStartGap * f / 100);
                    long dx = candidate.x - r.Centre.x, dy = candidate.y - r.Centre.y;
                    if (dx * dx + dy * dy < gap * gap) { ok = false; break; }
                }
                if (ok) break;
            }
            placed.Add(new MapRegion(candidate, radius, kind));
        }

        /// <summary>Gabriel graph (a non-crossing subgraph of the Delaunay triangulation), its MST, and a third of the other edges as loops.</summary>
        private static void BuildGraph(Plan plan, int size, MapConfig cfg, ref Random rng)
        {
            int n = plan.Regions.Count;
            var d2 = new long[n, n];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                long dx = plan.Regions[i].Centre.x - plan.Regions[j].Centre.x, dy = plan.Regions[i].Centre.y - plan.Regions[j].Centre.y;
                d2[i, j] = dx * dx + dy * dy;
            }

            var gabriel = new List<(long d, int a, int b)>();
            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                bool empty = true;
                for (int k = 0; k < n && empty; k++)
                    if (k != i && k != j && d2[i, k] + d2[j, k] < d2[i, j]) empty = false;
                if (empty) gabriel.Add((d2[i, j], i, j));
            }
            gabriel.Sort((x, y) => x.d != y.d ? x.d.CompareTo(y.d) : x.a != y.a ? x.a.CompareTo(y.a) : x.b.CompareTo(y.b));

            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

            var chosen = new HashSet<(int, int)>();
            var extra = new List<(long d, int a, int b)>();
            foreach (var e in gabriel)
            {
                int ra = Find(e.a), rb = Find(e.b);
                if (ra != rb) { parent[ra] = rb; chosen.Add((e.a, e.b)); }
                else extra.Add(e);
            }
            for (int i = 0; i < (extra.Count + 2) / 3; i++) chosen.Add((extra[i].a, extra[i].b));

            // A start site gets two ways out, so one wall can't seal it.
            var degree = new int[n];
            foreach (var e in chosen) { degree[e.Item1]++; degree[e.Item2]++; }
            for (int i = 0; i < n; i++)
            {
                while (plan.Regions[i].Kind == RegionKind.Start && degree[i] < 2 && n > 2)
                {
                    int best = -1;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i || chosen.Contains((math.min(i, j), math.max(i, j)))) continue;
                        if (best < 0 || d2[i, j] < d2[i, best]) best = j;
                    }
                    if (best < 0) break;
                    chosen.Add((math.min(i, best), math.max(i, best)));
                    degree[i]++; degree[best]++;
                }
            }

            var ordered = new List<(int, int)>(chosen);
            ordered.Sort();
            foreach ((int a, int b) in ordered) plan.Lanes.Add(MakeLane(plan, a, b, size, cfg, ref rng));
        }

        private static LanePlan MakeLane(Plan plan, int a, int b, int size, MapConfig cfg, ref Random rng)
        {
            MapRegion ra = plan.Regions[a], rb = plan.Regions[b];
            var lane = new LanePlan { A = a, B = b, Width = rng.NextInt(cfg.LaneWidthMin, cfg.LaneWidthMax + 1) * Scales(cfg, size).lane / 100 };
            long dx = rb.Centre.x - ra.Centre.x, dy = rb.Centre.y - ra.Centre.y;
            lane.HasM2 = rng.NextInt(0, 2) == 1;
            lane.M1 = Mid(ra.Centre, dx, dy, 1, lane.HasM2 ? 3 : 2, size, ref rng);
            if (lane.HasM2) lane.M2 = Mid(ra.Centre, dx, dy, 2, 3, size, ref rng);

            lane.Length = Chebyshev(ra.Centre, lane.M1) + (lane.HasM2 ? Chebyshev(lane.M1, lane.M2) + Chebyshev(lane.M2, rb.Centre) : Chebyshev(lane.M1, rb.Centre));

            lane.Dist = Octagonal(ra.Centre, lane.M1) + (lane.HasM2 ? Octagonal(lane.M1, lane.M2) + Octagonal(lane.M2, rb.Centre) : Octagonal(lane.M1, rb.Centre));

            // The chokepoint sits in the stretch between the two regions' discs.
            int free = lane.Length - ra.Radius - rb.Radius - 4;
            if (rng.NextInt(0, 100) < (int)(cfg.ChokepointShare * 100f + 0.5f))
            {
                int len = rng.NextInt(8, 21);
                if (free >= len)
                {
                    lane.ChokeLen = len;
                    lane.ChokeWidth = rng.NextInt(cfg.ChokeWidthMin, cfg.ChokeWidthMax + 1);
                    lane.ChokeFrom = ra.Radius + 2 + rng.NextInt(0, free - len + 1);
                }
            }
            return lane;
        }

        /// <summary>A point num/den of the way from <paramref name="from"/> along (dx, dy), pushed sideways by up to 18 % of the lane's length.</summary>
        private static int2 Mid(int2 from, long dx, long dy, int num, int den, int size, ref Random rng)
        {
            long jitter = rng.NextInt(-18, 19);
            long x = from.x + dx * num / den - dy * jitter / 100, y = from.y + dy * num / den + dx * jitter / 100;
            return new int2((int)math.clamp(x, 4, size - 5), (int)math.clamp(y, 4, size - 5));
        }

        /// <summary>Path length in tiles, never less than the straight-line distance: max + 0.414 x min.</summary>
        private static int Octagonal(int2 a, int2 b)
        {
            int dx = math.abs(a.x - b.x), dy = math.abs(a.y - b.y);
            return math.max(dx, dy) + math.min(dx, dy) * 414 / 1000;
        }

        private static int Chebyshev(int2 a, int2 b) => math.max(math.abs(a.x - b.x), math.abs(a.y - b.y));

        /// <summary>
        /// Fairness: each start site's nearest other site and nearest expansion, by lane distance, must lie in the
        /// configured band. Returns 0 when every site passes, otherwise a score that grows with the violations.
        /// </summary>
        private static int Score(Plan plan, int size, MapConfig cfg)
        {
            int n = plan.Regions.Count;
            long min = (long)(cfg.MinStartDistance * size), max = (long)(cfg.MaxStartDistance * size);
            var dist = new long[n];
            var done = new bool[n];
            long score = 0;
            for (int s = 0; s < n; s++)
            {
                if (plan.Regions[s].Kind != RegionKind.Start) continue;
                for (int i = 0; i < n; i++) { dist[i] = long.MaxValue; done[i] = false; }
                dist[s] = 0;
                for (int step = 0; step < n; step++)
                {
                    int u = -1;
                    for (int i = 0; i < n; i++) if (!done[i] && dist[i] != long.MaxValue && (u < 0 || dist[i] < dist[u])) u = i;
                    if (u < 0) break;
                    done[u] = true;
                    foreach (LanePlan l in plan.Lanes)
                    {
                        int v = l.A == u ? l.B : l.B == u ? l.A : -1;
                        if (v >= 0 && dist[u] + l.Dist < dist[v]) dist[v] = dist[u] + l.Dist;
                    }
                }
                long nearestStart = long.MaxValue, nearestExpansion = long.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (i == s || dist[i] == long.MaxValue) continue;
                    if (plan.Regions[i].Kind == RegionKind.Start) nearestStart = math.min(nearestStart, dist[i]);
                    if (plan.Regions[i].Kind == RegionKind.Expansion) nearestExpansion = math.min(nearestExpansion, dist[i]);
                }
                bool anyExpansion = false;
                foreach (MapRegion r in plan.Regions) if (r.Kind == RegionKind.Expansion) anyExpansion = true;
                if (Diagnostics != null) Diagnostics.Add($"site {s}: start {(nearestStart == long.MaxValue ? -1 : nearestStart)} exp {(nearestExpansion == long.MaxValue ? -1 : nearestExpansion)} band {min}-{max}");
                if (nearestStart != long.MaxValue && nearestStart < min) score += 1000 + (min - nearestStart);
                if (nearestStart != long.MaxValue && nearestStart > max) score += 1000 + (nearestStart - max);
                if (anyExpansion && nearestExpansion > max) score += 1000 + (nearestExpansion == long.MaxValue ? max : nearestExpansion - max);
            }
            return (int)math.min(score, int.MaxValue);
        }

        // ---- Rasterising ----------------------------------------------------------------------------------

        private static MapLayout Rasterise(int size, uint seed, float gemChance, Allocator allocator, MapConfig cfg, Plan plan, int attempt)
        {
            var tiles = new NativeArray<byte>(size * size, allocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < tiles.Length; i++) tiles[i] = Rock;

            // Regions as lobed blobs, rock outcrops inside the open ground, the start sites' cores cleared again (a
            // neighbour's outcrop never lands on an HQ site), then the lanes, which run centre to centre and so cut a
            // clear way through any outcrop in their path.
            var shapes = new Random(SubSeed(seed, attempt) ^ 0x51ED27u | 1u);
            foreach (MapRegion r in plan.Regions) CarveRegion(tiles, size, r, ref shapes);
            foreach (MapRegion r in plan.Regions)
                if (r.Kind != RegionKind.Start) AddOutcrops(tiles, size, r, cfg.OutcropShare, ref shapes);
            foreach (MapRegion r in plan.Regions)
                if (r.Kind == RegionKind.Start) CarveDisc(tiles, size, r.Centre, StartRadius);
            // Chokepoints (and a margin around them) are protected, so the cave band never widens them.
            var protect = new NativeArray<byte>(tiles.Length, Allocator.Temp);
            int depth = CaveDepth(cfg, size);
            foreach (LanePlan l in plan.Lanes) CarveLane(tiles, size, plan, l, protect, depth + 2);
            GrowCaves(tiles, size, protect, depth, SubSeed(seed, attempt));
            Roughen(tiles, size, SubSeed(seed, attempt), protect);
            protect.Dispose();
            FillUnreached(tiles, size, plan.Regions[0].Centre);

            var rng = new Random(SubSeed(seed, attempt) ^ 0x9E3779B9u | 1u);
            var starts = new List<int2>();
            var expansions = new List<int2>();
            for (int i = 0; i < plan.Regions.Count; i++)
            {
                MapRegion r = plan.Regions[i];
                if (r.Kind == RegionKind.Expansion)
                {
                    expansions.Add(r.Centre);
                    int count = cfg.ExpansionGemTiles * 3 / 4 + rng.NextInt(0, cfg.ExpansionGemTiles / 2 + 1);
                    GrowGems(tiles, size, r.Centre, r.Radius + 4, count, int2.zero, false, ref rng);
                }
                else if (r.Kind == RegionKind.Start)
                {
                    starts.Add(r.Centre);
                    AddVein(tiles, size, plan, i, ref rng);
                }
            }

            SprinkleGems(tiles, size, ref rng, gemChance);
            WriteBorder(tiles, size);

            var lanes = new MapLane[plan.Lanes.Count];
            for (int i = 0; i < lanes.Length; i++)
            {
                LanePlan l = plan.Lanes[i];
                lanes[i] = new MapLane(l.A, l.B, l.Width, l.ChokeWidth, l.Dist);
            }
            return new MapLayout(tiles, plan.Regions.ToArray(), lanes, starts.ToArray(), expansions.ToArray(), plan.Score == 0, attempt);
        }

        private static void CarveDisc(NativeArray<byte> tiles, int size, int2 centre, int radius) => FillDisc(tiles, size, centre, radius, Floor);

        /// <summary>A region's floor: a core disc and three to five seeded lobes, all within its radius. A start site's core is the HQ clearing.</summary>
        private static void CarveRegion(NativeArray<byte> tiles, int size, MapRegion region, ref Random rng)
        {
            int r = region.Radius;
            uint noiseSeed = rng.NextUInt() | 1u;
            if (region.Kind == RegionKind.Start) CarveDisc(tiles, size, region.Centre, math.min(r, StartRadius)); // the HQ clearing stays round and whole
            else CarveCave(tiles, size, region.Centre, math.max(4, r * 55 / 100), noiseSeed);
            int lobes = 3 + rng.NextInt(0, 3);
            for (int i = 0; i < lobes; i++)
            {
                int lobe = math.max(3, r * rng.NextInt(35, 56) / 100);
                int reach = rng.NextInt(r / 4, math.max(r / 4, r - lobe) + 1);
                int2 dir = Directions[rng.NextInt(0, Directions.Length)];
                CarveCave(tiles, size, region.Centre + dir * reach / 1000, lobe, noiseSeed);
            }
        }

        /// <summary>
        /// A disc whose edge wanders with seeded value noise between about 0.7x and 1.2x <paramref name="radius"/>, so a
        /// region reads as a cave, not a circle. The middle (half the radius) is always carved, so lanes meeting at the centre connect.
        /// </summary>
        private static void CarveCave(NativeArray<byte> tiles, int size, int2 centre, int radius, uint seed)
        {
            int outer = radius * 6 / 5 + 1, cell = math.max(6, radius / 2);
            for (int y = math.max(1, centre.y - outer); y <= math.min(size - 2, centre.y + outer); y++)
            for (int x = math.max(1, centre.x - outer); x <= math.min(size - 2, centre.x + outer); x++)
            {
                long dx = x - centre.x, dy = y - centre.y, d2 = dx * dx + dy * dy;
                long edge = radius * (700L + ValueNoise(x, y, cell, seed) / 2) / 1000; // 0.7r .. 1.2r
                if (d2 <= edge * edge || d2 * 4 <= (long)radius * radius) tiles[y * size + x] = Floor;
            }
        }

        /// <summary>
        /// Rock outcrops inside a central area or expansion until about <paramref name="share"/> of its disc is covered: small
        /// lobed islands, kept apart (a large unit passes between any two) and away from the centre where the lanes meet.
        /// </summary>
        private static void AddOutcrops(NativeArray<byte> tiles, int size, MapRegion region, float share, ref Random rng)
        {
            int r = region.Radius;
            long budget = (long)r * r * 314 / 100 * (int)(share * 1000f + 0.5f) / 1000;
            var placed = new List<(int2 c, int o)>();
            for (int tries = 0; budget > 0 && tries < 64; tries++)
            {
                int o = rng.NextInt(math.max(3, r / 12), math.max(4, r / 6) + 1);
                int reach = rng.NextInt(math.max(8 + o, r / 5), math.max(9 + o, r * 75 / 100 - o) + 1);
                int2 c = region.Centre + Directions[rng.NextInt(0, Directions.Length)] * reach / 1000;
                bool clear = true;
                foreach ((int2 pc, int po) in placed)
                {
                    long dx = c.x - pc.x, dy = c.y - pc.y, gap = o + po + 8;
                    if (dx * dx + dy * dy < gap * gap) { clear = false; break; }
                }
                if (!clear) continue;
                placed.Add((c, o));
                FillDisc(tiles, size, c, o, Rock);
                int bumps = rng.NextInt(1, 4);
                for (int b = 0; b < bumps; b++) FillDisc(tiles, size, c + Directions[rng.NextInt(0, Directions.Length)] * o / 1000, math.max(1, o * 2 / 3), Rock);
                budget -= (long)o * o * 314 / 100;
            }
        }

        /// <summary>Turns every floor tile not reachable from <paramref name="from"/> (4-connected) into rock, so no pocket is cut off.</summary>
        private static void FillUnreached(NativeArray<byte> tiles, int size, int2 from)
        {
            var seen = new NativeArray<bool>(tiles.Length, Allocator.Temp);
            var queue = new NativeArray<int>(tiles.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            int head = 0, tail = 0, start = from.y * size + from.x;
            if (tiles[start] == Floor) { seen[start] = true; queue[tail++] = start; }
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % size;
                if (x > 0 && !seen[i - 1] && tiles[i - 1] == Floor) { seen[i - 1] = true; queue[tail++] = i - 1; }
                if (x < size - 1 && !seen[i + 1] && tiles[i + 1] == Floor) { seen[i + 1] = true; queue[tail++] = i + 1; }
                if (i >= size && !seen[i - size] && tiles[i - size] == Floor) { seen[i - size] = true; queue[tail++] = i - size; }
                if (i < tiles.Length - size && !seen[i + size] && tiles[i + size] == Floor) { seen[i + size] = true; queue[tail++] = i + size; }
            }
            for (int i = 0; i < tiles.Length; i++) if (tiles[i] == Floor && !seen[i]) tiles[i] = Rock;
            seen.Dispose();
            queue.Dispose();
        }

        private static void FillDisc(NativeArray<byte> tiles, int size, int2 centre, int radius, byte tile)
        {
            for (int y = math.max(1, centre.y - radius); y <= math.min(size - 2, centre.y + radius); y++)
            for (int x = math.max(1, centre.x - radius); x <= math.min(size - 2, centre.x + radius); x++)
            {
                int dx = x - centre.x, dy = y - centre.y;
                if (dx * dx + dy * dy <= radius * radius) tiles[y * size + x] = tile;
            }
        }

        private static void CarveBlock(NativeArray<byte> tiles, int size, int2 centre, int half)
        {
            for (int y = math.max(1, centre.y - half); y <= math.min(size - 2, centre.y + half); y++)
            for (int x = math.max(1, centre.x - half); x <= math.min(size - 2, centre.x + half); x++)
                tiles[y * size + x] = Floor;
        }

        /// <summary>Walks the lane's polyline in integer steps, carving a disc (lane) or a square block (chokepoint) at each.</summary>
        private static void CarveLane(NativeArray<byte> tiles, int size, Plan plan, LanePlan lane, NativeArray<byte> protect, int margin)
        {
            var points = new List<int2> { plan.Regions[lane.A].Centre, lane.M1 };
            if (lane.HasM2) points.Add(lane.M2);
            points.Add(plan.Regions[lane.B].Centre);

            int walked = 0;
            int2 previous = points[0];
            for (int s = 0; s + 1 < points.Count; s++)
            {
                int2 a = points[s], b = points[s + 1];
                int steps = math.max(1, Chebyshev(a, b));
                for (int i = 0; i <= steps; i++, walked++)
                {
                    int2 p = new int2((int)(a.x + (long)(b.x - a.x) * i / steps), (int)(a.y + (long)(b.y - a.y) * i / steps));
                    bool choke = lane.ChokeWidth > 0 && walked >= lane.ChokeFrom && walked < lane.ChokeFrom + lane.ChokeLen;
                    if (choke)
                    {
                        // A diagonal step also carves the corner, so the chokepoint's centre line is 4-connected.
                        if (p.x != previous.x && p.y != previous.y) CarveBlock(tiles, size, new int2(p.x, previous.y), lane.ChokeWidth / 2);
                        CarveBlock(tiles, size, p, lane.ChokeWidth / 2);
                        MarkBlock(protect, size, p, lane.ChokeWidth / 2 + margin);
                    }
                    else CarveDisc(tiles, size, p, lane.Width / 2);
                    previous = p;
                }
            }
        }

        private static void MarkBlock(NativeArray<byte> mask, int size, int2 centre, int half)
        {
            for (int y = math.max(0, centre.y - half); y <= math.min(size - 1, centre.y + half); y++)
            for (int x = math.max(0, centre.x - half); x <= math.min(size - 1, centre.x + half); x++)
                mask[y * size + x] = 1;
        }

        /// <summary>
        /// Cave-like edges: rock within <paramref name="depth"/> tiles of floor turns to floor where seeded value noise
        /// beats a threshold that rises with the distance, so bays, fingers and alcoves grow out of every region and lane;
        /// two cellular-automaton passes then smooth them into cave shapes. Only ever widens (planned floor never turns
        /// to rock) and never touches protected tiles (the chokepoints). Integer arithmetic only.
        /// </summary>
        private static void GrowCaves(NativeArray<byte> tiles, int size, NativeArray<byte> protect, int depth, uint seed)
        {
            if (depth <= 0) return;
            // Distance from the planned floor, over rock, up to the band's depth (0 = planned floor, 255 = outside the band).
            var dist = new NativeArray<byte>(tiles.Length, Allocator.Temp);
            var queue = new NativeArray<int>(tiles.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            int head = 0, tail = 0;
            for (int i = 0; i < tiles.Length; i++)
            {
                if (tiles[i] == Floor) queue[tail++] = i;
                else dist[i] = 255;
            }
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % size, y = i / size, d = dist[i] + 1;
                if (d > depth) continue;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + Steps[k].x, ny = y + Steps[k].y;
                    if (nx < 2 || ny < 2 || nx > size - 3 || ny > size - 3) continue;
                    int n = ny * size + nx;
                    if (dist[n] != 255 || protect[n] != 0) continue;
                    dist[n] = (byte)d;
                    queue[tail++] = n;
                }
            }
            queue.Dispose();

            int big = math.max(4, depth * 2), small = math.max(2, depth / 2 + 1);
            for (int y = 2; y < size - 2; y++)
            for (int x = 2; x < size - 2; x++)
            {
                int i = y * size + x;
                int d = dist[i];
                if (d == 0 || d == 255) continue;
                int noise = (2 * ValueNoise(x, y, big, seed) + ValueNoise(x, y, small, seed ^ 0xA511E9B3u)) / 3;
                if (noise >= 300 + 600 * d / depth) tiles[i] = Floor;
            }

            var next = new NativeArray<byte>(tiles.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int pass = 0; pass < 2; pass++)
            {
                NativeArray<byte>.Copy(tiles, next, tiles.Length);
                for (int y = 2; y < size - 2; y++)
                for (int x = 2; x < size - 2; x++)
                {
                    int i = y * size + x;
                    int d = dist[i];
                    if (d == 0 || d == 255) continue;
                    int around = 0;
                    foreach (int2 a in Around) if (tiles[i + a.y * size + a.x] == Floor) around++;
                    if (around >= 5) next[i] = Floor;
                    else if (around <= 2) next[i] = Rock;
                }
                NativeArray<byte>.Copy(next, tiles, tiles.Length);
            }
            next.Dispose();
            dist.Dispose();
        }

        /// <summary>Seeded value noise in [0, 1000): hashed lattice values every <paramref name="cell"/> tiles, bilinearly blended in integers.</summary>
        private static int ValueNoise(int x, int y, int cell, uint seed)
        {
            int cx = x / cell, cy = y / cell, fx = x % cell, fy = y % cell;
            long h00 = Hash(cx, cy, seed) % 1000, h10 = Hash(cx + 1, cy, seed) % 1000, h01 = Hash(cx, cy + 1, seed) % 1000, h11 = Hash(cx + 1, cy + 1, seed) % 1000;
            long v = h00 * (cell - fx) * (cell - fy) + h10 * fx * (cell - fy) + h01 * (cell - fx) * fy + h11 * fx * fy;
            return (int)(v / ((long)cell * cell));
        }

        /// <summary>One pass that bulges the rock edge: rock beside floor turns to floor on a seeded hash. Never narrows a lane, never touches a protected tile.</summary>
        private static void Roughen(NativeArray<byte> tiles, int size, uint seed, NativeArray<byte> protect)
        {
            var next = new NativeArray<byte>(tiles.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<byte>.Copy(tiles, next, tiles.Length);
            for (int y = 1; y < size - 1; y++)
            for (int x = 1; x < size - 1; x++)
            {
                int i = y * size + x;
                if (tiles[i] != Rock || protect[i] != 0) continue;
                if (tiles[i - 1] != Floor && tiles[i + 1] != Floor && tiles[i - size] != Floor && tiles[i + size] != Floor) continue;
                if (Hash(x, y, seed) % 100 < RoughenPercent) next[i] = Floor;
            }
            NativeArray<byte>.Copy(next, tiles, tiles.Length);
            next.Dispose();
        }

        private static uint Hash(int x, int y, uint seed)
        {
            uint h = unchecked((uint)x * 73856093u ^ (uint)y * 19349663u ^ seed * 83492791u);
            h ^= h >> 13; h = unchecked(h * 0x5bd1e995u); h ^= h >> 15;
            return h;
        }

        private static bool FacesFloor(NativeArray<byte> tiles, int size, int x, int y)
        {
            if (x < 1 || y < 1 || x >= size - 1 || y >= size - 1) return false;
            int i = y * size + x;
            return tiles[i] == Rock && (tiles[i - 1] == Floor || tiles[i + 1] == Floor || tiles[i - size] == Floor || tiles[i + size] == Floor);
        }

        /// <summary>
        /// Grows a cluster of up to <paramref name="count"/> gem tiles along the rim (rock facing floor) near
        /// <paramref name="centre"/>. With <paramref name="awayFrom"/> set, starts at the rim tile farthest from it.
        /// </summary>
        private static void GrowGems(NativeArray<byte> tiles, int size, int2 centre, int reach, int count, int2 awayFrom, bool useAway, ref Random rng)
        {
            var candidates = new List<int2>();
            for (int y = centre.y - reach; y <= centre.y + reach; y++)
            for (int x = centre.x - reach; x <= centre.x + reach; x++)
            {
                int dx = x - centre.x, dy = y - centre.y;
                if (dx * dx + dy * dy <= reach * reach && FacesFloor(tiles, size, x, y)) candidates.Add(new int2(x, y));
            }
            if (candidates.Count == 0) return;

            int2 start;
            if (useAway)
            {
                start = candidates[0];
                long bestScore = -1;
                foreach (int2 c in candidates)
                {
                    long dx = c.x - awayFrom.x, dy = c.y - awayFrom.y, score = dx * dx + dy * dy;
                    if (score > bestScore) { bestScore = score; start = c; }
                }
            }
            else start = candidates[rng.NextInt(0, candidates.Count)];

            var inReach = new HashSet<int2>(candidates);
            var seen = new HashSet<int2>();
            var queue = new Queue<int2>();
            int placed = 0;
            int next = 0;
            while (placed < count)
            {
                if (queue.Count == 0)
                {
                    // The rim is cut by lanes: carry on from the first rim tile not yet reached.
                    if (seen.Add(start)) queue.Enqueue(start);
                    else
                    {
                        while (next < candidates.Count && seen.Contains(candidates[next])) next++;
                        if (next >= candidates.Count) break;
                        seen.Add(candidates[next]);
                        queue.Enqueue(candidates[next]);
                    }
                }
                int2 t = queue.Dequeue();
                tiles[t.y * size + t.x] = Gem;
                placed++;
                foreach (int2 d in Around)
                {
                    int2 n = t + d;
                    if (inReach.Contains(n) && seen.Add(n)) queue.Enqueue(n);
                }
            }
        }

        /// <summary>A 6–10 tile vein on the site's rim, away from the lanes that leave it.</summary>
        private static void AddVein(NativeArray<byte> tiles, int size, Plan plan, int regionIndex, ref Random rng)
        {
            MapRegion region = plan.Regions[regionIndex];
            // The point to stay away from: the mean of the neighbouring regions' centres, so the vein sits opposite the lanes.
            long sx = 0, sy = 0; int neighbours = 0;
            foreach (LanePlan l in plan.Lanes)
            {
                int other = l.A == regionIndex ? l.B : l.B == regionIndex ? l.A : -1;
                if (other < 0) continue;
                sx += plan.Regions[other].Centre.x; sy += plan.Regions[other].Centre.y; neighbours++;
            }
            int2 away = neighbours == 0 ? region.Centre : new int2((int)(sx / neighbours), (int)(sy / neighbours));
            GrowGems(tiles, size, region.Centre, region.Radius + 3, 6 + rng.NextInt(0, 5), away, true, ref rng);
        }

        /// <summary>Each rock tile next to floor becomes a gem with probability <paramref name="gemChance"/>, in row-major order.</summary>
        private static void SprinkleGems(NativeArray<byte> tiles, int size, ref Random rng, float gemChance)
        {
            int threshold = (int)(math.clamp(gemChance, 0f, 1f) * 1000000f);
            for (int y = 1; y < size - 1; y++)
            for (int x = 1; x < size - 1; x++)
                if (FacesFloor(tiles, size, x, y) && rng.NextInt(0, 1000000) < threshold) tiles[y * size + x] = Gem;
        }

        private static void WriteBorder(NativeArray<byte> tiles, int size)
        {
            for (int i = 0; i < size; i++)
            {
                tiles[i] = BorderTile;
                tiles[(size - 1) * size + i] = BorderTile;
                tiles[i * size] = BorderTile;
                tiles[i * size + size - 1] = BorderTile;
            }
        }
    }
}

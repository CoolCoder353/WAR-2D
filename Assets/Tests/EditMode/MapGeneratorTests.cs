using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.World;

/// <summary>Correctness and fairness tests for the seeded region-graph map generator.</summary>
public class MapGeneratorTests
{
    private const int Size = 256;
    private const uint Seed = 7;

    private static readonly int2[] Steps = { new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1) };

    private static Config.MapConfig Cfg => Config.ConfigLoader.LoadConfig().Match.Map;

    private static MapLayout Make(int size, uint seed, float gems = 0.01f) => RegionMapGenerator.Generate(size, seed, gems, Allocator.TempJob);

    private static byte At(MapLayout l, int size, int2 t) => ((uint)t.x < (uint)size && (uint)t.y < (uint)size) ? l.Tiles[t.y * size + t.x] : (byte)TileType.Border;

    /// <summary>Tiles reachable from <paramref name="from"/> over floor, optionally only floor whose 3x3 neighbourhood is all floor.</summary>
    private static bool[] Flood(MapLayout l, int size, int2 from, bool eroded)
    {
        var seen = new bool[size * size];
        var queue = new Queue<int>();
        seen[from.y * size + from.x] = true;
        queue.Enqueue(from.y * size + from.x);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % size, y = i / size;
            foreach (int2 step in Steps)
            {
                int2 n = new int2(x + step.x, y + step.y);
                if ((uint)n.x >= (uint)size || (uint)n.y >= (uint)size || seen[n.y * size + n.x] || l.Tiles[n.y * size + n.x] != (byte)TileType.Ground) continue;
                if (eroded && !Roomy(l, size, n)) continue;
                seen[n.y * size + n.x] = true;
                queue.Enqueue(n.y * size + n.x);
            }
        }
        return seen;
    }

    private static bool Roomy(MapLayout l, int size, int2 t)
    {
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (At(l, size, t + new int2(dx, dy)) != (byte)TileType.Ground) return false;
        return true;
    }

    [Test]
    public void SameSeedSameMap()
    {
        using MapLayout la = Make(Size, Seed), lb = Make(Size, Seed), lc = Make(Size, Seed + 1);
        NativeArray<byte> a = la.Tiles, b = lb.Tiles, c = lc.Tiles;
        int mismatch = -1, differences = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && mismatch < 0) mismatch = i;
            if (a[i] != c[i]) differences++;
        }
        Assert.AreEqual(-1, mismatch, "the same seed produced two different maps");
        Assert.Greater(differences, 0, "seed 8 produced the same map as seed 7");
    }

    [Test]
    public void BorderIsBlocked([Values(128, 256, 512)] int size)
    {
        using MapLayout l = (Make(size, Seed));
        for (int i = 0; i < size; i++)
            foreach (int2 t in new[] { new int2(i, 0), new int2(i, size - 1), new int2(0, i), new int2(size - 1, i) })
                Assert.AreEqual((byte)TileType.Border, At(l, size, t), $"border tile {t}");
    }

    [Test]
    public void StartSitesAreTheConfiguredCountAndClear([Values(128, 256, 512, 1024)] int size)
    {
        using MapLayout l = (Make(size, Seed));
        Assert.AreEqual(RegionMapGenerator.Counts(Cfg, size).starts, l.StartSites.Length);
        for (int s = 0; s < l.StartSites.Length; s++)
        {
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                Assert.AreEqual((byte)TileType.Ground, At(l, size, l.StartSites[s] + new int2(dx, dy)), $"size {size}: the 5x5 HQ footprint at site {s} {l.StartSites[s]}");
            for (int j = s + 1; j < l.StartSites.Length; j++) Assert.AreNotEqual(l.StartSites[s], l.StartSites[j]);
        }
    }

    [Test]
    public void AllFloorIsConnected([Values(128, 256, 512)] int size)
    {
        using MapLayout l = (Make(size, Seed));
        bool[] seen = Flood(l, size, l.StartSites[0], false);
        for (int i = 0; i < l.Tiles.Length; i++)
            if (l.Tiles[i] == (byte)TileType.Ground) Assert.IsTrue(seen[i], $"floor tile {i % size},{i / size} is cut off");
        foreach (int2 e in l.Expansions) Assert.IsTrue(seen[e.y * size + e.x], "an expansion is cut off");
    }

    [Test]
    public void LargeUnitsCanCrossBetweenEveryStartSite([Values(256, 512)] int size)
    {
        using MapLayout l = (Make(size, Seed));
        bool[] roomy = Flood(l, size, l.StartSites[0], true);
        foreach (int2 s in l.StartSites) Assert.IsTrue(roomy[s.y * size + s.x], $"site {s} is not reachable with 1 tile of clearance on each side (a chokepoint is too narrow)");
    }

    [Test]
    public void FloorIsAMinorityButSubstantial()
    {
        using MapLayout l = (Make(512, Seed));
        int floor = 0;
        for (int i = 0; i < l.Tiles.Length; i++) if (l.Tiles[i] == (byte)TileType.Ground) floor++;
        float fraction = (float)floor / l.Tiles.Length;
        Assert.That(fraction, Is.InRange(0.10f, 0.60f), $"floor fraction {fraction:P1}");
    }

    [Test]
    public void GemsOnlyReplaceRockThatFacesFloor()
    {
        using MapLayout l = (Make(256, 7u, 0.5f));
        int gems = 0;
        for (int y = 1; y < 255; y++)
        for (int x = 1; x < 255; x++)
        {
            if (l.Tiles[y * 256 + x] != (byte)TileType.Gem) continue;
            gems++;
            bool facesFloor = false;
            foreach (int2 s in Steps) facesFloor |= At(l, 256, new int2(x, y) + s) == (byte)TileType.Ground;
            Assert.IsTrue(facesFloor, $"gem at {x},{y} is buried in rock");
        }
        Assert.Greater(gems, 0);
    }

    [Test]
    public void EveryStartSiteHasAVeinAndEveryExpansionARichField([Values(256, 512)] int size)
    {
        using MapLayout l = (Make(size, 3u, 0f));
        int Count(int2 c, int reach)
        {
            int n = 0;
            for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
                if (math.lengthsq(new int2(dx, dy)) <= reach * reach && At(l, size, c + new int2(dx, dy)) == (byte)TileType.Gem) n++;
            return n;
        }
        foreach (int2 s in l.StartSites) Assert.That(Count(s, RegionMapGenerator.StartRadius + 4), Is.GreaterThanOrEqualTo(6), $"vein at {s}");
        foreach (int2 e in l.Expansions) Assert.That(Count(e, 28), Is.GreaterThanOrEqualTo(Cfg.ExpansionGemTiles * 3 / 4), $"rich field at {e}");
    }

    [Test]
    public void LanesAndChokepointsStayInTheConfiguredBounds()
    {
        Config.MapConfig cfg = Cfg;
        for (uint seed = 1; seed <= 20; seed++)
        {
            using MapLayout l = (Make(512, seed));
            int chokes = 0;
            foreach (MapLane lane in l.Lanes)
            {
                Assert.That(lane.Width, Is.InRange(cfg.LaneWidthMin, cfg.LaneWidthMax));
                if (lane.ChokeWidth == 0) continue;
                chokes++;
                Assert.That(lane.ChokeWidth, Is.InRange(cfg.ChokeWidthMin, cfg.ChokeWidthMax));
            }
            Assert.Greater(chokes, 0, $"seed {seed} has no chokepoint");
        }
    }

    [Test]
    public void EveryStartSiteHasTwoWaysOut([Values(128, 256, 512, 1024)] int size)
    {
        for (uint seed = 1; seed <= 5; seed++)
        {
            using MapLayout l = (Make(size, seed));
            var degree = new int[l.Regions.Length];
            foreach (MapLane lane in l.Lanes) { degree[lane.A]++; degree[lane.B]++; }
            for (int i = 0; i < l.Regions.Length; i++)
                if (l.Regions[i].Kind == RegionKind.Start) Assert.GreaterOrEqual(degree[i], 2, $"size {size} seed {seed}: start region {i}");
        }
    }

    [Test]
    public void FairLayoutsOnManySeeds([Values(128, 256, 512, 1024)] int size)
    {
        int seeds = size <= 256 ? 200 : size == 512 ? 60 : 12;
        int unfair = 0;
        var unfairSeeds = new List<uint>();
        for (uint seed = 1; seed <= seeds; seed++)
        {
            using MapLayout l = (Make(size, seed));
            if (!l.Fair) { unfair++; unfairSeeds.Add(seed); }
        }
        if (unfair > 0)
        {
            RegionMapGenerator.Diagnostics = new List<string>();
            RegionMapGenerator.Generate(size, unfairSeeds[0], 0f, Allocator.Temp).Dispose();
            var lines = RegionMapGenerator.Diagnostics.GetRange(RegionMapGenerator.Diagnostics.Count - RegionMapGenerator.Counts(Cfg, size).starts, RegionMapGenerator.Counts(Cfg, size).starts);
            RegionMapGenerator.Diagnostics = null;
            Assert.Fail($"size {size}: seeds {string.Join(",", unfairSeeds)} unfair; seed {unfairSeeds[0]} last attempt:\n{string.Join("\n", lines)}");
        }
        Assert.AreEqual(0, unfair, $"size {size}: {unfair} of {seeds} seeds fell back to a best-effort layout");
    }

    [Test]
    public void LargestMapGeneratesWithinTheBudget()
    {
        Make(1024, 1u).Tiles.Dispose(); // warm-up
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Make(1024, 2u).Tiles.Dispose();
        Assert.Less(timer.ElapsedMilliseconds, 1000, "a 1024 map must generate in under a second");
    }

    [Test]
    public void EveryLobbySizeFitsTheDealtOptionsOfEightPlayers()
    {
        foreach (int size in Config.ConfigLoader.LoadConfig().Lobby.MapSizes)
            Assert.GreaterOrEqual(RegionMapGenerator.Counts(Cfg, size).starts, 8 * 1 + Config.ConfigLoader.LoadConfig().Lobby.SpareStartSites, $"size {size}");
    }
}

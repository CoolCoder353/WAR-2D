using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Spike;

/// <summary>Correctness tests for the seeded cave generator.</summary>
public class MapGeneratorTests
{
    private const int Size = 256;
    private const uint Seed = 7;

    private static readonly int2[] Steps =
    {
        new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1),
    };

    [Test]
    public void SameSeedSameMap()
    {
        using SpikeMap a = MapGenerator.Generate(Size, Seed, Allocator.TempJob);
        using SpikeMap b = MapGenerator.Generate(Size, Seed, Allocator.TempJob);
        using SpikeMap c = MapGenerator.Generate(Size, Seed + 1, Allocator.TempJob);

        int mismatch = -1, differences = 0;
        for (int i = 0; i < a.Tiles.Length; i++)
        {
            if (a.Tiles[i] != b.Tiles[i] && mismatch < 0) mismatch = i;
            if (a.Tiles[i] != c.Tiles[i]) differences++;
        }
        Assert.AreEqual(-1, mismatch, "the same seed produced two different maps");
        Assert.Greater(differences, 0, "seed 8 produced the same map as seed 7");
    }

    [Test]
    public void BorderIsBlocked()
    {
        using SpikeMap map = MapGenerator.Generate(Size, Seed, Allocator.TempJob);
        int2 bad = new int2(-1, -1);
        for (int x = 0; x < map.Width; x++)
        {
            if (map.TileAt(new int2(x, 0)) != SpikeMap.Border || map.TileAt(new int2(x, map.Height - 1)) != SpikeMap.Border)
                bad = new int2(x, 0);
        }
        for (int y = 0; y < map.Height; y++)
        {
            if (map.TileAt(new int2(0, y)) != SpikeMap.Border || map.TileAt(new int2(map.Width - 1, y)) != SpikeMap.Border)
                bad = new int2(0, y);
        }
        Assert.AreEqual(new int2(-1, -1), bad, $"the outer ring is not all Border (first bad column/row {bad})");
    }

    [Test]
    public void HqSitesAreFloorAndClear()
    {
        int2[] sites = MapGenerator.HqSites(Size);
        Assert.AreEqual(8, sites.Length);

        using SpikeMap map = MapGenerator.Generate(Size, Seed, Allocator.TempJob);
        string failure = null;
        for (int s = 0; s < sites.Length && failure == null; s++)
        for (int dy = -6; dy <= 6 && failure == null; dy++)
        for (int dx = -6; dx <= 6 && failure == null; dx++)
        {
            int2 tile = sites[s] + new int2(dx, dy);
            if (map.TileAt(tile) != SpikeMap.Floor)
                failure = $"HQ {s} at {sites[s]} is not clear at {tile}";
        }
        Assert.IsNull(failure);

        for (int i = 0; i < sites.Length; i++)
        for (int j = i + 1; j < sites.Length; j++)
            Assert.AreNotEqual(sites[i], sites[j], $"HQ sites {i} and {j} coincide");
    }

    [Test]
    public void AllFloorIsConnected()
    {
        using SpikeMap map = MapGenerator.Generate(Size, Seed, Allocator.TempJob);
        int2 start = MapGenerator.HqSites(Size)[0];

        var visited = new bool[map.Tiles.Length];
        var queue = new Queue<int>();
        int startIndex = start.y * map.Width + start.x;
        visited[startIndex] = true;
        queue.Enqueue(startIndex);

        int reached = 0, floor = 0;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            reached++;
            int x = i % map.Width, y = i / map.Width;
            foreach (int2 step in Steps)
            {
                int nx = x + step.x, ny = y + step.y;
                if ((uint)nx >= (uint)map.Width || (uint)ny >= (uint)map.Height) continue;
                int n = ny * map.Width + nx;
                if (visited[n] || map.Tiles[n] != SpikeMap.Floor) continue;
                visited[n] = true;
                queue.Enqueue(n);
            }
        }

        for (int i = 0; i < map.Tiles.Length; i++)
            if (map.Tiles[i] == SpikeMap.Floor) floor++;
        Assert.AreEqual(floor, reached, "some floor tiles are not reachable from HQ 0");
    }

    [Test]
    public void RockFractionIsNearTarget()
    {
        using SpikeMap map = MapGenerator.Generate(512, Seed, Allocator.TempJob);
        int blocked = 0, interior = 0;
        for (int y = 1; y < map.Height - 1; y++)
        for (int x = 1; x < map.Width - 1; x++)
        {
            interior++;
            if (map.Tiles[y * map.Width + x] != SpikeMap.Floor) blocked++;
        }
        float fraction = (float)blocked / interior;
        Assert.GreaterOrEqual(fraction, 0.20f, $"blocked fraction {fraction:P1} is below 20 %");
        Assert.LessOrEqual(fraction, 0.40f, $"blocked fraction {fraction:P1} is above 40 %");
    }
}

/// <summary>Correctness tests for scenario placement and the scripted orders and terrain changes.</summary>
public class SpikeScenarioTests
{
    private const int Size = 128;
    private const uint Seed = 11;

    private sealed class Fixture : IDisposable
    {
        public SpikeMap Map;
        public SpikeScenario Scenario;

        public void Dispose()
        {
            Scenario.Dispose();
            Map.Dispose();
        }
    }

    private static Fixture Create(SpikePlacement placement = SpikePlacement.Hqs, int units = 200,
        int buildings = 5, int largePercent = 10, bool clump = false)
    {
        SpikeMap map = MapGenerator.Generate(Size, Seed, Allocator.TempJob);
        SpikeScenarioConfig config = SpikeScenarioConfig.Defaults;
        config.Players = 8;
        config.UnitsPerPlayer = units;
        config.BuildingsPerPlayer = buildings;
        config.Seed = Seed;
        config.LargePercent = largePercent;
        config.Placement = placement;
        config.Clump = clump;
        return new Fixture { Map = map, Scenario = SpikeScenario.Create(config, map, Allocator.TempJob) };
    }

    [Test]
    public void PlacesEveryUnitOnFloorWithItsPlayer()
    {
        using Fixture f = Create();
        SpikeScenario s = f.Scenario;
        Assert.AreEqual(8 * 200, s.UnitCount);
        Assert.AreEqual(8 * 5, s.BuildingCount);

        string failure = null;
        for (int player = 0; player < 8 && failure == null; player++)
        for (int i = 0; i < 200 && failure == null; i++)
        {
            int unit = player * 200 + i;
            if (s.UnitOwners[unit] != player) failure = $"unit {unit} belongs to player {s.UnitOwners[unit]}, not {player}";
            else if (s.UnitHealth[unit] != 100f) failure = $"unit {unit} has health {s.UnitHealth[unit]}";
            else if (f.Map.TileAt((int2)math.floor(s.UnitPositions[unit])) != SpikeMap.Floor)
                failure = $"unit {unit} is not on a floor tile";
        }
        Assert.IsNull(failure);

        for (int building = 0; building < s.BuildingCount; building++)
        {
            Assert.AreEqual(building / 5, s.BuildingOwners[building]);
            Assert.AreEqual(300f, s.BuildingHealth[building]);
            int2 tile = (int2)math.floor(s.BuildingPositions[building]);
            Assert.AreEqual(SpikeMap.Floor, f.Map.TileAt(tile), $"building {building} is not on a floor tile");
            int2 hq = s.HqSites[s.BuildingOwners[building]];
            Assert.LessOrEqual(math.cmax(math.abs(tile - hq)), 8, $"building {building} is not near its HQ");
        }
    }

    [Test]
    public void LargeShareMatchesAndLargeUnitsHaveClearance()
    {
        using Fixture f = Create();
        SpikeScenario s = f.Scenario;
        var large = new int[8];
        string failure = null;
        for (int unit = 0; unit < s.UnitCount; unit++)
        {
            int2 tile = (int2)math.floor(s.UnitPositions[unit]);
            if (s.UnitSizeClass[unit] == 1)
            {
                large[unit / 200]++;
                if (s.UnitRadius[unit] != SpikeScenario.LargeRadius) failure = $"large unit {unit} has radius {s.UnitRadius[unit]}";
                for (int dy = -1; dy <= 1 && failure == null; dy++)
                for (int dx = -1; dx <= 1 && failure == null; dx++)
                    if (f.Map.TileAt(tile + new int2(dx, dy)) != SpikeMap.Floor)
                        failure = $"large unit {unit} lacks clearance at {tile}";
            }
            else if (s.UnitRadius[unit] != SpikeScenario.SmallRadius)
                failure = $"small unit {unit} has radius {s.UnitRadius[unit]}";
        }
        Assert.IsNull(failure);
        for (int player = 0; player < 8; player++)
            Assert.AreEqual(20, large[player], $"player {player}'s 200-unit army should be 10 % large");
    }

    [Test]
    public void ReferenceArmiesFitTheDefaultMap()
    {
        using SpikeMap map = MapGenerator.Generate(512, 1, Allocator.TempJob);
        using SpikeScenario s = SpikeScenario.Create(SpikeScenarioConfig.Defaults, map, Allocator.TempJob);

        Assert.AreEqual(8 * 10000, s.UnitCount);
        var large = new int[8];
        for (int unit = 0; unit < s.UnitCount; unit++)
            if (s.UnitSizeClass[unit] == 1) large[unit / 10000]++;
        for (int player = 0; player < 8; player++)
            Assert.AreEqual(1000, large[player], $"player {player}'s 10,000-unit army should hold its 10 % large share");
    }

    [Test]
    public void OrdersArePureAndMarchAtOpposingHq()
    {
        using Fixture f = Create();
        SpikeScenario s = f.Scenario;
        int[] pairs = { 1, 0, 3, 2, 5, 4, 7, 6 };

        var first = new List<(int player, int2 goal, float fraction)>();
        foreach (var order in s.OrdersAt(0)) first.Add(order);
        Assert.AreEqual(8, first.Count, "every player should order at tick 0");
        for (int i = 0; i < first.Count; i++)
        {
            Assert.AreEqual(i, first[i].player);
            Assert.AreEqual(s.HqSites[pairs[i]], first[i].goal, $"player {i} does not march at its opposing HQ");
            Assert.AreEqual(SpikeScenario.OrderFraction, first[i].fraction);
        }

        var again = new List<(int player, int2 goal, float fraction)>();
        foreach (var order in s.OrdersAt(0)) again.Add(order);
        CollectionAssert.AreEqual(first, again, "OrdersAt is not a pure function of the tick");

        int offTick = 0, afterScript = 0;
        foreach (var _ in s.OrdersAt(1)) offTick++;
        foreach (var _ in s.OrdersAt(SpikeScenarioConfig.Defaults.ScriptTicks)) afterScript++;
        Assert.AreEqual(0, offTick, "orders should only fire on the interval");
        Assert.AreEqual(0, afterScript, "orders should stop at the end of the script");
    }

    [Test]
    public void ClumpOrdersGoToTheMapCentre()
    {
        using Fixture f = Create(clump: true);
        foreach (var order in f.Scenario.OrdersAt(0))
            Assert.AreEqual(new int2(Size / 2, Size / 2), order.goal, $"player {order.player} should clump on the centre");
    }

    [Test]
    public void FrontsPlaceArmiesAtTheirFront()
    {
        using Fixture f = Create(placement: SpikePlacement.Fronts, units: 100, largePercent: 0);
        SpikeScenario s = f.Scenario;
        int2 hq0 = s.HqSites[0], hq1 = s.HqSites[1];
        int2 front = (hq0 + hq1) / 2;
        for (int player = 0; player < 2; player++)
        {
            float2 centre = float2.zero;
            for (int i = 0; i < 100; i++) centre += s.UnitPositions[player * 100 + i];
            centre /= 100;
            Assert.Less(math.distance(centre, front), math.distance(centre, s.HqSites[player]) * 0.5f,
                $"player {player}'s army should start at the front, not at its HQ");
        }
    }

    [Test]
    public void TerrainScriptIsPureAndFlipsRockAdjacentTiles()
    {
        using Fixture f = Create();
        SpikeScenario s = f.Scenario;
        using var first = new NativeList<int2>(8, Allocator.TempJob);
        using var again = new NativeList<int2>(8, Allocator.TempJob);
        s.TerrainChangesAt(0, first);
        s.TerrainChangesAt(0, again);
        Assert.AreEqual(1, first.Length, "one tile flips every fifth tick");
        CollectionAssert.AreEqual(first.AsArray().ToArray(), again.AsArray().ToArray(), "the terrain script is not pure");

        int perSecond = 0;
        for (int tick = 0; tick < SpikeScenario.TicksPerSecond; tick++)
        {
            first.Clear();
            s.TerrainChangesAt(tick, first);
            perSecond += first.Length;
            foreach (int2 tile in first)
            {
                byte value = f.Map.TileAt(tile);
                Assert.IsTrue(value == SpikeMap.Floor || value == SpikeMap.Rock, $"tile {tile} is Border or Gem");
                bool rock = false, floor = false;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    byte neighbour = f.Map.TileAt(tile + new int2(dx, dy));
                    rock |= neighbour == SpikeMap.Rock;
                    floor |= neighbour == SpikeMap.Floor;
                }
                Assert.IsTrue(value == SpikeMap.Floor ? rock : floor, $"changed tile {tile} is not rock-adjacent");
                foreach (int2 hq in s.HqSites)
                    Assert.Greater(math.cmax(math.abs(tile - hq)), SpikeScenario.TerrainClearRadius, $"tile {tile} is inside an HQ clearing");
            }
        }
        Assert.AreEqual(SpikeScenario.TerrainChangesPerSecond, perSecond, "the script should change 4 tiles per second");
    }
}

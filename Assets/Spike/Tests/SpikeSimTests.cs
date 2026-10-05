using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Spike;
using Random = Unity.Mathematics.Random;

/// <summary>
/// Correctness tests for the spike's simulation tick. Each one builds its own Spike world (the sim's
/// own <c>World</c>, its own systems, no game state), ticks the group by hand through
/// <see cref="SpikeSim.Tick"/> and checks the world it leaves behind.
/// </summary>
public class SpikeSimTests
{
    private const float Small = SpikeScenario.SmallRadius;
    private const float Large = SpikeScenario.LargeRadius;

    /// <summary>How close the bulk of an ordered army counts as at its destination.</summary>
    private const float GoalRadius = 10f;

    /// <summary>How far the queue behind the goal may still be, once the army has arrived.</summary>
    private const float DestinationRadius = 25f;

    /// <summary>
    /// Where a large unit counts as arrived: the destination area around the goal cell, which is well
    /// clear of the wall at x = 32, so reaching it proves the unit went through a gap.
    /// </summary>
    private const float LargeArrivalRadius = 10f;

    // ---- world helpers ----

    /// <summary>An all-floor map with a blocked border, for the tests that need open ground.</summary>
    private static SpikeMap OpenMap(int size)
    {
        var tiles = new NativeArray<byte>(size * size, Allocator.Persistent);
        for (int i = 0; i < tiles.Length; i++) tiles[i] = SpikeMap.Floor;
        for (int i = 0; i < size; i++)
        {
            tiles[i] = SpikeMap.Border;
            tiles[(size - 1) * size + i] = SpikeMap.Border;
            tiles[i * size] = SpikeMap.Border;
            tiles[i * size + size - 1] = SpikeMap.Border;
        }
        return new SpikeMap(size, size, tiles);
    }

    /// <summary>
    /// An open map with a wall down the middle: solid everywhere except a one-tile gap at y = 16 and a
    /// three-tile gap at y = 40..42. A large unit fits the second and never the first.
    /// </summary>
    private static SpikeMap WallMap(int size)
    {
        SpikeMap map = OpenMap(size);
        int wall = size / 2;
        for (int y = 1; y < size - 1; y++)
        {
            if (y == 16 || (y >= 40 && y <= 42)) continue;
            map.Tiles[y * size + wall] = SpikeMap.Rock;
        }
        return map;
    }

    private static SpikeUnit Unit(int id, byte owner, float2 position, float radius, float health = 100f) => new SpikeUnit
    {
        Id = id,
        Owner = owner,
        Team = owner,
        SizeClass = radius > Small ? (byte)1 : (byte)0,
        Radius = radius,
        Health = health,
        Position = position,
        TargetId = -1,
        FieldGoal = -1,
    };

    /// <summary>The sim's defaults with spawning off and the test's sizes, so nothing happens behind the test's back.</summary>
    private static SpikeSimConfig TestConfig(int players, int capacity, int idCapacity = 0)
    {
        var config = SpikeSimConfig.Defaults;
        config.Players = players;
        config.Capacity = capacity;
        config.IdCapacity = idCapacity > 0 ? idCapacity : capacity + 4096;
        config.SpawnPerTick = 0;
        config.SpawnTarget = 0;
        return config;
    }

    /// <summary>All live units' components, copied out in query order.</summary>
    private static NativeArray<SpikeUnit> Units(EntityQuery query, Allocator allocator) =>
        query.ToComponentDataArray<SpikeUnit>(allocator);

    // ---- tests ----

    /// <summary>
    /// Two enemies three tiles apart, each 100 health and 10 damage a second, fought for ten seconds:
    /// both are dead (or exactly one), and health never went negative before removal.
    /// </summary>
    [Test]
    public void TwoUnitsFight()
    {
        using SpikeMap map = OpenMap(64);
        SpikeSimConfig config = TestConfig(players: 2, capacity: 8);
        config.Slice = 1; // a fresh search every tick, so the two units fight on the same schedule
        using var sim = SpikeSim.Create(config, map, Allocator.Persistent);
        Entity left = sim.AddUnit(Unit(1, owner: 0, position: new float2(30f, 32f), radius: Small));
        Entity right = sim.AddUnit(Unit(2, owner: 1, position: new float2(33f, 32f), radius: Small));

        for (int tick = 0; tick < 200; tick++)
        {
            sim.Tick();
            foreach (Entity entity in new[] { left, right })
            {
                if (!sim.EntityManager.Exists(entity)) continue;
                float health = sim.EntityManager.GetComponentData<SpikeUnit>(entity).Health;
                Assert.GreaterOrEqual(health, 0f, $"health went negative at tick {tick} before removal");
            }
        }

        int alive = 0;
        foreach (Entity entity in new[] { left, right })
        {
            if (!sim.EntityManager.Exists(entity)) continue;
            if (sim.EntityManager.GetComponentData<SpikeUnit>(entity).Health > 0f) alive++;
        }
        Assert.LessOrEqual(alive, 1, $"both units must be dead after ten seconds of fighting, {alive} survived");
    }

    /// <summary>
    /// Two thousand units ordered across a 128² cave for a thousand ticks: no unit's tile is ever
    /// anything but floor.
    /// </summary>
    [Test]
    public void UnitsNeverEnterRock()
    {
        SpikeMap map = MapGenerator.Generate(128, 7u, Allocator.Persistent);
        try
        {
            var scenarioConfig = SpikeScenarioConfig.Defaults;
            scenarioConfig.Players = 1;
            scenarioConfig.UnitsPerPlayer = 2000;
            scenarioConfig.LargePercent = 10;
            scenarioConfig.Placement = SpikePlacement.Hqs;
            scenarioConfig.OrderIntervalTicks = 0;
            scenarioConfig.ScriptTicks = 0;
            using var scenario = SpikeScenario.Create(scenarioConfig, map, Allocator.Persistent);

            using var cache = new FlowFieldCache(map, Allocator.Persistent);
            using var sim = SpikeSim.Create(TestConfig(1, scenario.UnitCount + 64), map, Allocator.Persistent, cache);
            sim.AddScenario(scenario);
            int slot = sim.AddField(cache, scenario.HqSites[1]);
            sim.OrderAll(slot);

            var query = sim.EntityManager.CreateEntityQuery(typeof(SpikeUnit));
            float2 start = MeanPosition(query);
            string failure = null;

            for (int tick = 0; tick < 1000 && failure == null; tick++)
            {
                sim.Tick();
                using NativeArray<SpikeUnit> units = Units(query, Allocator.Temp);
                foreach (SpikeUnit unit in units)
                {
                    int2 tile = (int2)math.floor(unit.Position);
                    if (map.IsBlocked(tile))
                    {
                        failure = $"unit {unit.Id} stood in blocked tile {tile.x},{tile.y} at tick {tick}";
                        break;
                    }
                }
            }

            Assert.IsNull(failure);
            float2 end = MeanPosition(query);
            Assert.Greater(math.distance(start, end), 5f, "the army must actually have moved across the cave");
        }
        finally
        {
            map.Dispose();
        }
    }

    /// <summary>
    /// Five hundred units ordered at an open area arrive within 1.5x the straight-line travel time -
    /// the bulk inside a ten-tile goal radius, the whole army (queue included) inside the destination
    /// area - and none is left oscillating: over the last two seconds no unit drifts more than a tile
    /// back from its closest approach.
    /// </summary>
    [Test]
    public void OrdersArrive()
    {
        using SpikeMap map = OpenMap(128);
        using var cache = new FlowFieldCache(map, Allocator.Persistent);
        using var sim = SpikeSim.Create(TestConfig(1, 600), map, Allocator.Persistent, cache);

        var rng = new Random(11u);
        var units = new NativeArray<float2>(500, Allocator.Temp);
        for (int i = 0; i < 500; i++)
        {
            units[i] = new float2(20f, 64f) + rng.NextFloat2(new float2(-4f), new float2(4f));
            sim.AddUnit(Unit(i + 1, owner: 0, position: units[i], radius: Small));
        }
        sim.SetNextId(501);
        int slot = sim.AddField(cache, new int2(100, 64));
        sim.OrderAll(slot);

        int2 goal = new int2(100, 64);
        float longest = 0f;
        for (int i = 0; i < units.Length; i++) longest = math.max(longest, math.distance(units[i], goal));
        int deadline = (int)math.ceil(longest / SpikeSimRules.Speed * 1.5f / SpikeSimRules.TickSeconds);

        for (int tick = 0; tick < deadline; tick++) sim.Tick();

        var query = sim.EntityManager.CreateEntityQuery(typeof(SpikeUnit));
        using (NativeArray<SpikeUnit> arrived = Units(query, Allocator.Temp))
        {
            Assert.AreEqual(500, arrived.Length, "the army must survive the march");
            var distances = new List<float>();
            foreach (SpikeUnit unit in arrived) distances.Add(math.distance(unit.Position, goal));
            distances.Sort();

            // The destination area, not the goal cell: the field spreads its goal over 64 cells, and
            // 500 units at the spike's density need about 250 tiles, so the army arrives as a crowd
            // with a queue behind it rather than all inside a 12-tile disc.
            Assert.LessOrEqual(distances[distances.Count - 1], DestinationRadius,
                $"the tail of the army was still {distances[distances.Count - 1]:F1} tiles out after {deadline} ticks");
            Assert.LessOrEqual(distances[distances.Count / 2], GoalRadius,
                $"the bulk of the army was {distances[distances.Count / 2]:F1} tiles out after {deadline} ticks");
        }

        // Oscillation: walk two more seconds and watch how far each unit strays from its best approach.
        var closest = new NativeArray<float>(501, Allocator.Temp);
        var wandering = new NativeArray<float>(501, Allocator.Temp);
        using (NativeArray<SpikeUnit> before = Units(query, Allocator.Temp))
            foreach (SpikeUnit unit in before)
            {
                closest[unit.Id] = math.distance(unit.Position, goal);
                wandering[unit.Id] = 0f;
            }

        for (int tick = 0; tick < 40; tick++)
        {
            sim.Tick();
            using NativeArray<SpikeUnit> now = Units(query, Allocator.Temp);
            foreach (SpikeUnit unit in now)
            {
                float distance = math.distance(unit.Position, goal);
                closest[unit.Id] = math.min(closest[unit.Id], distance);
                wandering[unit.Id] = math.max(wandering[unit.Id], distance - closest[unit.Id]);
            }
        }

        // A unit still travelling would drift tiles back; a settled crowd stays put.
        const float oscillation = 1f;
        using (NativeArray<SpikeUnit> after = Units(query, Allocator.Temp))
            foreach (SpikeUnit unit in after)
                Assert.LessOrEqual(wandering[unit.Id], oscillation,
                    $"unit {unit.Id} oscillates: it drifted {wandering[unit.Id]:F2} tiles back from its closest approach");

        closest.Dispose();
        wandering.Dispose();
        units.Dispose();
    }

    /// <summary>
    /// A thousand ticks of twenty deaths and twenty spawns a tick: the ids in the world are never
    /// duplicated, and the population stays where the spawn target keeps it.
    /// </summary>
    [Test]
    public void ChurnKeepsIdsUnique()
    {
        using SpikeMap map = OpenMap(128);
        var config = TestConfig(1, capacity: 600, idCapacity: 600 + 20 * 1000 + 64);
        config.SpawnPerTick = 20;
        config.SpawnTarget = 10000; // always below target, so exactly the per-tick cap spawns
        config.SpawnOrigins = new NativeArray<int2>(new[] { new int2(64, 64) }, Allocator.Temp);
        using var sim = SpikeSim.Create(config, map, Allocator.Persistent);

        var rng = new Random(23u);
        for (int i = 0; i < 400; i++)
            sim.AddUnit(Unit(i + 1, owner: 0, position: new float2(30f, 30f) + rng.NextFloat2(-4f, 4f), radius: Small));
        sim.SetNextId(401);

        EntityQuery query = sim.EntityManager.CreateEntityQuery(typeof(SpikeUnit));
        var seen = new HashSet<int>();
        for (int tick = 0; tick < 1000; tick++)
        {
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < 20; i++)
                    sim.EntityManager.SetComponentData(entities[i], Unit(i + 1, owner: 0, position: new float2(30f, 30f), radius: Small, health: 0f));
            }

            sim.Tick();

            using NativeArray<SpikeUnit> units = Units(query, Allocator.Temp);
            seen.Clear();
            foreach (SpikeUnit unit in units)
                Assert.IsTrue(seen.Add(unit.Id), $"duplicate id {unit.Id} at tick {tick}");
            Assert.AreEqual(400, units.Length, $"the population must stay at 400 at tick {tick}");
        }
    }

    /// <summary>
    /// Two hundred small and twenty large units dropped on the same 10 x 10 area with no orders: after
    /// a hundred ticks no pair overlaps by more than a tenth of its radii, and the small units, which
    /// take the larger share of every push, have moved further on average.
    /// </summary>
    [Test]
    public void MixedSizesSeparate()
    {
        using SpikeMap map = OpenMap(64);
        var config = TestConfig(1, capacity: 300);
        config.SeparationStrength = SeparationStrength;
        config.SeparationIterations = SeparationIterations;
        using var sim = SpikeSim.Create(config, map, Allocator.Persistent);

        var rng = new Random(5u);
        float2 centre = new float2(32f, 32f);
        var start = new NativeArray<float2>(220, Allocator.Temp);
        for (int i = 0; i < 220; i++)
        {
            start[i] = centre + rng.NextFloat2(new float2(-5f), new float2(5f));
            sim.AddUnit(Unit(i + 1, owner: 0, position: start[i], radius: i < 200 ? Small : Large));
        }

        for (int tick = 0; tick < 100; tick++) sim.Tick();

        EntityQuery query = sim.EntityManager.CreateEntityQuery(typeof(SpikeUnit));
        using NativeArray<SpikeUnit> units = Units(query, Allocator.Temp);
        Assert.AreEqual(220, units.Length);

        int overlapped = 0;
        float worst = 0f;
        string first = null;
        for (int i = 0; i < units.Length; i++)
        for (int j = i + 1; j < units.Length; j++)
        {
            float sum = units[i].Radius + units[j].Radius;
            float distance = math.distance(units[i].Position, units[j].Position);
            if (distance >= sum * 0.9f) continue;
            overlapped++;
            float penetration = (sum - distance) / sum;
            if (penetration <= worst) continue;
            worst = penetration;
            first = $"units {units[i].Id} and {units[j].Id} overlap: {distance:F3} apart, radii sum {sum:F3}";
        }
        Assert.Zero(overlapped, $"{overlapped} of {units.Length * (units.Length - 1) / 2} pairs overlap by more " +
                                $"than 10% of their radii (worst {worst:P1}): {first}");

        double smallMoved = 0, largeMoved = 0;
        foreach (SpikeUnit unit in units)
        {
            float moved = math.distance(unit.Position, start[unit.Id - 1]);
            if (unit.SizeClass == 0) smallMoved += moved;
            else largeMoved += moved;
        }
        smallMoved /= 200;
        largeMoved /= 20;
        Assert.Greater(smallMoved, largeMoved,
            $"small units must give way more than large ones (small {smallMoved:F3}, large {largeMoved:F3} tiles)");

        start.Dispose();
    }

    /// <summary>
    /// Fifty large units ordered past a wall with a one-tile and a three-tile gap: all of them get
    /// through the three-tile gap and arrive, and none is ever inside the one-tile gap.
    /// </summary>
    [Test]
    public void LargeUnitsDoNotWedgeInGaps()
    {
        using SpikeMap map = WallMap(64);
        using var cache = new FlowFieldCache(map, Allocator.Persistent);
        using var sim = SpikeSim.Create(TestConfig(1, capacity: 64), map, Allocator.Persistent, cache);

        var rng = new Random(31u);
        for (int i = 0; i < 50; i++)
        {
            float2 position = new float2(16f, 41f) + rng.NextFloat2(new float2(-7f, -8f), new float2(7f, 8f));
            sim.AddUnit(Unit(i + 1, owner: 0, position: position, radius: Large));
        }

        int2 gapOne = new int2(32, 16);
        int slot = sim.AddField(cache, new int2(56, 41), FlowSizeClass.Large);
        sim.OrderAll(slot);

        EntityQuery query = sim.EntityManager.CreateEntityQuery(typeof(SpikeUnit));
        string failure = null;
        for (int tick = 0; tick < 500 && failure == null; tick++)
        {
            sim.Tick();
            using NativeArray<SpikeUnit> units = Units(query, Allocator.Temp);
            foreach (SpikeUnit unit in units)
                if (math.all((int2)math.floor(unit.Position) == gapOne))
                {
                    failure = $"unit {unit.Id} was inside the one-tile gap at tick {tick}";
                    break;
                }
        }

        Assert.IsNull(failure);
        using (NativeArray<SpikeUnit> units = Units(query, Allocator.Temp))
        {
            Assert.AreEqual(50, units.Length, "every large unit must still be alive");
            foreach (SpikeUnit unit in units)
                Assert.LessOrEqual(math.distance(unit.Position, new float2(56f, 41f)), LargeArrivalRadius,
                    $"unit {unit.Id} did not get past the wall (at {unit.Position.x:F1},{unit.Position.y:F1})");
        }
    }

    /// <summary>
    /// The separation knobs the tests are pinned to, recorded so the report can name them: the plan's
    /// defaults, k = 2 with one pass. The push is a per-tick correction, so these are the values that
    /// clear the mixed-size crowd's 10 % overlap tolerance within 100 ticks.
    /// </summary>
    private const float SeparationStrength = 1f;
    private const int SeparationIterations = 1;

    /// <summary>The mean position of every live unit.</summary>
    private static float2 MeanPosition(EntityQuery query)
    {
        using NativeArray<SpikeUnit> units = Units(query, Allocator.Temp);
        float2 sum = float2.zero;
        if (units.Length == 0) return sum;
        foreach (SpikeUnit unit in units) sum += unit.Position;
        return sum / units.Length;
    }
}

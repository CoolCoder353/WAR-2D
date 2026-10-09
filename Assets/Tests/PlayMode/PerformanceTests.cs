using System.Collections;
using System.Collections.Generic;
using Config;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using UnityEngine.TestTools;
using WAR2D.Sim;

/// <summary>
/// A small-scale regression guard that runs with the normal suite: 2 owners × 2,000 units fighting on a
/// 256² generated map. The real §4.1 gate is the player-build harness (<c>tools/perf-run.sh</c>).
/// </summary>
public class PerformanceTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        config.Match.Map.Size = 256;
        config.Match.Map.Seed = 5;
        config.Resources.StartingResources = 1e9f;
        DevApi.AllowForTests = true;
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        yield return PlayModeMatch.TearDown();
    }

    [UnityTest, Performance, Timeout(180000)]
    public IEnumerator TwoArmiesOfTwoThousandStayWithinTheTickBudget()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        WorldStateManager world = WorldStateManager.Instance;
        const int bot = 1000002, perOwner = 2000;
        GameCore.Instance.AddBot(bot, 1e9f);
        bool hqPlaced = false; // the bot needs an HQ, or win/loss ends the match at once
        foreach (int2 site in world.Map.HqSites)
        {
            for (int ring = 0; ring < 20 && !hqPlaced; ring++)
            for (int i = 0; i < math.max(1, 8 * ring) && !hqPlaced; i++)
                hqPlaced = world.DevPlaceBuilding(bot, BuildingType.Base, Ring(site, ring, i), 1e7f);
            if (hqPlaced) break;
        }
        Assert.IsTrue(hqPlaced);
        int[] owners = { PlayModeMatch.LocalOwner, bot };
        var centre = new int2(world.Map.Grid.Width / 2, world.Map.Grid.Height / 2);
        int2[] starts = { centre + new int2(-8, 0), centre + new int2(8, 0) };
        var ids = new List<int>[] { new List<int>(), new List<int>() };
        for (int o = 0; o < 2; o++)
        {
            int owner = o, placed = 0;
            for (int ring = 0; placed < perOwner && ring < 120; ring++)
            for (int i = 0; i < math.max(1, 8 * ring) && placed < perOwner; i++)
            {
                int2 tile = Ring(starts[o], ring, i);
                if (!world.Map.Grid.IsWalkable(tile)) continue;
                world.DevSpawnUnit(owners[o], (float2)tile + 0.5f, id => ids[owner].Add(id));
                placed++;
            }
        }
        yield return PlayModeMatch.WaitUntil(() => ids[0].Count + ids[1].Count >= 2 * perOwner * 9 / 10, 20f);
        for (int o = 0; o < 2; o++)
            SimContext.Current.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = OrderKind.AttackMove, OwnerId = owners[o], Tile = starts[1 - o], Ids = ids[o].ToArray() });

        var tickMain = new SampleGroup("tick.main", SampleUnit.Millisecond);
        var samples = new List<double>();
        int last = SimContext.Current.Clock.Tick;
        using (Measure.Frames().Scope())
        {
            float end = UnityEngine.Time.realtimeSinceStartup + 60f;
            while (samples.Count < 200)
            {
                Assert.That(UnityEngine.Time.realtimeSinceStartup, Is.LessThan(end), $"only {samples.Count} ticks in 60 s (state {GameCore.Instance.CurrentState})");
                yield return null;
                int tick = SimContext.Current.Clock.Tick;
                if (tick == last) continue;
                last = tick;
                double ms = SimTiming.LastMainThreadMs - SimTiming.LastBoundaryWaitMs;
                samples.Add(ms);
                Measure.Custom(tickMain, ms);
            }
        }
        samples.Sort();
        double p95 = samples[(int)(samples.Count * 0.95) - 1];
        Assert.That(p95, Is.LessThanOrEqualTo(25.0), $"tick main-thread p95 {p95:F2} ms (editor) is over 25 ms");
    }

    private static int2 Ring(int2 origin, int ring, int i)
    {
        if (ring == 0) return origin;
        int side = 2 * ring;
        if (i < side) return new int2(origin.x - ring + i, origin.y - ring);
        i -= side;
        if (i < side) return new int2(origin.x + ring, origin.y - ring + i);
        i -= side;
        if (i < side) return new int2(origin.x + ring - i, origin.y + ring);
        i -= side;
        return new int2(origin.x - ring, origin.y + ring - i);
    }
}

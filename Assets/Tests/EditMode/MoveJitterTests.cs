using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Owner playtest 2026-11-06: moving units jumped back and forth.</summary>
public class MoveJitterTests
{
    /// <summary>Ticks where a moving unit's step points back against its last one (a reversal), over all units and ticks.</summary>
    private static (int reversals, int steps) Measure(bool formation, int count, OrderKind kind = OrderKind.Move, bool crossing = false)
    {
        using var sim = new SimHarness(SimHarness.OpenMap(128));
        var boxes = new List<int[]>();
        var others = new List<int[]>();
        for (int i = 0; i < count; i++) boxes.Add(sim.Spawn(SimHarness.OwnerA, new float2(10.5f + i % 8 * 1.1f, 10.5f + i / 8 * 1.1f), UnitType.Tank));
        if (crossing)
            for (int i = 0; i < count; i++) others.Add(sim.Spawn(SimHarness.OwnerA, new float2(90.5f + i % 8 * 1.1f, 90.5f + i / 8 * 1.1f), UnitType.Tank));
        sim.Tick();
        List<int> ids = boxes.Select(b => b[0]).ToList();
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = kind, OwnerId = SimHarness.OwnerA, Tile = new int2(100, 100), Ids = ids.ToArray(), Formation = formation });
        if (crossing)
            sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = kind, OwnerId = SimHarness.OwnerA, Tile = new int2(12, 12), Ids = others.Select(b => b[0]).ToArray(), Formation = formation });
        var last = new Dictionary<int, float2>();
        var step = new Dictionary<int, float2>();
        int reversals = 0, steps = 0;
        for (int t = 0; t < 600; t++)
        {
            sim.Tick();
            foreach (Unit u in sim.Units())
            {
                if (last.TryGetValue(u.Id, out float2 was))
                {
                    float2 d = u.Position - was;
                    if (math.lengthsq(d) > 1e-6f)
                    {
                        steps++;
                        if (step.TryGetValue(u.Id, out float2 before) && math.dot(before, d) < -0.2f * math.length(before) * math.length(d)) reversals++;
                        step[u.Id] = d;
                    }
                }
                last[u.Id] = u.Position;
            }
        }
        return (reversals, steps);
    }

    [Test]
    public void AMovingGroupDoesNotJitter()
    {
        foreach (bool formation in new[] { true, false })
        foreach (bool crossing in new[] { false, true })
        {
            (int reversals, int steps) = Measure(formation, 40, OrderKind.Move, crossing);
            Assert.Less(reversals, steps / 50, $"formation {formation}, crossing {crossing}: {reversals} reversals in {steps} steps");
        }
    }
}

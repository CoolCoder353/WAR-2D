using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Owner playtest 2026-11-06: Builders ordered onto a blueprint stood idle instead of building it.</summary>
public class BuilderCrowdTests
{
    [Test]
    public void ACrowdOfBuildersAllGetToWork()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96), c => c.GetBuilding(BuildingType.HeavyFactory).Health = 100000);
        var boxes = new System.Collections.Generic.List<int[]>();
        for (int i = 0; i < 16; i++) boxes.Add(sim.Spawn(SimHarness.OwnerA, new float2(10.5f + i % 4 * 1.2f, 10.5f + i / 4 * 1.2f), UnitType.Builder));
        sim.Tick();
        var ids = boxes.Select(b => b[0]).ToList();
        Assert.That(ids, Has.None.EqualTo(-1));
        int2 anchor = new int2(40, 40);
        int factory = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.HeavyFactory, anchor);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Build, anchor, ids.ToArray());
        int idleOutOfReach = 0, samples = 0;
        for (int s = 0; s < 60; s++)
        {
            sim.TickSeconds(1f);
            if (s < 30) continue;
            foreach (Unit u in sim.Units().Where(u => ids.Contains(u.Id)))
            {
                float2 lo = Footprint.Start(anchor, new int2(3, 3)), hi = lo + 3;
                float d = math.length(math.max(math.max(lo - u.Position, u.Position - hi), 0f));
                samples++;
                if (d > 2.2f && u.OrderSlot < 0) idleOutOfReach++;
            }
        }
        Assert.Greater(samples, 0);
        Assert.LessOrEqual(idleOutOfReach, samples / 10, $"Builders stand idle out of reach: {idleOutOfReach} of {samples}");
    }
}

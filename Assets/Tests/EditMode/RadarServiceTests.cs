using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

public class RadarServiceTests
{
    private static List<(int owner, Alert alert)> Run(SimHarness sim, RadarService service, List<RadarView> radars, int seconds, ref double now)
    {
        var alerts = new List<(int, Alert)>();
        for (int i = 0; i < seconds * 5; i++)
        {
            sim.Tick(); sim.Tick(); sim.Tick(); sim.Tick();
            sim.Em.CompleteAllTrackedJobs();
            now += 0.2;
            service.Update(sim.Context.Data, sim.Context.Clock.UnitCount, radars, now, (o, a) => alerts.Add((o, a)));
        }
        return alerts;
    }

    private static List<RadarView> OneRadar() =>
        new List<RadarView> { new RadarView { Id = 1, OwnerId = SimHarness.OwnerA, Tile = new int2(20, 20), Centre = new float2(21, 21), Radius = 10f } };

    [Test]
    public void OneAlertToTheOwnerPerContact()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Spawn(SimHarness.OwnerA, new float2(5, 5));
        int[] enemy = sim.Spawn(SimHarness.OwnerB, new float2(50, 50));
        sim.TickSeconds(0.5f);
        var service = new RadarService(5f, 30f);
        List<RadarView> radars = OneRadar();
        double now = 0;
        Assert.IsEmpty(Run(sim, service, radars, 2, ref now));
        sim.Teleport(enemy[0], new float2(24, 22));
        var alerts = Run(sim, service, radars, 3, ref now);
        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(SimHarness.OwnerA, alerts[0].owner);
        Assert.AreEqual(AlertKind.EnemySpotted, alerts[0].alert.Kind);
        Assert.AreEqual(new int2(20, 20), alerts[0].alert.Tile, "the Radar's tile, never the enemy's");
    }

    [Test]
    public void AlliedUnitsDontPing()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Context.TeamResolver = _ => 0;
        sim.Spawn(SimHarness.OwnerA, new float2(5, 5));
        sim.Spawn(SimHarness.OwnerB, new float2(24, 22));
        sim.TickSeconds(0.5f);
        double now = 0;
        Assert.IsEmpty(Run(sim, new RadarService(5f, 30f), OneRadar(), 3, ref now));
    }

    [Test]
    public void ARadarNotListedNeverPings()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Spawn(SimHarness.OwnerA, new float2(5, 5));
        sim.Spawn(SimHarness.OwnerB, new float2(24, 22));
        sim.TickSeconds(0.5f);
        double now = 0;
        Assert.IsEmpty(Run(sim, new RadarService(5f, 30f), new List<RadarView>(), 3, ref now));
    }
}

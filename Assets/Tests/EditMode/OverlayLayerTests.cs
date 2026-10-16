using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Dev.Overlays;
using WAR2D.Sim;

public class OverlayLayerTests
{
    [Test]
    public void FootprintsMarksUsedTiles()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        sim.Map.QueueUsed(new int2(5, 6), true);
        sim.Tick();
        var layer = new FootprintsLayer();
        SimContext.RunningOverride = false; sim.Tick();
        layer.OnSettled(sim.Context.Data, sim.Context.Clock.Tick, sim.UnitCount);
        var canvas = new OverlayCanvas();
        layer.Draw(canvas);
        Assert.AreEqual(32, canvas.GridWidth);
        Assert.Greater(canvas.Texels[6 * 32 + 5].a, 0);
        Assert.AreEqual(0, canvas.Texels[6 * 32 + 4].a);
    }

    [Test]
    public void FogShowsTheChosenOwnersGrid()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        for (int i = 0; i < 8; i++) sim.Tick();
        var layer = new FogLayer { Args = Newtonsoft.Json.Linq.JObject.Parse($"{{\"owner\":{SimHarness.OwnerA}}}") };
        SimContext.RunningOverride = false; sim.Tick();
        layer.OnSettled(sim.Context.Data, sim.Context.Clock.Tick, sim.UnitCount);
        var canvas = new OverlayCanvas();
        layer.Draw(canvas);
        int cell = sim.Context.Data.FogCellOf(new float2(10.5f, 10.5f));
        Assert.Greater(canvas.Texels[cell].a, 0, "the unit's own cell is visible");
    }

    [Test]
    public void HashCountsUnitsPerCell()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        for (int i = 0; i < 4; i++) sim.Spawn(SimHarness.OwnerA, new float2(2.5f + i * 0.5f, 2.5f));
        sim.Tick(); sim.Tick();
        var layer = new HashLayer();
        SimContext.RunningOverride = false; sim.Tick();
        layer.OnSettled(sim.Context.Data, sim.Context.Clock.Tick, sim.UnitCount);
        var canvas = new OverlayCanvas();
        layer.Draw(canvas);
        Assert.Greater(canvas.Texels[0].a, 0);
    }

    private static OverlayCanvas Capture(SimHarness sim, OverlayLayer layer)
    {
        SimContext.RunningOverride = false; sim.Tick(); // settle the jobs before reading the SoA
        layer.OnSettled(sim.Context.Data, sim.Context.Clock.Tick, sim.UnitCount);
        var canvas = new OverlayCanvas();
        layer.Draw(canvas);
        return canvas;
    }

    [Test]
    public void FlowDrawsArrowsForAMoveOrder()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] id = sim.Spawn(SimHarness.OwnerA, new float2(5.5f, 5.5f));
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(50, 50), id);
        for (int i = 0; i < 6; i++) sim.Tick();
        Assert.Greater(Capture(sim, new FlowLayer()).Lines.Count, 0);
    }

    [Test]
    public void TargetsDrawsALineToTheTarget()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        sim.Spawn(SimHarness.OwnerB, new float2(12.5f, 10.5f));
        for (int i = 0; i < 6; i++) sim.Tick();
        var c = Capture(sim, new TargetsLayer());
        var d = sim.Context.Data;
        Assert.Greater(c.Lines.Count, 0, $"targets {d.Target[0]},{d.Target[1]} kinds {d.TargetKind[0]},{d.TargetKind[1]} count {sim.UnitCount} hp {d.Health[0]},{d.Health[1]}");
    }

    [Test]
    public void SectorsDrawsTheSectorGrid()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Tick();
        // 64 tiles, 32-tile sectors: three vertical and three horizontal lines.
        Assert.AreEqual(6, Capture(sim, new SectorsLayer()).Lines.Count);
    }

    [Test]
    public void InterestMarksTheOwnersUnitButNotAnUnseenEnemy()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        sim.Spawn(SimHarness.OwnerA, new float2(5.5f, 5.5f));
        sim.Spawn(SimHarness.OwnerB, new float2(90.5f, 90.5f));
        for (int i = 0; i < 8; i++) sim.Tick();
        var layer = new InterestLayer { Args = Newtonsoft.Json.Linq.JObject.Parse($"{{\"owner\":{SimHarness.OwnerA}}}") };
        Assert.AreEqual(2, Capture(sim, layer).Lines.Count, "one cross: A's own unit");
    }

    [Test]
    public void TerrainMarksChangedTiles()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        sim.ForceBuiltWall(new int2(7, 8));
        sim.Tick(); sim.Tick();
        OverlayCanvas canvas = Capture(sim, new TerrainChangesLayer());
        Assert.Greater(canvas.Texels[8 * 32 + 7].a, 0);
    }
}

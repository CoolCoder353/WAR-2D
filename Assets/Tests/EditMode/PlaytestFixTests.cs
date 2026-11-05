using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client;
using WAR2D.Client.Replay;
using WAR2D.Dev.DebugApi;
using WAR2D.Sim;
using WAR2D.UI;

/// <summary>The rules behind the fixes from the agent-played match of 2026-11-04 (known-issues.md).</summary>
public class PlaytestFixTests
{
    private const float RepairBelow = 0.9f;

    [Test]
    public void AScratchedBuildingIsNoWorkSiteButOneNeedingRepairIs()
    {
        Assert.IsFalse(Selection.IsWorkSite(100, 950f, 1000f, RepairBelow), "no Builder comes for a scratch, so Builders beside it are idle");
        Assert.IsTrue(Selection.IsWorkSite(100, 899f, 1000f, RepairBelow), "under AutoWork/RepairBelow an idle Builder takes it");
        Assert.IsTrue(Selection.IsWorkSite(40, 1000f, 1000f, RepairBelow), "a blueprint");
        Assert.IsFalse(Selection.IsWorkSite(100, 1000f, 1000f, RepairBelow));
    }

    [Test]
    public void TheHqHasAnIconThoughItsTypeIsNamedBase()
    {
        Assert.IsTrue(SelectionPanelController.TryIconOf(BuildingType.Base, out IconKind hq));
        Assert.AreEqual(IconKind.Hq, hq);
        Assert.IsTrue(SelectionPanelController.TryIconOf(BuildingType.Miner, out IconKind miner));
        Assert.AreEqual(IconKind.Miner, miner);
    }

    [Test]
    public void TheMatchLengthCountsTicks() => Assert.AreEqual(367.5f, MatchLength.Seconds(7350, 0.05f), 0.01f);

    [Test]
    public void AReplayOpensOnTheFirstHqWhenItsSummaryKnowsOne()
    {
        Assert.IsTrue(ReplayOpening.TryFirstHq(new SummaryData { HqX = new[] { 40f, 200f }, HqY = new[] { 60f, 10f } }, out float2 hq));
        Assert.AreEqual(new float2(40f, 60f), hq);
        Assert.IsFalse(ReplayOpening.TryFirstHq(null, out _), "a recording cut short has no summary");
        Assert.IsFalse(ReplayOpening.TryFirstHq(new SummaryData { HqX = new float[0], HqY = new float[0] }, out _));
    }

    [Test]
    public void ZoomingOutToShowARingStopsAtTheFirstStepThatDoes()
    {
        const int ppu = 64, h = 1080;
        float one = CameraZoom.SizeAt(1, ppu, h);
        float size = CameraZoom.OutToShow(one, 14f, ppu, h, 1f, 120f);
        Assert.GreaterOrEqual(size, 14f);
        Assert.Less(size / CameraZoom.SmoothFactor, 14f, "one step fewer would not show it");
        Assert.AreEqual(20f, CameraZoom.OutToShow(20f, 14f, ppu, h, 1f, 120f), "never zooms in");
        Assert.AreEqual(16f, CameraZoom.OutToShow(10f, 500f, ppu, h, 1f, 16f), "stops at the limit");
    }

    [Test]
    public void UnitsSaysWhenItsListStopsAtTheLimit()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        for (int i = 0; i < 5; i++) sim.Spawn(SimHarness.OwnerA, new float2(5.5f + i, 5.5f));
        sim.Tick();
        SimContext.RunningOverride = false; sim.Tick();
        var snap = new SettledSnapshot();
        snap.Capture(sim.Context.Data, sim.Context.Clock.Tick, sim.UnitCount);
        Assert.IsTrue((bool)JObject.FromObject(ReadCommands.Units(snap, JObject.Parse("{\"limit\":3}")))["truncated"]);
        Assert.IsFalse((bool)JObject.FromObject(ReadCommands.Units(snap, JObject.Parse("{\"limit\":5}")))["truncated"]);
    }

    private static List<(int owner, Alert alert)> Blast(params (float2 at, int owner, float radius)[] blasts) => Blast(false, blasts);

    private static List<(int owner, Alert alert)> Blast(bool unitKilled, params (float2 at, int owner, float radius)[] blasts)
    {
        var sent = new List<(int, Alert)>();
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Radar, new int2(40, 40), complete: true);
        sim.Tick();
        SimContext.RunningOverride = false; sim.Tick();
        if (unitKilled)
        {
            var health = sim.Context.Data.Health;
            health[0] = 0f;
        }
        var service = new AlertService(10f, 32, (owner, alerts) => { foreach (Alert a in alerts) sent.Add((owner, a)); });
        service.CollectBlasts(sim.Context.Data, sim.UnitCount, blasts, 0);
        service.Flush();
        return sent;
    }

    [Test]
    public void ABlastAlertsTheOwnerOfAUnitItHits()
    {
        List<(int owner, Alert alert)> sent = Blast((new float2(11.5f, 10.5f), SimHarness.OwnerB, 2f));
        Assert.AreEqual(1, sent.Count);
        Assert.AreEqual(SimHarness.OwnerA, sent[0].owner);
        Assert.AreEqual(AlertKind.UnderAttack, sent[0].alert.Kind);
        Assert.AreEqual(new int2(10, 10), sent[0].alert.Tile, "the receiver's own tile, not the blast's");
    }

    [Test]
    public void ABlastAlertsTheOwnerOfAUnitItKilled()
    {
        List<(int owner, Alert alert)> sent = Blast(true, (new float2(11.5f, 10.5f), SimHarness.OwnerB, 2f));
        Assert.AreEqual(1, sent.Count, "the dead unit is still in the arrays at 0 health when the blast is read");
    }

    [Test]
    public void ABlastAlertsTheOwnerOfABuildingItReaches()
    {
        List<(int owner, Alert alert)> sent = Blast((new float2(39f, 41f), SimHarness.OwnerB, 2f));
        Assert.AreEqual(1, sent.Count);
        Assert.AreEqual(new int2(40, 40), sent[0].alert.Tile);
    }

    [Test]
    public void NoAlertFromYourOwnBlastOrOneOutOfReach()
    {
        CollectionAssert.IsEmpty(Blast((new float2(11.5f, 10.5f), SimHarness.OwnerA, 2f)), "own blast");
        CollectionAssert.IsEmpty(Blast((new float2(25.5f, 25.5f), SimHarness.OwnerB, 2f)), "out of reach");
    }
}

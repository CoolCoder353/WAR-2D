using System.Collections;
using Config;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using WAR2D.Dev.DebugApi;
using WAR2D.UI;

/// <summary>What the agent-played match of 2026-11-04 found, in a real hosted match (known-issues.md).</summary>
public class PlaytestFixPlayModeTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
        DebugApi.AllowForTests = true;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        InputCommands.Reset();
        DebugApi.AllowForTests = false;
        DevApi.AllowForTests = false;
        yield return PlayModeMatch.TearDown();
    }

    private static IEnumerator Press(string keys)
    {
        JObject reply = null;
        var d = new DebugDispatcher(DebugServer.Registry, "t", () => false, () => false);
        d.Handle($"{{\"id\":1,\"token\":\"t\",\"cmd\":\"key\",\"args\":{{\"keys\":\"{keys}\"}}}}", s => reply = JObject.Parse(s));
        float until = Time.realtimeSinceStartup + 10f;
        while (reply == null && Time.realtimeSinceStartup < until) { d.Pump(Time.unscaledDeltaTime); yield return null; }
        Assert.IsTrue(reply != null && (bool)reply["ok"], keys + ": " + reply);
    }

    [UnityTest]
    public IEnumerator ABuildKeyWorksWithNoBuilderSelected()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 anchor = default;
        yield return PlayModeMatch.PlaceHQ(a => anchor = a);
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 20f);
        HudController hud = Object.FindAnyObjectByType<HudController>();
        yield return PlayModeMatch.WaitUntil(() => hud.Model.Resources >= config.GetBuilding(BuildingType.Radar).UpfrontCost, 10f);
        BuildingPlacement placement = BuildingPlacement.Instance;

        yield return Press("C");
        Assert.AreEqual(BuildingType.Radar, placement.PlacingType, "nothing selected");
        placement.Cancel();

        DevApi.AllowForTests = true;
        WorldStateManager wsm = WorldStateManager.Instance;
        bool spawned = false;
        for (int x = 4; x < 40 && !spawned; x++)
        {
            int2 tile = anchor + new int2(x, 0);
            if (wsm.Map.Grid.IsWalkable(tile)) spawned = wsm.DevSpawnUnit(PlayModeMatch.LocalOwner, (float2)tile + 0.5f, null, UnitType.Scout);
        }
        Assert.IsTrue(spawned, "room for the Scout");
        yield return PlayModeMatch.WaitUntil(() => HudTests.OwnUnits().Count > 0, 5f);
        UnitCommander.Instance.Selection.SelectIds(HudTests.OwnUnits());
        yield return PlayModeMatch.WaitUntil(() => hud.Selection.Count > 0, 5f);
        yield return Press("C");
        Assert.AreEqual(BuildingType.Radar, placement.PlacingType, "a Scout selected");
        placement.Cancel();

        Assert.IsTrue(HudTests.TryOwnBuilding(BuildingType.Base, out int hq));
        UnitCommander.Instance.Selection.SelectBuilding(hq);
        yield return PlayModeMatch.WaitUntil(() => hud.Selection.BuildingId == hq, 5f);
        yield return Press("Q");
        Assert.IsFalse(placement.IsPlacing, "with the HQ selected Q makes a Builder");
        yield return Press("C");
        Assert.AreEqual(BuildingType.Radar, placement.PlacingType, "the HQ doesn't use the bottom row");
        placement.Cancel();
    }

    [UnityTest]
    public IEnumerator TheMinersFacingWaitsBehindTheMenuAndCarriesToTheNextOne()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(_ => { });
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 20f);
        HudController hud = Object.FindAnyObjectByType<HudController>();
        BuildingPlacement placement = BuildingPlacement.Instance;

        placement.BeginPlacement(BuildingType.Miner);
        Assert.AreEqual(0f, placement.Rotation);
        hud.MatchMenu.Open();
        yield return null;
        yield return null;
        Assert.IsTrue(HudController.DialogOpen);
        yield return Press("R");
        Assert.AreEqual(0f, placement.Rotation, "R behind the menu does nothing");

        hud.MatchMenu.Close();
        yield return null;
        yield return null;
        Assert.IsFalse(HudController.DialogOpen);
        yield return Press("R");
        Assert.AreEqual(90f, placement.Rotation);

        placement.Cancel();
        placement.BeginPlacement(BuildingType.Miner);
        Assert.AreEqual(90f, placement.Rotation, "the next Miner along the vein keeps it");
        placement.BeginPlacement(BuildingType.Barracks);
        Assert.AreEqual(0f, placement.Rotation, "a building with one facing doesn't turn");
        placement.Cancel();
    }

    [UnityTest]
    public IEnumerator TheTimelineAndTheEndScreenCountTheSameSeconds()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return new WaitForSecondsRealtime(3f); // the HQ pick: on the match clock, but no play
        yield return PlayModeMatch.PlaceHQ(_ => { });
        yield return new WaitForSecondsRealtime(1f);
        MatchResult before = GameCore.LastMatchStats;
        GameCore.Instance.Cmd_Surrender();
        yield return PlayModeMatch.WaitUntil(() => GameCore.LastMatchStats != before && GameCore.LastMatchSummary.HasValue, 10f);
        SummaryData summary = GameCore.LastMatchSummary.Value;
        Assert.That(summary.Eliminations.Length, Is.GreaterThan(0));
        // The win/loss check ends the match a moment after the surrender; the 3 s of HQ pick must be in neither.
        float length = GameCore.LastMatchStats.DurationSeconds;
        Assert.That(summary.Eliminations[0].Time, Is.LessThanOrEqualTo(length));
        Assert.That(summary.Eliminations[0].Time, Is.GreaterThan(length - 1.5f));
    }

    [UnityTest]
    public IEnumerator TheHqPickOpensWithTheStartRingInView()
    {
        config.Match.Map.Size = 256; // a generated map: start options are dealt
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.WaitUntil(() => Camera.main != null && Camera.main.orthographicSize >= WAR2D.World.MapGenerator.HqClearRadius, 10f,
            () => "camera size " + (Camera.main != null ? Camera.main.orthographicSize : -1f));
    }
}

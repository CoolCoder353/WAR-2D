using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Config;
using Mirror;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using WAR2D.Client;
using WAR2D.Dev.DebugApi;
using WAR2D.Net.Replication;

/// <summary>A drag box takes units; with the assign key held it takes only buildings. Driven with real input in a hosted match.</summary>
public class SelectBuildingsPlayModeTests
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

    private static IEnumerator Call(string command, string args)
    {
        JObject reply = null;
        var d = new DebugDispatcher(DebugServer.Registry, "t", () => false, () => false);
        d.Handle($"{{\"id\":1,\"token\":\"t\",\"cmd\":\"{command}\",\"args\":{args}}}", s => reply = JObject.Parse(s));
        float until = Time.realtimeSinceStartup + 10f;
        while (reply == null && Time.realtimeSinceStartup < until) { d.Pump(Time.unscaledDeltaTime); yield return null; }
        Assert.IsTrue(reply != null && (bool)reply["ok"], command + ": " + reply);
    }

    private static IEnumerator Drag(int2 from, int2 to) =>
        Call("mouse", $"{{\"x\":{from.x},\"y\":{from.y},\"space\":\"tile\",\"action\":\"drag\",\"to\":[{to.x},{to.y}],\"duration\":0.1}}");

    [UnityTest, Timeout(120000)]
    public IEnumerator TheAssignKeyMakesADragTakeOnlyBuildingsOfEveryKind()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 20f);
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        Assert.IsTrue(WorldStateManager.Instance.TryFindBuildableAnchor(BuildingType.Radar, 0f, local, out int2 radarAt), "room for the Radar");
        Assert.IsTrue(PlayModeMatch.PlaceBuilt(BuildingType.Radar, radarAt));
        yield return PlayModeMatch.WaitUntil(() => ClientBuildings.Current.Entries.Values.Any(e => e.Data.buildingType == BuildingType.Radar && !e.Ghost), 5f);
        Assert.IsTrue(WorldStateManager.Instance.TryFindSpawnTile(hq, out int2 scoutAt), "room for the Scout");
        DevApi.AllowForTests = true;
        Assert.IsTrue(WorldStateManager.Instance.DevSpawnUnit(PlayModeMatch.LocalOwner, (float2)scoutAt + 0.5f, null, UnitType.Scout));
        int2 lo = math.min(math.min(hq, radarAt), scoutAt) - 3, hi = math.max(math.max(hq, radarAt), scoutAt) + 3;
        var units = new List<int>();
        yield return PlayModeMatch.WaitUntil(() =>
        {
            units.Clear();
            ClientWorld.Instance.CollectOwn(PlayModeMatch.LocalOwner, units, UnitType.Scout);
            return units.Count > 0;
        }, 5f);

        Camera camera = Camera.main;
        camera.transform.position = new Vector3(hi.x, hi.y, camera.transform.position.z); // the press lands mid-screen, clear of the HUD
        yield return null;
        Selection selection = UnitCommander.Instance.Selection;

        yield return Drag(hi, lo);
        Assert.That(selection.Selected, Does.Contain(units[0]), "a plain drag takes the units");
        Assert.AreEqual(0, selection.SelectedBuildings.Count, "and no building");

        yield return Call("key", "{\"keys\":\"ctrl\",\"action\":\"down\"}");
        yield return Drag(hi, lo);
        yield return Call("key", "{\"keys\":\"ctrl\",\"action\":\"up\"}");
        Assert.AreEqual(0, selection.Selected.Count, "no unit with the assign key held");
        List<BuildingType> kinds = selection.SelectedBuildings.Select(id => ClientBuildings.Current.Entries[id].Data.buildingType).ToList();
        Assert.That(kinds, Does.Contain(BuildingType.Base), "a spawner");
        Assert.That(kinds, Does.Contain(BuildingType.Radar), "and a building that makes nothing");
    }
}

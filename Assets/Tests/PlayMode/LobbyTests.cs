using System.Collections;
using Config;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.UI;

/// <summary>The UI Toolkit menus and lobby in Main_Menu, as the host. (Remote clients: tools/qa-smoke.sh.)</summary>
public class LobbyTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        yield return PlayModeMatch.TearDown();
    }

    private static MenuController Menu() => Object.FindAnyObjectByType<MenuController>();
    private static VisualElement Root() => Menu().GetComponent<UIDocument>().rootVisualElement;
    private static ClientPlayer Local => NetworkClient.localPlayer.GetComponent<ClientPlayer>();

    /// <summary>Hosts from the Play screen and waits for the lobby.</summary>
    private static IEnumerator HostFromMenu()
    {
        VisualElement root = Root();
        Assert.That(root.Q("main-menu").ClassListContains("menu-screen--visible"), Is.True, "starts on the main menu");
        HudTests.Click(root.Q<Button>("play-button"));
        yield return null;
        Assert.That(root.Q("host-join").ClassListContains("menu-screen--visible"), Is.True, "Play opens the host/join screen");
        HudTests.Click(root.Q<Button>("host-button"));
        yield return PlayModeMatch.WaitUntil(() => root.Q("lobby").ClassListContains("menu-screen--visible"), 15f);
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator HostSetsSettingsAndPreviewChanges()
    {
        yield return HostFromMenu();
        MenuController menu = Menu();
        VisualElement root = Root();
        HudTests.Click(root.Q<Button>("size-256"));
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.Settings.MapSize == 256 && menu.Preview.Texture != null && !menu.Preview.Busy, 10f);
        Texture2D texture = menu.Preview.Texture;
        Color32[] before = texture.GetPixels32();
        Assert.That(root.Q("preview-markers").childCount, Is.EqualTo(8), "one marker per HQ site");

        HudTests.Click(root.Q<Button>("mode-teams"));
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.Settings.Mode == MatchMode.Teams, 5f);
        Assert.That(GameCore.Instance.Settings.MapSize, Is.EqualTo(256), "one change keeps the others");

        uint seed = GameCore.Instance.Settings.Seed;
        HudTests.Click(root.Q<Button>("reroll-button"));
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.Settings.Seed != seed, 5f);
        yield return PlayModeMatch.WaitUntil(() => !menu.Preview.Busy && !Same(before, menu.Preview.Texture.GetPixels32()), 10f);
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator RerollChangesSeedAndClearsReady()
    {
        yield return HostFromMenu();
        VisualElement root = Root();
        HudTests.Click(root.Q<Button>("ready-button"));
        yield return PlayModeMatch.WaitUntil(() => Local.ready && root.Q<Button>("ready-button").text == "Cancel ready", 5f);
        uint seed = GameCore.Instance.Settings.Seed;
        HudTests.Click(root.Q<Button>("reroll-button"));
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.Settings.Seed != seed, 5f);
        yield return PlayModeMatch.WaitUntil(() => !Local.ready, 5f);
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator StartRequiresAllReadyAndTwoTeams()
    {
        yield return HostFromMenu();
        VisualElement root = Root();
        Assert.That(root.Q<Button>("start-button").enabledSelf, Is.False, "nobody is ready");
        GameCore.Instance.Cmd_StartGame();
        yield return new WaitForSeconds(0.5f);
        Assert.That(GameCore.Instance.CurrentState, Is.EqualTo(GameState.Lobby), "the server refuses an unready start");

        HudTests.Click(root.Q<Button>("ready-button"));
        yield return PlayModeMatch.WaitUntil(() => Local.ready, 5f);
        GameCore.Instance.Cmd_StartGame();
        yield return new WaitForSeconds(0.5f);
        Assert.That(GameCore.Instance.CurrentState, Is.EqualTo(GameState.Lobby), "a lone player is one team: nobody to fight");
        StringAssert.Contains("two", root.Q<Label>("lobby-status").text);
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator ColoursAreValidatedAndSettingsAreHostOnly()
    {
        yield return HostFromMenu();
        Assert.That(Local.colourIndex, Is.EqualTo(0), "the first player gets the first colour");
        GameCore.Instance.Cmd_SetColour(5);
        yield return PlayModeMatch.WaitUntil(() => Local.colourIndex == 5, 5f);
        GameCore.Instance.Cmd_SetColour(200);
        yield return new WaitForSeconds(0.3f);
        Assert.That(Local.colourIndex, Is.EqualTo(5), "colours outside the palette are refused");

        MatchSettings bad = GameCore.Instance.Settings;
        bad.MapSize = 333;
        GameCore.Instance.Cmd_SetMatchSettings(bad);
        yield return new WaitForSeconds(0.3f);
        Assert.That(GameCore.Instance.Settings.MapSize, Is.Not.EqualTo(333), "settings outside the lobby's lists are refused");
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator JoinRejectsAnInvalidAddress()
    {
        VisualElement root = Root();
        HudTests.Click(root.Q<Button>("play-button"));
        yield return null;
        root.Q<TextField>("address-field").value = "not an address";
        HudTests.Click(root.Q<Button>("join-button"));
        yield return null;
        Assert.That(root.Q("address-input").ClassListContains("input--error"), Is.True);
        Assert.That(NetworkClient.active, Is.False, "nothing was joined");
    }

    private static bool Same(Color32[] a, Color32[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b) return false;
        return true;
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator MenuBattleRunsAndIsGoneOnceHostIsPressed()
    {
        WAR2D.Client.MenuBattle.Enabled = true;
        yield return PlayModeMatch.WaitUntil(() => WAR2D.Client.MenuBattle.Instance != null && WAR2D.Client.MenuBattle.Instance.Running, 10f);
        yield return PlayModeMatch.WaitUntil(() => WAR2D.Client.ClientWorld.Instance != null && WAR2D.Client.ClientWorld.Instance.KnownCount > 100, 20f);

        // The armies march at each other: it is a battle, not a still picture.
        WAR2D.Client.ClientWorld view = WAR2D.Client.ClientWorld.Instance;
        var ids = new System.Collections.Generic.List<int>();
        view.QueryBox(Unity.Mathematics.float2.zero, new Unity.Mathematics.float2(4096f), 1, ids);
        Assert.That(ids.Count, Is.GreaterThan(100), "army A is visible");
        var start = new System.Collections.Generic.Dictionary<int, Unity.Mathematics.float2>();
        foreach (int id in ids) if (view.TryGet(id, out WAR2D.Client.ClientUnitView unit)) start[id] = unit.Position;
        yield return PlayModeMatch.WaitUntil(() =>
        {
            foreach (var pair in start)
                if (view.TryGet(pair.Key, out WAR2D.Client.ClientUnitView unit) && Unity.Mathematics.math.distance(unit.Position, pair.Value) > 3f) return true;
            return false;
        }, 15f);

        // Owner report (2026-10-10): the battle drew no shots or explosions, as the real game does.
        Assert.That(view.ViewCamera, Is.Not.Null, "tracers are culled against the menu's camera");
        bool sawTracer = false, sawExplosion = false;
        yield return PlayModeMatch.WaitUntil(() =>
        {
            sawTracer |= UnityEngine.GameObject.Find("Tracer") != null;
            sawExplosion |= UnityEngine.GameObject.Find("Explosion") != null;
            return sawTracer && sawExplosion;
        }, 40f);

        yield return HostFromMenu();
        Assert.That(WAR2D.Client.MenuBattle.Instance.Running, Is.False, "the battle is torn down before hosting");
        Assert.That(WAR2D.Sim.SimContext.RunningOverride, Is.Null);
        foreach (Unity.Entities.World w in Unity.Entities.World.All) Assert.That(w.Name, Is.Not.EqualTo("MenuBattle"), "its World is disposed");
        Assert.That(WAR2D.Client.ClientWorld.Instance == null, Is.True, "its view is gone");
    }
}

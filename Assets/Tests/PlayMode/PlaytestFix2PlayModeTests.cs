using System.Collections;
using System.Linq;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.Net.Replication;
using WAR2D.UI;

/// <summary>The second agent-played session of 2026-11-04, in a real hosted match (known-issues.md).</summary>
public class PlaytestFix2PlayModeTests
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

    private IEnumerator HostWithAis(int ais, int pickSeconds)
    {
        config.Match.Map.Size = 256; // a generated map: start options are dealt
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        GameCore core = GameCore.Instance;
        MatchSettings settings = core.Settings;
        settings.StartPickSeconds = pickSeconds;
        settings.Seed = 21;
        core.Settings = settings;
        foreach (int owner in core.AiPlayers.Keys.ToList()) core.Cmd_RemoveAi(owner);
        yield return PlayModeMatch.WaitUntil(() => core.AiPlayers.Count == 0, 5f);
        for (int i = 0; i < ais; i++) Assert.That(core.AddAi(AiDifficulty.Easy, AiStyle.Balanced), Is.Not.Null);
        DevApi.AllowForTests = true; // a test lobby skips the ready check
        core.Cmd_StartGame();
        yield return PlayModeMatch.WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        DevApi.AllowForTests = false;
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator ARandomPickOpensWithTheOwnHqInView()
    {
        yield return HostWithAis(1, 0); // Random: the server places every HQ at a random option
        int me = PlayModeMatch.LocalOwner;
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing && ClientBuildings.Current.TryFindOwn(me, BuildingType.Base, out _), 30f);
        ClientBuildings.Current.TryFindOwn(me, BuildingType.Base, out ClientBuildings.Entry hq);
        float2 centre = hq.Data.position + new float2(2.5f, 2.5f);
        yield return PlayModeMatch.WaitUntil(() =>
        {
            Camera cam = Camera.main;
            return cam != null && CameraZoom.InView(new float2(cam.transform.position.x, cam.transform.position.y), cam.orthographicSize, cam.aspect, centre);
        }, 5f, () => $"camera {Camera.main.transform.position}, HQ {centre}");
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator ASpectatorHasNoControlsLeftToPress()
    {
        yield return HostWithAis(2, 0);
        GameCore core = GameCore.Instance;
        yield return PlayModeMatch.WaitUntil(() => core.CurrentState == GameState.Playing, 30f);
        VisualElement root = HudTests.HudRoot();
        Assert.That(Shown(root.Q("gift-button")), Is.True, "playing: the controls are there");
        Assert.That(Shown(root.Q("command-card-instance")), Is.True);

        core.Cmd_Surrender();
        yield return PlayModeMatch.WaitUntil(() => root.Q("spectating-panel") != null && Shown(root.Q("spectating-panel")), 10f);
        Assert.That(core.CurrentState, Is.EqualTo(GameState.Playing), "the two AIs play on");
        yield return null;
        foreach (string name in new[] { "gift-button", "diplomacy-button", "command-card-instance", "squad-bar-instance", "selection-panel-instance" })
            Assert.That(Shown(root.Q(name)), Is.False, name);
    }

    private static bool Shown(VisualElement element) =>
        element != null && element.resolvedStyle.display != DisplayStyle.None && element.style.display != DisplayStyle.None && !element.ClassListContains("top-bar__hidden");
}

using System.Collections;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// <summary>Private start points: dealt by the server, picked by placing the HQ, placed for the player when the pick time says so.</summary>
public class StartPointsPlayModeTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        config.Match.Map.Size = 256; // a generated map has start sites; Map_2's tilemaps have none
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown() => PlayModeMatch.TearDown();

    /// <summary>Hosts with the given pick time and starts the match; returns once the match scene is up.</summary>
    private IEnumerator Start(int pickSeconds)
    {
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        MatchSettings settings = GameCore.Instance.Settings;
        settings.StartPickSeconds = pickSeconds;
        GameCore.Instance.Settings = settings;
        bool devAllowed = DevApi.AllowForTests;
        DevApi.AllowForTests = true;
        GameCore.Instance.Cmd_StartGame();
        yield return PlayModeMatch.WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        DevApi.AllowForTests = devAllowed;
    }

    private static ClientPlayer Local => NetworkClient.localPlayer.GetComponent<ClientPlayer>();

    private static void AssertHqInAnOption()
    {
        ServerPlayer me = GameCore.Instance.GetServerPlayerById(PlayModeMatch.LocalOwner);
        Assert.That(me.StartOptions, Has.Length.EqualTo(GameCore.Instance.Settings.StartOptions));
        Assert.That(me.StartOptions, Does.Contain(me.StartPick), "the HQ went into one of the dealt options");
        Assert.That(Local.OwnStartOptions, Is.EqualTo(me.StartOptions), "the player was told their own options");
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator PickTimeZero_PlacesTheHqAtOnce()
    {
        yield return Start(0);
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 30f);
        Assert.That(Local.hasPlacedHQ, Is.True);
        yield return PlayModeMatch.WaitUntil(() => Local.OwnStartOptions.Length > 0, 5f);
        AssertHqInAnOption();
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator TimedPick_PlacesTheHqAtTheDeadline()
    {
        yield return Start(2);
        Assert.That(GameCore.Instance.HqPlacementEndTime, Is.GreaterThan(0), "a timed pick has a deadline");
        Assert.That(Local.hasPlacedHQ, Is.False, "nothing is placed before the deadline");
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 30f);
        yield return PlayModeMatch.WaitUntil(() => Local.OwnStartOptions.Length > 0, 5f);
        AssertHqInAnOption();
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator Unlimited_WaitsUntilThePlayerPlaces()
    {
        yield return Start(StartPickRules.Unlimited);
        yield return new UnityEngine.WaitForSeconds(3f);
        Assert.That(GameCore.Instance.CurrentState, Is.EqualTo(GameState.PlacingHQ), "the match waits");
        Assert.That(GameCore.Instance.HqPlacementEndTime, Is.EqualTo(0));
        yield return PlayModeMatch.PlaceHQ(null);
        AssertHqInAnOption();
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator Hq_OutsideTheDealtOptions_IsRefused()
    {
        yield return Start(StartPickRules.Unlimited);
        yield return PlayModeMatch.WaitUntil(() => Local.OwnStartOptions.Length > 0, 5f);
        int2[] sites = WorldStateManager.Instance.Map.HqSites;
        int other = -1;
        for (int i = 0; i < sites.Length && other < 0; i++) if (System.Array.IndexOf(Local.OwnStartOptions, i) < 0) other = i;
        Assert.That(WorldStateManager.Instance.CheckPlacement(BuildingType.Base, sites[other], 0f, Local), Is.EqualTo(PlacementResult.OutsideStartOptions));
        Assert.That(WorldStateManager.Instance.CheckPlacement(BuildingType.Base, sites[Local.OwnStartOptions[0]], 0f, Local), Is.EqualTo(PlacementResult.Ok));
    }
}

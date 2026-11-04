using System.Collections;
using System.Linq;
using Config;
using Mirror;
using NUnit.Framework;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using WAR2D.Sim;

/// <summary>v0.18: Rematch goes back to the same lobby and nothing of the first match survives into the second.</summary>
public class RematchPlayModeTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown() => PlayModeMatch.TearDown();

    private IEnumerator StartFromLobby()
    {
        bool dev = DevApi.AllowForTests;
        DevApi.AllowForTests = true;
        GameCore.Instance.Cmd_StartGame();
        yield return PlayModeMatch.WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && WorldStateManager.Instance.Map != null, 30f);
        DevApi.AllowForTests = dev;
    }

    [UnityTest, Timeout(180000)]
    public IEnumerator ARematchStartsCleanWithTheSeatsKept()
    {
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        GameCore core = GameCore.Instance;
        Assert.That(core.AddAi(AiDifficulty.Easy, AiStyle.Balanced), Is.Not.Null);
        string callsign = core.AiPlayers.Values.First().Callsign;
        int colour = NetworkClient.localPlayer.GetComponent<ClientPlayer>().colourIndex;

        yield return StartFromLobby();
        yield return PlayModeMatch.PlaceHQ(null);
        PlayModeMatch.FastForward(4);
        yield return PlayModeMatch.WaitUntil(() => core.Summary != null && core.MatchClock.Now > 12, 30f);
        uint firstSeed = core.Settings.Seed;
        int me = PlayModeMatch.LocalOwner;

        // Rematch is refused while the match runs.
        core.Cmd_Rematch();
        yield return null;
        yield return null;
        Assert.That(core.CurrentState, Is.EqualTo(GameState.Playing));

        Assert.That(core.ServerPlayers.Count, Is.EqualTo(1), "before the end");
        core.DeclareWinner(new[] { me });
        yield return PlayModeMatch.WaitUntil(() => GameCore.LastMatchSummary.HasValue, 10f);
        Assert.That(GameCore.LastMatchSummary.Value.Times.Length, Is.GreaterThan(0), "the graphs arrived");

        Assert.That(core.ServerPlayers.Count, Is.EqualTo(1), "after the end");
        core.Cmd_Rematch();
        yield return PlayModeMatch.WaitUntil(() => core.CurrentState == GameState.Lobby && SceneManager.GetActiveScene().name == "Main_Menu", 20f);
        Time_Reset();
        yield return PlayModeMatch.WaitUntil(() => WorldStateManager.Instance == null, 10f);
        yield return null;

        Assert.That(NetworkClient.isConnected, Is.True, "still connected");
        Assert.That(core.ServerPlayers.Count, Is.EqualTo(1), "the seat is kept");
        Assert.That(core.AiPlayers.Values.Single().Callsign, Is.EqualTo(callsign), "the AI seat is kept");
        Assert.That(NetworkClient.localPlayer.GetComponent<ClientPlayer>().colourIndex, Is.EqualTo(colour));
        Assert.That(NetworkClient.localPlayer.GetComponent<ClientPlayer>().ready, Is.False);
        Assert.That(core.Settings.Seed, Is.Not.EqualTo(firstSeed), "a new seed");

        yield return StartFromLobby();
        yield return PlayModeMatch.PlaceHQ(null);
        Assert.That(core.Stats.Of(me).UnitsLost, Is.Zero, "fresh statistics");
        Assert.That(SimContext.Current, Is.Not.Null);
        Assert.That(core.AiPlayers.Values.Single().Player.state, Is.EqualTo(PlayerState.Playing), "the AI plays again");
    }

    private static void Time_Reset() => UnityEngine.Time.captureDeltaTime = 0f;
}

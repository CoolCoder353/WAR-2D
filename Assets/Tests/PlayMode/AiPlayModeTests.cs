using System.Collections;
using Config;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using WAR2D.Ai;

/// <summary>
/// v0.14 plan Tasks 7 and 10: AI players play a real hosted match (place, mine, build, produce, attack), sped up with
/// the dev fast-forward: AI against AI, a Private single-player 1v1 against Easy, and co-op (a human and a Normal AI
/// against a Normal AI).
/// </summary>
public class AiPlayModeTests
{
    private GameConfigData config;
    private WAR2D.Net.Steam.ISteamBackend steamBefore;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        config.Match.Map.Size = 256; // a generated map: start sites, gems and the region plan
        steamBefore = WAR2D.Net.Steam.SteamServices.Backend;
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        WAR2D.Net.Steam.SteamServices.Backend = steamBefore;
        return PlayModeMatch.TearDown();
    }

    private static AiState StateOf(AiSeat seat) => ((AiBrain)seat.Brain).Context.State;

    private static string Describe(AiSeat seat)
    {
        AiState s = StateOf(seat);
        ArmyManager army = ((AiBrain)seat.Brain).Manager<ArmyManager>();
        return $"{seat.Callsign}: playing {s.PlayingSeconds:F0}s, combat {s.Combat.Count}, army {army.Mode}, attacks {army.Attacks}, retreats {army.Retreats}, miners {s.CompleteCount[(int)BuildingType.Miner]}, free {s.Free:F0}, income {s.Income:F1}, upkeep {s.Upkeep:F1}, units {s.View.OwnUnits.Count}, producers {s.Producers.Count}, queued {s.QueuedCount[(int)UnitType.Tank]}";
    }

    private static bool Producing(AiSeat seat)
    {
        AiState s = StateOf(seat);
        int producers = s.CompleteCount[(int)BuildingType.Barracks] + s.CompleteCount[(int)BuildingType.HeavyFactory] + s.CompleteCount[(int)BuildingType.SiegeWorkshop];
        return s.CompleteCount[(int)BuildingType.Miner] >= 1 && producers >= 1 && s.Combat.Count >= 3;
    }

    [UnityTest, Timeout(420000)]
    public IEnumerator AnEasyAndANormalAiPlaceMineBuildProduceAndAttack()
    {
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        GameCore core = GameCore.Instance;
        MatchSettings settings = core.Settings;
        settings.StartPickSeconds = StartPickRules.Unlimited;
        settings.Seed = 21;
        core.Settings = settings;
        AiSeat easy = core.AddAi(AiDifficulty.Easy, AiStyle.Rusher);
        AiSeat normal = core.AddAi(AiDifficulty.Normal, AiStyle.Rusher);
        Assert.That(easy, Is.Not.Null);
        Assert.That(normal, Is.Not.Null);

        DevApi.AllowForTests = true; // a test lobby skips the ready check
        core.Cmd_StartGame();
        yield return PlayModeMatch.WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        Assert.That(core.CurrentState, Is.EqualTo(GameState.PlacingHQ));
        Assert.That(easy.Brain, Is.InstanceOf<AiBrain>(), "a lobby AI gets a brain at match start");

        // Both place their HQs themselves, inside one of their own options, while the host still picks.
        yield return PlayModeMatch.WaitUntil(() => core.HasPlacedHq(easy.OwnerId) && core.HasPlacedHq(normal.OwnerId), 15f);
        Assert.That(easy.Player.StartOptions, Does.Contain(easy.Player.StartPick), "Easy's HQ is in one of its options");
        Assert.That(normal.Player.StartOptions, Does.Contain(normal.Player.StartPick), "Normal's HQ is in one of its options");
        yield return PlayModeMatch.PlaceHQ(null); // the host, passive from here on

        // Sped up (the dev fast-forward): the tick runs several times per frame.
        PlayModeMatch.FastForward(8);
        yield return PlayModeMatch.WaitUntil(() => Producing(easy) && Producing(normal), 150f, () => Describe(easy) + " | " + Describe(normal));
        ArmyManager easyArmy = ((AiBrain)easy.Brain).Manager<ArmyManager>(), normalArmy = ((AiBrain)normal.Brain).Manager<ArmyManager>();
        yield return PlayModeMatch.WaitUntil(() => easyArmy.Attacks + normalArmy.Attacks > 0 || core.CurrentState == GameState.GameOver, 200f, () => Describe(easy) + " | " + Describe(normal));
        Assert.That(easyArmy.Attacks + normalArmy.Attacks, Is.GreaterThan(0), "and eventually one of them attacks");
    }

    /// <summary>Starts the lobby's match (a test lobby skips the ready check) and waits for the match scene.</summary>
    private IEnumerator StartMatch(GameCore core)
    {
        DevApi.AllowForTests = true;
        core.Cmd_StartGame();
        yield return PlayModeMatch.WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        Assert.That(core.CurrentState, Is.EqualTo(GameState.PlacingHQ));
    }

    private static void SpeedUp()
    {
        PlayModeMatch.FastForward(8);
    }

    /// <summary>
    /// Spec §12's single-player test: with Steam off, a Private lobby, one human against one Easy AI. The AI places its
    /// HQ, builds Miners, a producer and an army, and attacks the human (a building of theirs it has seen).
    /// </summary>
    [UnityTest, Timeout(480000)]
    public IEnumerator APrivateOneVsOneAgainstEasyBuildsProducesAndAttacksTheHuman()
    {
        WAR2D.Net.Steam.SteamServices.Backend = null; // Steam off (-noSteam): Private needs no Steam
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        GameCore core = GameCore.Instance;
        MatchSettings settings = core.Settings;
        settings.Visibility = LobbyVisibility.Private;
        settings.StartPickSeconds = StartPickRules.Unlimited;
        settings.Seed = 21;
        core.Settings = settings;
        AiSeat easy = core.AddAi(AiDifficulty.Easy, AiStyle.Rusher);
        Assert.That(easy, Is.Not.Null);
        yield return StartMatch(core);
        Assert.That(core.Settings.Visibility, Is.EqualTo(LobbyVisibility.Private));
        int me = PlayModeMatch.LocalOwner;

        yield return PlayModeMatch.WaitUntil(() => core.HasPlacedHq(easy.OwnerId), 15f);
        yield return PlayModeMatch.PlaceHQ(null); // the human, passive from here on
        SpeedUp();
        yield return PlayModeMatch.WaitUntil(() => Producing(easy), 100f, () => Describe(easy));
        ArmyManager army = ((AiBrain)easy.Brain).Manager<ArmyManager>();
        yield return PlayModeMatch.WaitUntil(() => army.Attacks > 0 || core.CurrentState == GameState.GameOver, 100f, () => Describe(easy));
        Assert.That(army.Attacks, Is.GreaterThan(0), "Easy attacks");
        Assert.That(army.TargetOwner, Is.EqualTo(me), "the human, its only enemy");
    }

    /// <summary>
    /// Spec §12's co-op test: a human and a Normal AI on one team against a Normal AI. Both AIs build and produce; the
    /// enemy attacks the team, and the ally never targets the human (an independent ally on the human's side).
    /// </summary>
    [UnityTest, Timeout(480000)]
    public IEnumerator CoOpAHumanAndANormalAiAgainstANormalAi()
    {
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        GameCore core = GameCore.Instance;
        MatchSettings settings = core.Settings;
        settings.Mode = MatchMode.Teams;
        settings.StartPickSeconds = StartPickRules.Unlimited;
        settings.Seed = 21;
        core.Settings = settings;
        NetworkClient.localPlayer.GetComponent<ClientPlayer>().lobbyTeam = 0;
        AiSeat ally = core.AddAi(AiDifficulty.Normal, AiStyle.Balanced, lobbyTeam: 0);
        AiSeat enemy = core.AddAi(AiDifficulty.Normal, AiStyle.Rusher, lobbyTeam: 1);
        Assert.That(ally, Is.Not.Null);
        Assert.That(enemy, Is.Not.Null);
        yield return StartMatch(core);
        int me = PlayModeMatch.LocalOwner;
        Assert.That(core.TeamOf(ally.OwnerId), Is.EqualTo(core.TeamOf(me)), "the ally is on the human's team");
        Assert.That(core.TeamOf(enemy.OwnerId), Is.Not.EqualTo(core.TeamOf(me)));

        yield return PlayModeMatch.WaitUntil(() => core.HasPlacedHq(ally.OwnerId) && core.HasPlacedHq(enemy.OwnerId), 15f);
        yield return PlayModeMatch.PlaceHQ(null);
        SpeedUp();
        yield return PlayModeMatch.WaitUntil(() => Producing(ally) && Producing(enemy), 100f, () => Describe(ally) + " | " + Describe(enemy));
        ArmyManager allyArmy = ((AiBrain)ally.Brain).Manager<ArmyManager>(), enemyArmy = ((AiBrain)enemy.Brain).Manager<ArmyManager>();
        bool allyTargetedHuman = false, enemyTargetedOutsider = false;
        yield return PlayModeMatch.WaitUntil(() =>
        {
            if (allyArmy.Mode == ArmyMode.Attack && allyArmy.TargetOwner == me) allyTargetedHuman = true;
            if (enemyArmy.Mode == ArmyMode.Attack && enemyArmy.TargetOwner != me && enemyArmy.TargetOwner != ally.OwnerId) enemyTargetedOutsider = true;
            return enemyArmy.Attacks + allyArmy.Attacks > 0 || core.CurrentState == GameState.GameOver;
        }, 100f, () => Describe(ally) + " | " + Describe(enemy));
        // The match is a real, unscripted one: whichever AI strikes first (or ends it) is not fixed, so one attack by either side is the signal.
        Assert.That(enemyArmy.Attacks + allyArmy.Attacks, Is.GreaterThan(0), "an AI attacks");
        Assert.That(allyTargetedHuman, Is.False, "the ally never attacks its teammate");
        Assert.That(enemyTargetedOutsider, Is.False, "the enemy's targets are the human or the ally");
    }
}

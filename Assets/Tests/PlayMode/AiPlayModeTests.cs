using System.Collections;
using Config;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using WAR2D.Ai;

/// <summary>v0.14 plan Task 7: AI players play a real hosted match (place, mine, build, produce, attack), sped up.</summary>
public class AiPlayModeTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        config.Match.Map.Size = 256; // a generated map: start sites, gems and the region plan
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Time.timeScale = 1f;
        Time.maximumDeltaTime = 1f / 3f;
        DevApi.AllowForTests = false;
        return PlayModeMatch.TearDown();
    }

    private static AiState StateOf(AiSeat seat) => ((AiBrain)seat.Brain).Context.State;

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
        Time.maximumDeltaTime = 0.5f;
        Time.timeScale = 8f;
        yield return PlayModeMatch.WaitUntil(() => Producing(easy) && Producing(normal), 150f);
        ArmyManager easyArmy = ((AiBrain)easy.Brain).Manager<ArmyManager>(), normalArmy = ((AiBrain)normal.Brain).Manager<ArmyManager>();
        yield return PlayModeMatch.WaitUntil(() => easyArmy.Attacks + normalArmy.Attacks > 0 || core.CurrentState == GameState.GameOver, 200f);
        Assert.That(easyArmy.Attacks + normalArmy.Attacks, Is.GreaterThan(0), "and eventually one of them attacks");
    }
}

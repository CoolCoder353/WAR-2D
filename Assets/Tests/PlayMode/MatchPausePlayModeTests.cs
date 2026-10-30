using System;
using System.Collections;
using System.IO;
using System.Linq;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.Ai;
using WAR2D.Net.Replay;
using WAR2D.UI;
using Object = UnityEngine.Object;

/// <summary>
/// v0.14 plan Task 9: with one human connected the in-match menu pauses the match (the tick, income, upkeep,
/// production, the AI and every match timer) and closing it resumes; with two humans it is refused; the dev pause goes
/// through the same pause; a replay of a paused match has no gap.
/// </summary>
public class MatchPausePlayModeTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        config.Match.Map.Size = 256; // a generated map: the AI needs start sites
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        return PlayModeMatch.TearDown();
    }

    /// <summary>What a pause must hold still, read on the server.</summary>
    private struct Snapshot
    {
        public int Tick, Thinks;
        public float Resources, Progress;
        public int Queued;
        public double Clock, Played;

        public static Snapshot Take(AiSeat ai, int hqId)
        {
            GameCore core = GameCore.Instance;
            var s = new Snapshot
            {
                Tick = WAR2D.Sim.SimContext.Current.Clock.Tick,
                Thinks = ((AiBrain)ai.Brain).Thinks,
                Resources = core.GetServerPlayerById(PlayModeMatch.LocalOwner).Resources,
                Clock = core.MatchClock.Now,
                Played = NetworkTime.time - core.MatchStartTime,
            };
            EntityManager em = PlayModeMatch.Em;
            using EntityQuery q = em.CreateEntityQuery(typeof(BuildingData), typeof(SpawnerData));
            foreach (Entity e in q.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                if (em.GetComponentData<BuildingData>(e).id != hqId) continue;
                SpawnerData spawner = em.GetComponentData<SpawnerData>(e);
                s.Progress = spawner.progress;
                s.Queued = spawner.count;
            }
            return s;
        }
    }

    private static int OwnHqId()
    {
        EntityManager em = PlayModeMatch.Em;
        using EntityQuery q = em.CreateEntityQuery(typeof(BuildingData), typeof(SpawnerData));
        foreach (Entity e in q.ToEntityArray(Unity.Collections.Allocator.Temp))
        {
            BuildingData b = em.GetComponentData<BuildingData>(e);
            if (b.ownerId == PlayModeMatch.LocalOwner && b.buildingType == BuildingType.Base) return b.id;
        }
        return 0;
    }

    private IEnumerator HostWithAi(Action<AiSeat> seat, int pickSeconds)
    {
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        GameCore core = GameCore.Instance;
        MatchSettings settings = core.Settings;
        settings.StartPickSeconds = pickSeconds;
        settings.Seed = 21;
        core.Settings = settings;
        AiSeat ai = core.AddAi(AiDifficulty.Normal, AiStyle.Balanced);
        Assert.That(ai, Is.Not.Null);
        DevApi.AllowForTests = true; // a test lobby skips the ready check
        core.Cmd_StartGame();
        yield return PlayModeMatch.WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        DevApi.AllowForTests = false;
        seat(ai);
    }

    [UnityTest, Timeout(180000)]
    public IEnumerator TheMenuPausesAOneHumanMatchAndClosingItResumes()
    {
        AiSeat ai = null;
        yield return HostWithAi(s => ai = s, 60);
        GameCore core = GameCore.Instance;
        int me = PlayModeMatch.LocalOwner;
        Assert.That(core.CurrentState, Is.EqualTo(GameState.PlacingHQ));
        Assert.That(core.HqPlacementEndTime, Is.GreaterThan(0), "a pick deadline");

        // While HQs are placed: the pick timer and the match clock freeze.
        Assert.That(core.TrySetMenuPause(me, true), Is.True, "the one human may pause");
        yield return null;
        double left = core.HqPlacementEndTime - NetworkTime.time, clock = core.MatchClock.Now;
        yield return new WaitForSecondsRealtime(1.5f);
        Assert.That(core.HqPlacementEndTime - NetworkTime.time, Is.EqualTo(left).Within(0.25), "the pick timer is frozen (its end moves by the paused time)");
        Assert.That(core.MatchClock.Now, Is.EqualTo(clock), "the match clock stops");
        Assert.That(core.TrySetMenuPause(me, false), Is.True);

        yield return PlayModeMatch.WaitUntil(() => core.HasPlacedHq(ai.OwnerId), 20f);
        yield return PlayModeMatch.PlaceHQ(null);
        int hq = OwnHqId();
        Assert.That(hq, Is.Not.Zero);
        Assert.That(PlayerActions.ChangeQueues(me, new[] { hq }, 10), Is.True, "Builders queued: production runs");
        yield return new WaitForSecondsRealtime(1.5f);

        // Through the HUD: opening the menu pauses, and the menu shows it.
        HudController hud = Object.FindAnyObjectByType<HudController>();
        VisualElement root = hud.GetComponent<UIDocument>().rootVisualElement;
        hud.MatchMenu.Open();
        yield return PlayModeMatch.WaitUntil(() => core.IsMenuPaused && hud.MatchMenu.ShowsPaused, 5f);
        Assert.That(root.Q<Label>("match-menu-title").text, Is.EqualTo(InMatchMenuController.PausedTitle));
        Assert.That(root.Q("in-match-menu").ClassListContains("in-match-menu--paused"), Is.True, "the Paused banner shows");
        Assert.That(root.Q<Label>("match-menu-note").text, Is.EqualTo(InMatchMenuController.PausedNote));
        yield return null;
        yield return null;
        Snapshot before = Snapshot.Take(ai, hq);
        yield return new WaitForSecondsRealtime(2f);
        Snapshot during = Snapshot.Take(ai, hq);
        Assert.That(during.Tick, Is.EqualTo(before.Tick), "the tick stops");
        Assert.That(during.Resources, Is.EqualTo(before.Resources), "no income, upkeep or spending");
        Assert.That(during.Progress, Is.EqualTo(before.Progress), "production stops");
        Assert.That(during.Queued, Is.EqualTo(before.Queued));
        Assert.That(during.Thinks, Is.EqualTo(before.Thinks), "the AI doesn't think");
        Assert.That(during.Clock, Is.EqualTo(before.Clock), "the match clock stops");
        Assert.That(during.Played, Is.EqualTo(before.Played).Within(0.25), "the match time shown doesn't run");

        // Settings opened from the menu keeps the pause.
        hud.MatchMenu.Close();
        hud.Settings.Open();
        yield return null;
        yield return null;
        Assert.That(core.MatchPause.Paused, Is.True, "Settings keeps the pause");
        hud.Settings.Close(); // back to the menu
        yield return null;
        Assert.That(hud.MatchMenu.IsOpen, Is.True);

        hud.MatchMenu.Close();
        yield return PlayModeMatch.WaitUntil(() => !core.IsPaused, 5f);
        Assert.That(root.Q<Label>("match-menu-title").text, Is.EqualTo(InMatchMenuController.Title));
        yield return new WaitForSecondsRealtime(2f);
        Snapshot after = Snapshot.Take(ai, hq);
        Assert.That(after.Tick, Is.GreaterThan(during.Tick), "the tick runs again");
        Assert.That(after.Thinks, Is.GreaterThan(during.Thinks), "the AI thinks again");
        Assert.That(after.Clock, Is.GreaterThan(during.Clock), "the match clock runs");
        Assert.That(after.Resources != during.Resources || after.Queued != during.Queued, Is.True, "the economy and production run");
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator WithTwoHumansTheMenuDoesntPause()
    {
        AiSeat ai = null;
        yield return HostWithAi(s => ai = s, 0);
        GameCore core = GameCore.Instance;
        int me = PlayModeMatch.LocalOwner;
        yield return PlayModeMatch.WaitUntil(() => core.CurrentState != GameState.PlacingHQ || core.HasPlacedHq(ai.OwnerId), 20f);

        // A second human connected (a stand-in record: only the count matters to the rule).
        var other = new GameObject("second human").AddComponent<NetworkIdentity>();
        core.ServerPlayers[other] = new ServerPlayer(null, 0f);
        try
        {
            Assert.That(core.TrySetMenuPause(me, true), Is.False, "two humans: the menu never pauses");
            Assert.That(core.MatchPause.Paused, Is.False);
        }
        finally { core.ServerPlayers.Remove(other); }

        // A pause that stops qualifying (a second human arrives) is dropped.
        Assert.That(core.TrySetMenuPause(me, true), Is.True);
        core.ServerPlayers[other] = new ServerPlayer(null, 0f);
        try { core.UpdatePause(); }
        finally { core.ServerPlayers.Remove(other); }
        Assert.That(core.MatchPause.Paused, Is.False, "dropped with two humans");
        Object.Destroy(other.gameObject);

        // Nor may anyone else's id pause (an AI, or a made-up owner).
        Assert.That(core.TrySetMenuPause(ai.OwnerId, true), Is.False, "an AI never pauses");
        Assert.That(core.TrySetMenuPause(12345, true), Is.False);
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator TheDevPauseGoesThroughTheMatchPause()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        GameCore core = GameCore.Instance;
        DevApi.AllowForTests = true;
        var d = new WAR2D.Dev.DebugApi.DebugDispatcher(WAR2D.Dev.DebugApi.DebugServer.Registry, "t", () => NetworkServer.active, () => NetworkClient.active);
        string reply = null;
        d.Handle("{\"id\":1,\"token\":\"t\",\"cmd\":\"pause\",\"args\":{}}", r => reply = r);
        StringAssert.Contains("\"ok\":true", reply);
        Assert.That(core.MatchPause.Dev, Is.True);
        yield return null;
        yield return null;
        int tick = WAR2D.Sim.SimContext.Current.Clock.Tick;
        double clock = core.MatchClock.Now;
        yield return new WaitForSecondsRealtime(1f);
        Assert.That(WAR2D.Sim.SimContext.Current.Clock.Tick, Is.EqualTo(tick), "the tick stops");
        Assert.That(core.MatchClock.Now, Is.EqualTo(clock), "and every match timer with it");
        Assert.That(core.IsPaused, Is.True, "clients are told");
        Assert.That(core.IsMenuPaused, Is.False, "the menu's Paused state is only for the menu pause");
        d.Handle("{\"id\":2,\"token\":\"t\",\"cmd\":\"resume\",\"args\":{}}", r => reply = r);
        Assert.That(core.MatchPause.Paused, Is.False);
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.That(WAR2D.Sim.SimContext.Current.Clock.Tick, Is.GreaterThan(tick));
    }

    [UnityTest, Timeout(150000)]
    public IEnumerator AReplayOfAPausedMatchHasNoGap()
    {
        config.Match.Map.Seed = 11;
        config.Replays.Enabled = true;
        DateTime started = DateTime.UtcNow.AddSeconds(-1);
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        GameCore core = GameCore.Instance;
        int me = PlayModeMatch.LocalOwner;
        float wallStart = Time.realtimeSinceStartup;
        yield return new WaitForSecondsRealtime(1f);
        Assert.That(core.TrySetMenuPause(me, true), Is.True);
        yield return new WaitForSecondsRealtime(3f);
        Assert.That(core.TrySetMenuPause(me, false), Is.True);
        yield return new WaitForSecondsRealtime(1f);
        core.DeclareWinner(new[] { me });
        yield return PlayModeMatch.WaitUntil(() => WorldStateManager.Instance.Recorder.Finished, 10f);
        float wall = Time.realtimeSinceStartup - wallStart; // the recording runs on a few seconds after the end

        ReplaySummary saved = ReplayLibrary.List().FirstOrDefault(s => s.Utc >= started && s.Header.Seed == 11);
        Assert.That(saved, Is.Not.Null, "a replay of the match");
        try
        {
            Assert.That(saved.DurationSeconds, Is.GreaterThan(0f));
            Assert.That(saved.DurationSeconds, Is.LessThan(wall - 2f), "the 3 s pause isn't in the replay's length");
            Assert.That(saved.DurationSeconds, Is.GreaterThan(wall - 3f - 1f), "only the pause is missing");
            using FileStream f = File.OpenRead(saved.Path);
            Assert.IsTrue(ReplayReader.TryOpen(f, out ReplayReader r, out string error), error);
            int last = -1, biggestStep = 0;
            using (r)
                while (r.TryNext(out int tick, out ReplayChannel c, out _))
                {
                    if (c == ReplayChannel.End) break;
                    if (last >= 0) biggestStep = Math.Max(biggestStep, tick - last);
                    last = tick;
                }
            // Records come at least once a second (the timeline); a 3 s hole would show the pause. Ticks stop while paused, so there is none.
            Assert.That(biggestStep * saved.Header.TickSeconds, Is.LessThanOrEqualTo(1.05f), "no gap in the recording: playback goes straight through");
        }
        finally { File.Delete(saved.Path); }
    }
}

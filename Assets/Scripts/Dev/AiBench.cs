using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Config;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;
using WAR2D.Dev.Logging;

/// <summary>
/// One AI-vs-AI match for the v0.14 bench (<c>tools/ai-bench.sh</c>, spec §12). Started with <c>-perf -aiBench
/// &lt;result.json&gt;</c> (and <c>-port n</c> when several run at once), it hosts a lobby, adds two AI seats
/// (<c>-benchA</c> / <c>-benchB</c> as <c>Difficulty:Style</c>), starts a 1v1 Free-for-all on a generated map
/// (<c>-benchSize</c>, <c>-benchSeed</c>) with the host out of the match (it places no HQ, so the win/loss rules drop it
/// at once), runs the match at <c>-benchScale</c> times speed (the dev fast-forward) until GameOver or
/// <c>-benchCapMinutes</c> of match time, and writes the result as JSON: the winner, the match length, and per seat its
/// style, peak army, first attack, attacks, retreats, highest balance, unpaid seconds and whether it conceded. A match
/// still running at the cap is a <c>cap</c> result (a failure to fix). Replays are off. Does nothing without the flags.
/// </summary>
public sealed class AiBench : MonoBehaviour
{
    /// <summary>True when this process runs a bench match (<c>-perf -aiBench</c>).</summary>
    public static bool Requested => DevApi.PerfFlag && PerfMatch.ArgValue("-aiBench") != null;

    private sealed class SeatResult
    {
        public int owner;
        public string difficulty, style, callsign;
        public bool won, conceded;
        public int peakArmy, attacks, retreats, finalArmy, finalBuildings;
        public float firstAttackSeconds = -1f, peakResources, unpaidSeconds;
        public Dictionary<string, int> finalUnits = new Dictionary<string, int>();
        /// <summary>Each match minute: minute, balance, income and upkeep (its estimates), army, buildings, Miners, producers, army mode.</summary>
        public List<string> timeline = new List<string>();
        /// <summary>The army's last decisions (attacks, staging, retreats, recalls).</summary>
        public string[] armyLog;
        [JsonIgnore] public int nextMinute;
    }

    private sealed class BenchResult
    {
        public int size;
        public uint seed;
        public string outcome; // "win", "draw", "cap", "error"
        public float matchSeconds, wallSeconds;
        public string error;
        public List<SeatResult> seats = new List<SeatResult>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (!Requested) return;
        var go = new GameObject("AiBench");
        DontDestroyOnLoad(go);
        go.AddComponent<AiBench>();
    }

    private readonly BenchResult result = new BenchResult();
    private string outPath;
    private float wallStart, watchdogMinutes = 70f;
    private bool finished;

    /// <summary>A stuck run (an exception in the match loop) still ends, as an error.</summary>
    private void Update()
    {
        if (!finished && Time.realtimeSinceStartup - wallStart > watchdogMinutes * 60f) Finish("error", "watchdog: the bench loop stopped");
    }

    private IEnumerator Start()
    {
        wallStart = Time.realtimeSinceStartup;
        outPath = PerfMatch.ArgValue("-aiBench");
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        yield return new WaitUntil(() => GameManager.Instance != null);

        result.size = int.TryParse(PerfMatch.ArgValue("-benchSize"), out int size) ? size : 256;
        result.seed = uint.TryParse(PerfMatch.ArgValue("-benchSeed"), out uint seed) && seed != 0 ? seed : 1u;
        float capMinutes = float.TryParse(PerfMatch.ArgValue("-benchCapMinutes"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cap) ? cap : 40f;
        float scale = float.TryParse(PerfMatch.ArgValue("-benchScale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) ? Mathf.Clamp(s, 1f, 100f) : 32f;
        float wallCapMinutes = float.TryParse(PerfMatch.ArgValue("-benchWallMinutes"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float w) ? w : 60f;
        watchdogMinutes = wallCapMinutes + 5f;

        GameConfigData config = ConfigLoader.LoadConfig();
        config.Replays.Enabled = false;
        config.Match.CountdownSeconds = 0.5f;
        if (!ConfigLoader.IsValid) { Finish("error", "GameConfig.xml is invalid"); yield break; }

        try { GameManager.Instance.HostServer(); }
        catch (Exception e) { Finish("error", $"could not host: {e.Message}"); yield break; }
        float hostStart = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => (NetworkClient.isConnected && NetworkClient.localPlayer != null && GameCore.Instance != null) || Time.realtimeSinceStartup - hostStart > 30f);
        GameCore core = GameCore.Instance;
        if (core == null || NetworkClient.localPlayer == null) { Finish("error", "could not host"); yield break; }

        var seats = new List<AiSeat>();
        foreach (string arg in new[] { "-benchA", "-benchB" })
        {
            string spec = PerfMatch.ArgValue(arg) ?? "Normal:Balanced";
            string[] parts = spec.Split(':');
            if (!Enum.TryParse(parts[0], out AiDifficulty difficulty) || !Enum.TryParse(parts.Length > 1 ? parts[1] : "Random", out AiStyle style))
            {
                Finish("error", $"bad seat '{spec}'");
                yield break;
            }
            AiSeat seat = core.AddAi(difficulty, style);
            if (seat == null) { Finish("error", $"could not add seat '{spec}'"); yield break; }
            seats.Add(seat);
        }

        MatchSettings settings = core.Settings;
        settings.Mode = MatchMode.FreeForAll;
        settings.MapSize = result.size;
        settings.Seed = result.seed;
        settings.StartPickSeconds = StartPickRules.Unlimited;
        core.DevStartMatch(settings);
        float startAt = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => (WorldStateManager.Instance != null && WorldStateManager.Instance.Sim != null && core.CurrentState == GameState.PlacingHQ) || Time.realtimeSinceStartup - startAt > 60f);
        if (core.CurrentState != GameState.PlacingHQ) { Finish("error", "the match did not start"); yield break; }

        // The host sits out: marked placed without an HQ, it holds nothing and the win/loss rules drop it at once.
        NetworkClient.localPlayer.GetComponent<ClientPlayer>().hasPlacedHQ = true;
        core.CheckHQPlacementProgress();

        Time.timeScale = scale;
        Time.maximumDeltaTime = Mathf.Max(1f / 3f, 0.05f * scale);

        var bySeat = new Dictionary<AiSeat, SeatResult>();
        foreach (AiSeat seat in seats)
        {
            var r = new SeatResult { owner = seat.OwnerId, difficulty = seat.Difficulty.ToString(), callsign = seat.Callsign };
            bySeat[seat] = r;
            result.seats.Add(r);
        }

        double playingSince = double.NaN;
        float nextSample = 0f;
        while (true)
        {
            WorldStateManager world = WorldStateManager.Instance;
            double now = world != null && world.AiRunner != null ? world.AiRunner.Now : 0;
            GameState state = core.CurrentState;
            if (state == GameState.Playing && double.IsNaN(playingSince)) playingSince = now;
            double played = double.IsNaN(playingSince) ? 0 : now - playingSince;
            if (state == GameState.Playing && Time.realtimeSinceStartup >= nextSample)
            {
                nextSample = Time.realtimeSinceStartup + 0.25f;
                foreach (AiSeat seat in seats) Sample(seat, bySeat[seat], (float)played);
                result.matchSeconds = (float)played;
            }
            if (state == GameState.GameOver || state == GameState.Lobby) break;
            if (played > capMinutes * 60f) { Finish("cap", null); yield break; }
            if (Time.realtimeSinceStartup - wallStart > wallCapMinutes * 60f) { Finish("cap", "wall-clock cap"); yield break; }
            yield return null;
        }

        int winners = 0;
        foreach (AiSeat seat in seats)
        {
            SeatResult r = bySeat[seat];
            r.won = seat.Player.state == PlayerState.Playing && core.CurrentState == GameState.GameOver;
            if (r.won) winners++;
        }
        Finish(winners > 0 ? "win" : "draw", null);
    }

    /// <summary>Reads one seat's state (its style, army, attacks and balance) from the brain's dev status.</summary>
    private static void Sample(AiSeat seat, SeatResult r, float played)
    {
        r.style = seat.ResolvedStyle.ToString();
        r.peakResources = Mathf.Max(r.peakResources, seat.Player.Resources);
        r.unpaidSeconds = seat.Player.UnpaidSeconds;
        if (!(seat.Brain is IAiBrainStatus brain)) return;
        Dictionary<string, object> status = brain.Status();
        int army = 0, buildings = 0;
        r.finalUnits.Clear();
        if (status.TryGetValue("units", out object u) && u is Dictionary<string, int> units)
            foreach (KeyValuePair<string, int> e in units)
            {
                r.finalUnits[e.Key] = e.Value;
                if (e.Key != nameof(UnitType.Builder)) army += e.Value;
            }
        if (status.TryGetValue("buildings", out object b) && b is Dictionary<string, int> built)
            foreach (KeyValuePair<string, int> e in built) buildings += e.Value;
        if (seat.Player.state == PlayerState.Playing)
        {
            r.finalArmy = army;
            r.finalBuildings = buildings;
        }
        r.peakArmy = Mathf.Max(r.peakArmy, army);
        int attacks = status.TryGetValue("attacks", out object a) ? Convert.ToInt32(a) : 0;
        if (attacks > 0 && r.firstAttackSeconds < 0f) r.firstAttackSeconds = played;
        r.attacks = attacks;
        r.retreats = status.TryGetValue("retreats", out object re) ? Convert.ToInt32(re) : 0;
        r.conceded = status.TryGetValue("conceded", out object c) && c is bool conceded && conceded;
        if (status.TryGetValue("armyLog", out object log) && log is string[] lines) r.armyLog = lines;
        if (played >= r.nextMinute * 60f && seat.Player.state == PlayerState.Playing)
        {
            int Count(string key) => status.TryGetValue("buildings", out object bs) && bs is Dictionary<string, int> d && d.TryGetValue(key, out int n) ? n : 0;
            r.timeline.Add($"{r.nextMinute}m res {seat.Player.Resources:F0} inc {Convert.ToSingle(status["incomeEst"]):F0} upk {Convert.ToSingle(status["upkeepEst"]):F0} army {army} bld {buildings} min {Count("Miner")} bar {Count("Barracks")} hf {Count("HeavyFactory")} sw {Count("SiegeWorkshop")} tur {Count("GunTurret")} exp {status["expansions"]} bp {status["blueprints"]} idle {status["idleBuilders"]} req {status["requests"]} eb {status["knownEnemyBases"]} sc {status["scout"]} {status["army"]}");
            r.nextMinute++;
        }
    }

    private void Finish(string outcome, string error)
    {
        if (finished) return;
        finished = true;
        result.outcome = outcome;
        result.error = error;
        result.wallSeconds = Time.realtimeSinceStartup - wallStart;
        try
        {
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, JsonConvert.SerializeObject(result, Formatting.Indented));
        }
        catch (Exception e) { WarLog.Error(LogCategory.Dev, $"could not write {outPath}: {e.Message}"); }
        WarLog.Info(LogCategory.Dev, $"bench match {outcome} after {result.matchSeconds:F0} s ({result.wallSeconds:F0} s wall)");
        Time.timeScale = 1f;
        Application.Quit(outcome == "error" ? 1 : 0);
    }
}

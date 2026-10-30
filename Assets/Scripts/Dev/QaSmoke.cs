using System.Collections;
using System.IO;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// <summary>
/// Two-process smoke test for real host/client matches (run it with <c>tools/qa-smoke.sh</c>). <c>-qaHost</c> hosts and starts the match once a
/// second player joins; <c>-qaJoin &lt;address&gt;</c> joins. Both write their log, state and screenshots
/// to <c>-qaOut &lt;dir&gt;</c>, then quit. Does nothing without the flags.
/// </summary>
public sealed class QaSmoke : MonoBehaviour
{
    private string dir, role;
    private StreamWriter log;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        bool host = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-qaHost") >= 0;
        if (!host && PerfMatch.ArgValue("-qaJoin") == null) return;
        var go = new GameObject("QaSmoke");
        DontDestroyOnLoad(go);
        QaSmoke qa = go.AddComponent<QaSmoke>();
        qa.role = host ? "host" : "client";
    }

    private IEnumerator Start()
    {
        Application.runInBackground = true; // both windows keep running while the other has focus
        dir = PerfMatch.ArgValue("-qaOut") ?? Application.persistentDataPath;
        Directory.CreateDirectory(dir);
        log = new StreamWriter(Path.Combine(dir, role + ".txt")) { AutoFlush = true };
        Application.logMessageReceived += (message, trace, type) =>
        {
            if (type != LogType.Log) log.WriteLine($"[{type}] {message}\n{trace}");
        };
        yield return new WaitUntil(() => GameManager.Instance != null);
        yield return new WaitForSecondsRealtime(4f); // the menu battle gets going
        Shot("menu");
        yield return null; // the shot is taken at the end of the frame
        if (role == "host")
        {
            var menu = FindAnyObjectByType<WAR2D.UI.MenuController>();
            menu.Settings.Open();
            yield return null;
            yield return null;
            Shot("settings-graphics");
            yield return null;
            Click(menu, "tab-controls");
            yield return null;
            yield return null;
            Shot("settings-controls");
            yield return null;
            Click(menu, "tab-audio");
            yield return null;
            yield return null;
            Shot("settings-audio");
            yield return null;
            menu.Settings.Close();
        }
        if (role == "host")
        {
            GameManager.Instance.HostServer();
            yield return Until(() => GameCore.Instance != null && GameCore.Instance.ServerPlayers.Count >= 2, 90f);
            GameCore.Instance.Cmd_SetReady(true);
            yield return Until(() => LobbyRules.CanStart(GameCore.Instance.LobbyStartState()), 30f);
            yield return new WaitForSecondsRealtime(2f);
            Shot("lobby");
            GameCore.Instance.Cmd_StartGame();
        }
        else
        {
            GameManager.Instance.ConnectToServer(PerfMatch.ArgValue("-qaJoin"));
            yield return Until(() => NetworkClient.isConnected && GameCore.Instance != null && NetworkClient.localPlayer != null, 30f);
            if (GameCore.Instance == null || NetworkClient.localPlayer == null)
            {
                // The server refused the join (its match had started, or it was full).
                State("refused");
                log.WriteLine("done (not admitted)");
                Application.Quit();
                yield break;
            }
            yield return new WaitForSecondsRealtime(2f);
            GameCore.Instance.Cmd_SetReady(true);
            Shot("lobby");
        }
        State("lobby");

        yield return Until(() => SceneManager.GetActiveScene().name != "Main_Menu", 60f);
        yield return Until(() => WorldStateManager.Instance != null && WorldStateManager.Instance.Map != null && NetworkClient.ready, 30f);
        yield return new WaitForSecondsRealtime(2f);
        State("placing");
        Shot("placing");
        // Each side places its HQ on the clearing it was given at the start, through the same command the placement click sends.
        Unity.Mathematics.int2[] sites = WorldStateManager.Instance.Map.HqSites;
        yield return Until(() => NetworkClient.localPlayer.GetComponent<ClientPlayer>().OwnStartOptions.Length > 0, 30f);
        int own = NetworkClient.localPlayer.GetComponent<ClientPlayer>().OwnStartOptions[0];
        Unity.Mathematics.int2 site = own >= 0 && own < sites.Length ? sites[own] : default;
        WorldStateManager.Instance.TryAddBuilding(site, BuildingType.Base, 0f);
        yield return Until(() => GameCore.Instance.CurrentState == GameState.Playing, 30f);
        yield return new WaitForSecondsRealtime(2f);
        State("playing");
        Shot("playing");
        var hud = FindAnyObjectByType<WAR2D.UI.HudController>();
        if (role == "client")
        {
            hud.MatchMenu.Open();
            yield return null;
            yield return null;
            Shot("match-menu");
            yield return null;
            hud.MatchMenu.Close();
        }
        // v0.15 reconnect: the client's connection breaks, its seat is kept for it, and it comes back with its ticket.
        if (role == "client")
        {
            yield return new WaitForSecondsRealtime(2f);
            var before = UnitCommander.Instance != null ? UnitCommander.Instance.KnownBuildings : null;
            string hqAt = "";
            if (before != null) foreach (var pair in before.Entries) hqAt += $"{pair.Value.Data.buildingType}@{pair.Value.Data.position} ";
            log.WriteLine($"before drop: camera {(Camera.main != null ? Camera.main.transform.position.ToString() : "none")}, buildings {hqAt}");
            GameManager.Instance.StopClient();
            yield return new WaitForSecondsRealtime(3f);
            log.WriteLine($"rejoin offered: {GameManager.Instance.CanRejoin} ({GameManager.Instance.RejoinSecondsLeft:0} s left)");
            bool started = GameManager.Instance.Rejoin();
            log.WriteLine($"rejoin started: {started}");
            yield return Until(() => NetworkClient.isConnected && NetworkClient.ready && NetworkClient.localPlayer != null && GameCore.Instance != null, 40f);
            yield return new WaitForSecondsRealtime(3f);
            var seen = UnitCommander.Instance != null ? UnitCommander.Instance.KnownBuildings : null;
            log.WriteLine($"rejoin view: camera {(Camera.main != null ? Camera.main.transform.position.ToString() : "none")}, buildings {(seen != null ? seen.Entries.Count : -1)}{AfterBuildings(seen)}, own units {WAR2D.Client.ClientWorld.Instance?.KnownCount}");
            State("rejoined");
            Shot("rejoined");
            ClientPlayer back = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;
            log.WriteLine(back != null && !back.isAway && back.hasPlacedHQ && GameCore.Instance.CurrentState == GameState.Playing ? "REJOIN OK" : "REJOIN FAILED");
            yield return new WaitForSecondsRealtime(3f);
        }
        else
        {
            yield return Until(() => AnyAway(), 25f);
            log.WriteLine("host saw a player go away");
            State("away");
            yield return Until(() => !AnyAway() && GameCore.Instance.ServerPlayers.Count >= 2, 40f);
            log.WriteLine(GameCore.Instance.ServerPlayers.Count >= 2 && !AnyAway() ? "HOST: player came back" : "HOST: player did not come back");
            yield return new WaitForSecondsRealtime(4f);
            GameCore.Instance.Cmd_Surrender();
        }
        if (role == "client") yield return new WaitForSecondsRealtime(1f);
        yield return Until(() => GameCore.Instance == null || GameCore.Instance.CurrentState == GameState.GameOver, 30f);
        yield return new WaitForSecondsRealtime(2f);
        State("end");
        Shot("end");
        log.WriteLine("done");
        Application.Quit();
    }

    private static string AfterBuildings(WAR2D.Net.Replication.ClientBuildings seen)
    {
        string text = "";
        if (seen != null) foreach (var pair in seen.Entries) text += $" {pair.Value.Data.buildingType}@{pair.Value.Data.position}";
        return text;
    }

    private static bool AnyAway()
    {
        foreach (ServerPlayer player in GameCore.Instance.ServerPlayers.Values) if (player.Away) return true;
        return false;
    }

    private IEnumerator Until(System.Func<bool> condition, float seconds)
    {
        float end = Time.realtimeSinceStartup + seconds;
        while (!condition() && Time.realtimeSinceStartup < end) yield return null;
        log.WriteLine($"wait {(condition() ? "ok" : "TIMED OUT")}");
    }

    private void State(string label)
    {
        GameCore core = GameCore.Instance;
        ClientPlayer local = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;
        var hud = FindAnyObjectByType<WAR2D.UI.HudController>();
        VisualElement root = hud != null ? hud.GetComponent<UIDocument>().rootVisualElement : null;
        log.WriteLine($"{label}: scene={SceneManager.GetActiveScene().name} state={core?.CurrentState} local={(local != null)} hasHQ={local?.hasPlacedHQ} " +
                      $"placing={BuildingPlacement.Instance?.IsPlacing} placingType={BuildingPlacement.Instance?.PlacingType} hud={(hud != null)} " +
                      $"prompt={root?.Q("hq-prompt")?.ClassListContains("hud-overlay--visible")} wsm={(WorldStateManager.Instance != null)} map={(WorldStateManager.Instance?.Map != null)} " +
                      $"commander={(UnitCommander.Instance != null)} players={core?.PlayerOrder.Count}");
        foreach (NetworkIdentity id in Resources.FindObjectsOfTypeAll<NetworkIdentity>())
        {
            if (string.IsNullOrEmpty(id.gameObject.scene.name)) continue; // prefab assets
            log.WriteLine($"  identity '{id.name}' active={id.gameObject.activeInHierarchy} scene={id.gameObject.scene.name} sceneId={id.sceneId:X} netId={id.netId} " +
                          $"server={NetworkServer.spawned.ContainsKey(id.netId)} client={NetworkClient.spawned.ContainsKey(id.netId)} observers={(NetworkServer.active ? id.observers.Count : -1)}");
        }
    }

    private static void Click(WAR2D.UI.MenuController menu, string button)
    {
        var b = menu.GetComponent<UIDocument>().rootVisualElement.Q<Button>(button);
        using (NavigationSubmitEvent e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
    }

    private void Shot(string label) => ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{role}-{label}.png"));
}

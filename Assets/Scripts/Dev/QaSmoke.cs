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
        yield return new WaitForSecondsRealtime(1f);
        Shot("menu");
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
            yield return new WaitForSecondsRealtime(2f);
            GameCore.Instance.Cmd_SetReady(true);
            Shot("lobby");
        }
        State("lobby");

        yield return Until(() => SceneManager.GetActiveScene().name != "Main_Menu", 60f);
        for (int i = 0; i < 4; i++)
        {
            yield return new WaitForSecondsRealtime(3f);
            State("match" + i);
            Shot("match" + i);
        }
        log.WriteLine("done");
        Application.Quit();
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

    private void Shot(string label) => ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{role}-{label}.png"));
}

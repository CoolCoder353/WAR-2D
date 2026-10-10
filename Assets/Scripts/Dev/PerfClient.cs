using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using kcp2k;
using Mirror;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Client;

/// <summary>
/// A real player for the performance gate. Started with <c>-perf -perfClient &lt;address&gt;</c> (and
/// <c>-perfOut &lt;dir&gt;</c>), it joins the host over KCP through <see cref="MeteredKcpTransport"/>,
/// keeps its camera on its own army, and from 10 s into the match records the raw UDP bytes it receives
/// each second. When the host ends the match it writes <c>perf.csv</c> (<c>perf.bw.avg</c>,
/// <c>perf.bw.peak1s</c>) and quits. Does nothing without the flags.
/// </summary>
public sealed class PerfClient : MonoBehaviour
{
    private const float WarmupSeconds = 10f, MaxSeconds = 900f;
    private readonly PerfStats stats = new PerfStats();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (!DevApi.PerfFlag || PerfMatch.ArgValue("-perfClient") == null) return;
        var go = new GameObject("PerfClient");
        DontDestroyOnLoad(go);
        go.AddComponent<PerfClient>();
    }

    private IEnumerator Start()
    {
        Application.targetFrameRate = 30; // a headless player needs no more, and leaves the CPU to the host
        yield return new WaitUntil(() => GameManager.Instance != null);
        UseMeteredTransport();
        GameManager.Instance.ConnectToServer(PerfMatch.ArgValue("-perfClient"));

        float started = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => (GameCore.Instance != null && GameCore.Instance.CurrentState == GameState.Playing)
                                         || Time.realtimeSinceStartup - started > 300f);
        yield return new WaitForSecondsRealtime(WarmupSeconds);

        long last = MeteredKcpTransport.ClientBytesReceived;
        float lastTime = Time.realtimeSinceStartup;
        while (NetworkClient.isConnected && Time.realtimeSinceStartup - started < MaxSeconds)
        {
            yield return new WaitForSecondsRealtime(1f);
            long now = MeteredKcpTransport.ClientBytesReceived;
            float t = Time.realtimeSinceStartup;
            double rate = (now - last) / Math.Max(1e-3, t - lastTime);
            stats.Add("perf.bw.avg", rate);
            stats.Add("perf.bw.peak1s", rate);
            last = now;
            lastTime = t;
            FollowArmy();
        }

        string dir = PerfMatch.ArgValue("-perfOut") ?? Path.Combine(Application.persistentDataPath, "PerfResults");
        string path = Path.Combine(dir, "perf.csv");
        stats.WriteCsv(path, new Dictionary<string, (string, double, bool)>
        {
            ["perf.bw.avg"] = ("mean", 262144.0, true),
            ["perf.bw.peak1s"] = ("max", 786432.0, true),
        });
        Debug.Log($"[PerfClient] wrote {path}");
        Application.Quit();
    }

    /// <summary>
    /// Replaces the scene's KCP transport with the metered one (same tuning) before connecting. The scene's
    /// transport may be a multiplexer (KCP and WebSocket); a perf client only needs its KCP half.
    /// </summary>
    private static void UseMeteredTransport()
    {
        // The manager's transport: Transport.active (and NetworkManager.singleton) are only set once a
        // client or server starts.
        Transport scene = GameManager.Instance.transport;
        KcpTransport kcp = scene as KcpTransport;
        if (kcp == null && scene is MultiplexTransport multiplex)
            foreach (Transport t in multiplex.transports) if (t is KcpTransport k) { kcp = k; break; }
        if (kcp == null)
        {
            Debug.LogError("[PerfClient] the scene has no KCP transport to meter; bandwidth will read 0");
            return;
        }
        if (kcp is MeteredKcpTransport) return;
        var go = new GameObject("MeteredKcpTransport");
        go.SetActive(false); // Awake (which reads the tuning) waits for the copy
        DontDestroyOnLoad(go);
        MeteredKcpTransport metered = go.AddComponent<MeteredKcpTransport>();
        metered.CopyFrom(kcp);
        go.SetActive(true);
        kcp.enabled = false;
        GameManager.Instance.transport = metered;
        Transport.active = metered;
    }

    /// <summary>Points the camera at the centre of the local player's units, as a player watching their army would.</summary>
    private static void FollowArmy()
    {
        ClientWorld world = ClientWorld.Instance;
        Camera cam = Camera.main;
        if (world == null || cam == null || NetworkClient.localPlayer == null) return;
        int me = BuildingData.UIntToInt(NetworkClient.localPlayer.netId);
        var store = world.Store;
        float2 sum = 0f;
        int n = 0;
        for (int k = 0; k < store.Count; k++)
        {
            int index = store.IndexAt(k);
            if (store.OwnerOf(index) != me) continue;
            sum += store.Predicted[k];
            n++;
        }
        if (n == 0) return;
        float2 centre = sum / n;
        cam.orthographicSize = 34f;
        cam.transform.position = new Vector3(centre.x, centre.y, cam.transform.position.z);
    }
}

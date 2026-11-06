using WAR2D.Dev.Logging;
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

    /// <summary>
    /// True with <c>-perfView</c> (the 10,000-unit benchmark, <c>tools/perf-10k.sh</c>): this client renders, at 1920×1080
    /// fullscreen with the default settings, VSync off and no frame cap, frames every unit it knows, and records its frames
    /// (fps, 1 % low, CPU main and render thread, GPU), its process CPU and memory, and the units it knows and has on screen.
    /// </summary>
    private static readonly bool View = Array.IndexOf(Environment.GetCommandLineArgs(), "-perfView") >= 0;

    /// <summary>The client frame budget: 120 fps on average and in the slowest 1 % of frames.</summary>
    public const double FpsBudget = 120.0;

    private bool measuring;
    private float measureStart;
    private readonly List<double> frameMs = new List<double>();
    private readonly List<float> frameAt = new List<float>();
    private readonly List<int> frameGc = new List<int>();
    private readonly FrameTiming[] timing = new FrameTiming[1];
    private readonly System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();

    private IEnumerator Start()
    {
        Application.targetFrameRate = 30; // a headless player needs no more, and leaves the CPU to the host
        yield return new WaitUntil(() => GameManager.Instance != null);
        if (View)
        {
            // After SettingsStore.ApplyOnStart: the defaults (1920×1080 fullscreen), whatever this machine saved, uncapped. Fullscreen
            // matters: a window behind another one stalls in DXGI.WaitOnSwapChain for ~500 ms at a time (profiled 2026-11-06).
            SettingsStore.Current = new GameSettings { VSync = false, FpsCap = 0, EdgeScroll = false };
            SettingsStore.Apply(SettingsStore.Current);
        }
        UseMeteredTransport();
        GameManager.Instance.ConnectToServer(PerfMatch.ArgValue("-perfClient"));

        float started = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => (GameCore.Instance != null && GameCore.Instance.CurrentState == GameState.Playing)
                                         || Time.realtimeSinceStartup - started > 300f);
        yield return new WaitForSecondsRealtime(WarmupSeconds);

        long last = MeteredKcpTransport.ClientBytesReceived;
        float lastTime = Time.realtimeSinceStartup;
        TimeSpan lastCpu = self.TotalProcessorTime;
        measuring = View;
        measureStart = Time.realtimeSinceStartup;
        if (View) FitAll();
        while (NetworkClient.isConnected && Time.realtimeSinceStartup - started < MaxSeconds)
        {
            yield return new WaitForSecondsRealtime(1f);
            long now = MeteredKcpTransport.ClientBytesReceived;
            float t = Time.realtimeSinceStartup;
            double rate = (now - last) / Math.Max(1e-3, t - lastTime);
            stats.Add("perf.bw.avg", rate);
            stats.Add("perf.bw.peak1s", rate);
            if (View && measuring)
            {
                self.Refresh();
                TimeSpan cpu = self.TotalProcessorTime;
                stats.Add("perf.client.cpu", (cpu - lastCpu).TotalMilliseconds / Math.Max(1.0, (t - lastTime) * 1000.0) * 100.0); // % of one core
                lastCpu = cpu;
                stats.Add("perf.client.memMB", PerfMatch.UnityMemoryMb());
                stats.Add("perf.client.gcMB", GC.GetTotalMemory(false) / 1048576.0);
                stats.Add("perf.client.unityMB", UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576.0);
                CountUnits(out int known, out int onScreen);
                stats.Add("perf.client.unitsKnown", known);
                stats.Add("perf.client.unitsOnScreen", onScreen);
                FitAll();
            }
            last = now;
            lastTime = t;
            if (!View) FollowArmy();
        }
        measuring = false;

        string dir = PerfMatch.ArgValue("-perfOut") ?? Path.Combine(Application.persistentDataPath, "PerfResults");
        string path = Path.Combine(dir, "perf.csv");
        var budgets = new Dictionary<string, (string, double, bool)>
        {
            ["perf.bw.avg"] = ("mean", 262144.0, true),
            ["perf.bw.peak1s"] = ("max", 786432.0, true),
        };
        if (View)
        {
            stats.Add("perf.client.low1", OnePercentLow(frameMs));
            // Every frame (seconds into the measurement, milliseconds, garbage collections so far), to find what the slow ones line up with.
            var frames = new System.Text.StringBuilder("t,ms,gc\n");
            for (int i = 0; i < frameMs.Count; i++) frames.Append(frameAt[i].ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(frameMs[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(frameGc[i]).Append('\n');
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "frames.csv"), frames.ToString());
            budgets["perf.client.fps"] = ("mean", FpsBudget, false);
            budgets["perf.client.low1"] = ("mean", FpsBudget, false);
        }
        stats.WriteCsv(path, budgets);
        WarLog.Info(LogCategory.Dev, $"wrote {path}");
        Application.Quit();
    }

    /// <summary>The view's frames, from the warm-up's end until the host ends the match (not the menu that follows).</summary>
    private void Update()
    {
        if (!measuring) return;
        if (!NetworkClient.isConnected) { measuring = false; return; }
        double ms = Time.unscaledDeltaTime * 1000.0;
        frameMs.Add(ms);
        frameAt.Add(Time.realtimeSinceStartup - measureStart);
        frameGc.Add(GC.CollectionCount(0));
        stats.Add("perf.client.fps", 1000.0 / Math.Max(1e-3, ms));
        stats.Add("perf.client.frame.ms", ms);
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, timing) < 1) return;
        stats.Add("perf.client.cpu.main.ms", timing[0].cpuMainThreadFrameTime);
        stats.Add("perf.client.cpu.render.ms", timing[0].cpuRenderThreadFrameTime);
        stats.Add("perf.client.cpu.presentWait.ms", timing[0].cpuMainThreadPresentWaitTime);
        if (timing[0].gpuFrameTime > 0 && timing[0].gpuFrameTime < 1000) stats.Add("perf.client.gpu.ms", timing[0].gpuFrameTime); // drops the odd unset reading
        if (ProfilePath != null) ProfileFrames();
    }

    /// <summary><c>-perfProfile &lt;file.raw&gt;</c> (Development builds): the Unity Profiler's data for 2,000 frames, from 20 s into the measurement.</summary>
    private static readonly string ProfilePath = PerfMatch.ArgValue("-perfProfile");
    private int profiledFrames = -1;

    private void ProfileFrames()
    {
        if (profiledFrames < 0 && frameMs.Count > 0 && Time.realtimeSinceStartup - measureStart >= 20f)
        {
            UnityEngine.Profiling.Profiler.logFile = ProfilePath;
            UnityEngine.Profiling.Profiler.enableBinaryLog = true;
            UnityEngine.Profiling.Profiler.enabled = true;
            profiledFrames = 0;
        }
        else if (profiledFrames >= 0 && ++profiledFrames == 2000)
        {
            UnityEngine.Profiling.Profiler.enabled = false;
            UnityEngine.Profiling.Profiler.enableBinaryLog = false;
        }
    }

    /// <summary>The "1 % low": the frame rate over the slowest 1 % of frames (their mean frame time).</summary>
    public static double OnePercentLow(List<double> frameMs)
    {
        if (frameMs.Count == 0) return double.NaN;
        var sorted = new List<double>(frameMs);
        sorted.Sort();
        int n = Math.Max(1, sorted.Count / 100);
        double sum = 0;
        for (int i = sorted.Count - n; i < sorted.Count; i++) sum += sorted[i];
        return 1000.0 * n / sum;
    }

    /// <summary>Known units, and those inside the camera's view.</summary>
    private static void CountUnits(out int known, out int onScreen)
    {
        known = onScreen = 0;
        ClientWorld world = ClientWorld.Instance;
        Camera cam = Camera.main;
        if (world == null || cam == null) return;
        Unity.Collections.NativeArray<float2> p = world.Positions;
        known = p.Length;
        float2 centre = new float2(cam.transform.position.x, cam.transform.position.y);
        float2 half = new float2(cam.orthographicSize * cam.aspect, cam.orthographicSize);
        for (int k = 0; k < p.Length; k++) if (math.all(math.abs(p[k] - centre) <= half)) onScreen++;
    }

    /// <summary>
    /// Centres the player's camera on every unit the client knows and zooms it out in wheel steps until they all show, as a
    /// player would (never in, at most to a player's limit).
    /// </summary>
    private static void FitAll()
    {
        ClientWorld world = ClientWorld.Instance;
        Camera cam = Camera.main;
        var controller = FindFirstObjectByType<Character.Character_Controler>();
        if (world == null || cam == null || controller == null || world.Positions.Length == 0) return;
        Unity.Collections.NativeArray<float2> p = world.Positions;
        float2 lo = p[0], hi = p[0];
        for (int k = 1; k < p.Length; k++) { lo = math.min(lo, p[k]); hi = math.max(hi, p[k]); }
        float2 half = (hi - lo) * 0.5f + 2f, centre = (lo + hi) * 0.5f;
        controller.CentreOn(new Vector2(centre.x, centre.y));
        controller.ZoomOutToShow(math.max(half.y, half.x / cam.aspect));
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
            WarLog.Error(LogCategory.Dev, "the scene has no KCP transport to meter; bandwidth will read 0");
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
        GameManager.Instance.SetDirectTransport(metered); // joins pick the direct transport since v0.9
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

using System.Collections;
using kcp2k;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The <c>combined</c> bench, the "Combined host" row: one windowed player process hosts and plays.
    /// The server ticks at 20 Hz on the wall clock (simulation, fog slice, terrain changes with their
    /// flow-field rebuilds, route maintenance, interest sets and encoding for all eight clients, seven
    /// of which are raw KCP clients on loopback), and every frame the local client (player 0) draws
    /// its own army and the enemies its team can see with the instanced renderer, interpolated
    /// between ticks, at 1920x1080 with vsync off.
    ///
    /// <para><b>Modes.</b> Sync (default): a tick's jobs complete inside the tick. Async
    /// (<c>-async</c>): the tick settles the previous tick's jobs, reads the settled world (fog,
    /// encoding, the client's snapshot), then schedules the next simulation tick and returns, so the
    /// simulation's jobs run on the workers while the host renders. Clients see one extra tick of
    /// latency.</para>
    ///
    /// <para><b>Metrics.</b> <c>combined.tick.main</c>: main-thread ms of a server tick (in sync mode
    /// this is also the tick's wall time, <c>combined.tick.wall</c>); <c>combined.tick.wait</c> (async
    /// only): how long the boundary waited for the previous tick's jobs, which stays near zero while
    /// they fit in the tick period; <c>combined.fps</c> and <c>combined.frame</c> (frame ms; its p99
    /// is the plan's <c>combined.frame.p99</c>); <c>combined.client.ms</c>: the local client's
    /// main-thread work a frame; <c>combined.bw.avg</c> / <c>combined.bw.peak1s</c>: payload KB/s per
    /// client; <c>combined.late</c>: ticks that started more than one period late.</para>
    ///
    /// <para><b>Caveat.</b> The seven raw clients' own KCP work runs in this process too, so the host
    /// pays a little CPU that a real host would not.</para>
    /// </summary>
    public static class CombinedBench
    {
        /// <summary>Warm-up ticks before the measured window: every fog team refreshed, orders out.</summary>
        private const int WarmupTicks = 20;

        /// <summary>The plan's measured window: 60 seconds at 20 Hz.</summary>
        private const int PlanTicks = 1200;

        /// <summary>The camera's half-height in tiles: the render row's 110-tile view.</summary>
        private const float CameraHalfHeight = 55f;

        private const double TickMs = SpikeSimRules.TickSeconds * 1000.0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => SpikeDriver.Benches["combined"] = Run;

        public static IEnumerator Run(SpikeArgs a)
        {
            Camera camera = SpikeDriver.Instance != null ? SpikeDriver.Instance.BenchCamera : null;
            if (camera == null)
            {
                Debug.LogError("[Combined] the spike scene has no bench camera; nothing can be drawn");
                yield break;
            }
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.04f, 0.04f, 0.05f);

            int ticks = a.Ticks == SpikeArgs.Defaults.Ticks ? PlanTicks : a.Ticks;
            int remote = math.clamp(a.Clients, 1, ReplicationMatch.Players) - 1;

            var transportObject = new GameObject("[Spike] Combined transport");
            var transport = transportObject.AddComponent<SpikeKcpTransport>();
            transport.port = LoopbackSoak.Port;
            transport.OnServerConnected = id => { };
            transport.OnServerConnectedWithAddress = (id, address) => { };
            transport.OnServerDataReceived = (id, message, channel) => { };
            transport.OnServerDataSent = (id, message, channel) => { };
            transport.OnServerDisconnected = id => Debug.LogWarning($"[Combined] server connection {id} disconnected");
            transport.OnServerError = (id, error, reason) => Debug.LogWarning($"[Combined] server connection {id} error {error}: {reason}");
            transport.OnServerTransportException = (id, exception) => Debug.LogWarning($"[Combined] transport exception {exception.Message}");
            transport.OnClientConnected = () => { };
            transport.OnClientDataReceived = (message, channel) => { };
            transport.OnClientDataSent = (message, channel) => { };
            transport.OnClientDisconnected = () => { };
            transport.OnClientError = (error, reason) => { };
            var raw = new LoopbackSoak.RawClient[remote];

            SpikeMap backdropMap = default;
            RenderBench.Backdrop backdrop = null;
            Texture2D barAtlas = null;
            InstancedUnitRenderer renderer = null;
            ReplicationMatch match = null;
            ClientView view = null;
            try
            {
                transport.ServerStart();
                for (int i = 0; i < remote; i++)
                {
                    var client = new LoopbackSoak.RawClient();
                    int index = i;
                    client.Peer = new KcpClient(() => { },
                        (message, channel) => LoopbackSoak.OnData(client, message, channel),
                        () => Debug.LogWarning($"[Combined] client {index} disconnected"),
                        (error, reason) => Debug.LogWarning($"[Combined] client {index} error {error}: {reason}"),
                        transport.Settings);
                    client.Peer.Connect("127.0.0.1", LoopbackSoak.Port);
                    raw[i] = client;
                }
                double deadline = Time.realtimeSinceStartupAsDouble + 10.0;
                while (transport.Server.connections.Count < remote && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    transport.ServerEarlyUpdate();
                    transport.ServerLateUpdate();
                    for (int i = 0; i < remote; i++) raw[i].Peer.Tick();
                    yield return null;
                }
                for (int i = 0; i < remote; i++)
                {
                    raw[i].ConnectionId = LoopbackSoak.FindConnection(transport, raw[i]);
                    if (raw[i].ConnectionId < 0)
                    {
                        Debug.LogError("[Combined] not every raw client connected; the bench cannot run");
                        yield break;
                    }
                }

                match = ReplicationMatch.Create(a, ticks + WarmupTicks, Allocator.Persistent);
                backdropMap = match.Map;
                backdrop = new RenderBench.Backdrop(backdropMap);
                Texture2D atlas = Resources.Load<Texture2D>("tank");
                atlas.filterMode = FilterMode.Point;
                barAtlas = RenderBench.BuildBarAtlas(RenderBench.BarFrames);
                int capacity = match.Sim.Data.Capacity;
                renderer = new InstancedUnitRenderer(capacity, capacity, atlas, barAtlas, RenderBench.BarFrames, RenderBench.UnitSize);
                view = new ClientView(capacity, match.Sim.Data.IdCapacity);

                Debug.Log($"[Combined] {match.Scenario.UnitCount} units, {match.Clients} clients ({remote} remote), " +
                          $"{match.Teams} teams, map {match.Map.Width}^2, vision {match.UnitVision}, " +
                          $"{(a.Async ? "async" : "sync")}, {ticks} measured ticks after {WarmupTicks} warm-up");
                yield return Measure(a, match, transport, raw, renderer, backdrop, view, camera, ticks);
            }
            finally
            {
                transport.ServerStop();
                for (int i = 0; i < remote; i++)
                {
                    if (raw[i] == null) continue;
                    if (raw[i].Peer.connected) raw[i].Peer.Disconnect();
                    raw[i].Peer.TickOutgoing();
                }
                Object.Destroy(transportObject);
                match?.Sim.Complete();
                view?.Dispose();
                renderer?.Dispose();
                backdrop?.Dispose();
                if (barAtlas != null) Object.Destroy(barAtlas);
                match?.Dispose();
            }
        }

        private static IEnumerator Measure(SpikeArgs a, ReplicationMatch match, SpikeKcpTransport transport,
            LoopbackSoak.RawClient[] raw, InstancedUnitRenderer renderer, RenderBench.Backdrop backdrop,
            ClientView view, Camera camera, int ticks)
        {
            int clients = match.Clients;
            var tickMain = new SpikeStats();
            var tickWait = new SpikeStats();
            var frameMs = new SpikeStats();
            var fps = new SpikeStats();
            var clientMs = new SpikeStats();
            var transportMs = new SpikeStats();
            var perTick = new long[clients, ticks];
            int late = 0;
            int total = WarmupTicks + ticks;
            var clock = Stopwatch.StartNew();
            var sw = new Stopwatch();
            double nextTick = 0;
            double lastFrame = clock.Elapsed.TotalMilliseconds;
            float tickStartedAt = 0f;

            int tick = 0;
            while (tick < total)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                if (now >= nextTick)
                {
                    if (now - nextTick > TickMs) late += tick >= WarmupTicks ? 1 : 0;
                    nextTick = math.max(nextTick + TickMs, now - TickMs); // never queue up more than one tick

                    sw.Restart();
                    double wait = 0;
                    if (a.Async) wait = match.SettleAndPrepare(tick);
                    else match.Step(tick);
                    if (tick % SpikeScenario.TicksPerSecond == 0)
                    {
                        match.UpdateViews();
                        float2 centre = match.DensestBlock(0);
                        camera.transform.position = new Vector3(centre.x, centre.y, -10f);
                        camera.orthographicSize = CameraHalfHeight;
                    }
                    match.Maintain();
                    match.BuildInterests();
                    match.EncodeAll(tick);
                    for (int client = 0; client < clients; client++)
                    {
                        (int reliable, int unreliable) = client == 0 ? match.Sizes(0) : match.Output(client);
                        if (tick >= WarmupTicks) perTick[client, tick - WarmupTicks] = reliable + unreliable;
                        if (client == 0) continue; // the local client reads the host's world in process
                        LoopbackSoak.SendStream(transport, raw[client - 1].ConnectionId, match.Reliable, KcpChannel.Reliable, match.Encoder.ReliableMessageSizes);
                        LoopbackSoak.SendStream(transport, raw[client - 1].ConnectionId, match.Unreliable, KcpChannel.Unreliable, match.Encoder.UnreliableMessageSizes);
                    }
                    view.Snapshot(match, 0);
                    if (a.Async) match.ScheduleSim();
                    sw.Stop();
                    tickStartedAt = Time.realtimeSinceStartup;
                    if (tick >= WarmupTicks)
                    {
                        tickMain.Add(sw.Elapsed.TotalMilliseconds);
                        if (a.Async) tickWait.Add(wait);
                    }
                    tick++;
                }

                // The transport's own update, as Mirror runs it every frame.
                sw.Restart();
                transport.ServerEarlyUpdate();
                transport.ServerLateUpdate();
                sw.Stop();
                if (tick > WarmupTicks) transportMs.Add(sw.Elapsed.TotalMilliseconds);
                for (int i = 0; i < raw.Length; i++) raw[i].Peer.Tick();

                // The local client's frame: interpolate between the last two ticks, pack, draw.
                sw.Restart();
                backdrop.Draw();
                float t = math.saturate((Time.realtimeSinceStartup - tickStartedAt) / SpikeSimRules.TickSeconds);
                view.Draw(renderer, t);
                sw.Stop();
                if (tick > WarmupTicks) clientMs.Add(sw.Elapsed.TotalMilliseconds);

                yield return null;

                double frameNow = clock.Elapsed.TotalMilliseconds;
                double ms = frameNow - lastFrame;
                lastFrame = frameNow;
                if (tick > WarmupTicks && ms > 0)
                {
                    frameMs.Add(ms);
                    fps.Add(1000.0 / ms);
                }
            }

            var avg = new SpikeStats();
            var peak = new SpikeStats();
            double seconds = ticks / (double)SpikeScenario.TicksPerSecond;
            for (int client = 0; client < clients; client++)
            {
                long sum = 0, worst = 0;
                for (int i = 0; i < ticks; i++) sum += perTick[client, i];
                for (int start = 0; start + SpikeScenario.TicksPerSecond <= ticks; start += SpikeScenario.TicksPerSecond)
                {
                    long window = 0;
                    for (int k = 0; k < SpikeScenario.TicksPerSecond; k++) window += perTick[client, start + k];
                    worst = math.max(worst, window);
                }
                avg.Add(sum / 1024.0 / seconds);
                peak.Add(worst / 1024.0);
            }

            if (!a.Async) SpikeResults.Write(a, "combined.tick.wall", "ms", tickMain);
            SpikeResults.Write(a, "combined.tick.main", "ms", tickMain);
            if (a.Async) SpikeResults.Write(a, "combined.tick.wait", "ms", tickWait);
            SpikeResults.Write(a, "combined.fps", "fps", fps);
            SpikeResults.Write(a, "combined.frame", "ms", frameMs);
            SpikeResults.Write(a, "combined.client.ms", "ms", clientMs);
            SpikeResults.Write(a, "combined.transport.ms", "ms", transportMs);
            SpikeResults.Write(a, "combined.bw.avg", "KB/s", avg);
            SpikeResults.Write(a, "combined.bw.peak1s", "KB/s", peak);
            var lateStats = new SpikeStats();
            lateStats.Add(late);
            SpikeResults.Write(a, "combined.late", "ticks", lateStats);
            var drawn = new SpikeStats();
            drawn.Add(view.LastCount);
            SpikeResults.Write(a, "combined.drawn", "units", drawn);

            Debug.Log($"[Combined] {(a.Async ? "async" : "sync")}: tick main {tickMain.Percentile(50):F2} ms p50/" +
                      $"{tickMain.Percentile(95):F2} p95" +
                      (a.Async ? $", boundary wait {tickWait.Percentile(95):F2} ms p95" : "") +
                      $"; {fps.Mean:F0} fps mean, frame p99 {frameMs.Percentile(99):F1} ms, client {clientMs.Percentile(95):F2} ms p95; " +
                      $"{late} late ticks; bandwidth {avg.Mean:F1} KB/s a client (peak 1 s {peak.Max:F1}); " +
                      $"{view.LastCount} units drawn");
        }

        /// <summary>
        /// What the local client draws: its team's allowed units from the last two ticks, by id, so a
        /// frame can interpolate between them the way a client does between server updates.
        /// </summary>
        private sealed class ClientView : System.IDisposable
        {
            private NativeArray<float2> previous, current, lerped, lastById;
            private NativeArray<byte> knownById;
            private NativeArray<float> rotations, health, scale;
            private NativeArray<uint> colors;
            private NativeArray<byte> damaged;
            private NativeArray<UnitInstance> instances, bars;
            private NativeArray<int> barCount;
            private int count;

            public int LastCount => count;

            public ClientView(int capacity, int idCapacity)
            {
                previous = new NativeArray<float2>(capacity, Allocator.Persistent);
                current = new NativeArray<float2>(capacity, Allocator.Persistent);
                lerped = new NativeArray<float2>(capacity, Allocator.Persistent);
                lastById = new NativeArray<float2>(idCapacity, Allocator.Persistent);
                knownById = new NativeArray<byte>(idCapacity, Allocator.Persistent);
                rotations = new NativeArray<float>(capacity, Allocator.Persistent);
                health = new NativeArray<float>(capacity, Allocator.Persistent);
                scale = new NativeArray<float>(capacity, Allocator.Persistent);
                colors = new NativeArray<uint>(capacity, Allocator.Persistent);
                damaged = new NativeArray<byte>(capacity, Allocator.Persistent);
                instances = new NativeArray<UnitInstance>(capacity, Allocator.Persistent);
                bars = new NativeArray<UnitInstance>(capacity, Allocator.Persistent);
                barCount = new NativeArray<int>(1, Allocator.Persistent);
            }

            /// <summary>Copies the client's allowed units out of the settled world (main thread, once a tick).</summary>
            public void Snapshot(ReplicationMatch match, int client)
            {
                var data = match.Sim.Data;
                NativeArray<ulong> allowed = match.Interests.Allowed(client);
                count = 0;
                for (int w = 0; w < allowed.Length; w++)
                {
                    ulong bits = allowed[w];
                    while (bits != 0)
                    {
                        int id = w * 64 + math.tzcnt(bits);
                        bits &= bits - 1;
                        if (id >= data.IdCapacity) continue;
                        int slot = data.IndexOfId[id];
                        if (slot < 0 || slot >= data.Capacity || data.IdOf[slot] != id || count >= current.Length) continue;
                        float2 position = data.Positions[slot];
                        previous[count] = knownById[id] != 0 ? lastById[id] : position;
                        current[count] = position;
                        lastById[id] = position;
                        knownById[id] = 1;
                        float h = math.saturate(data.Health[slot] / 100f);
                        health[count] = h;
                        damaged[count] = h < 1f ? (byte)1 : (byte)0;
                        scale[count] = 1f;
                        colors[count] = InstancedUnitRenderer.Pack(InstancedUnitRenderer.TeamColors[data.Team[slot] % 8]);
                        count++;
                    }
                }
            }

            /// <summary>One frame: interpolate, pack the instances and bars, draw.</summary>
            public void Draw(InstancedUnitRenderer renderer, float t)
            {
                if (count == 0) return;
                JobHandle lerp = new LerpJob { From = previous, To = current, T = t, Out = lerped }
                    .Schedule(count, RenderBench.JobBatch);
                JobHandle pack = new PackUnitsJob
                {
                    Positions = lerped, Rotations = rotations, Colors = colors, Scale = scale, Health = health,
                    Instances = instances,
                }.Schedule(count, RenderBench.JobBatch, lerp);
                JobHandle packBars = new PackHealthBarsJob
                {
                    Positions = lerped.GetSubArray(0, count), Health = health.GetSubArray(0, count),
                    Damaged = damaged.GetSubArray(0, count), Scale = scale.GetSubArray(0, count),
                    BarFrames = RenderBench.BarFrames, UnitHalfSize = RenderBench.UnitSize * 0.5f,
                    Bars = bars, Count = barCount,
                }.Schedule(lerp);
                JobHandle.CompleteAll(ref pack, ref packBars);
                renderer.Draw(instances, count);
                renderer.DrawHealthBars(bars, barCount[0]);
            }

            public void Dispose()
            {
                previous.Dispose(); current.Dispose(); lerped.Dispose(); lastById.Dispose(); knownById.Dispose();
                rotations.Dispose(); health.Dispose(); scale.Dispose(); colors.Dispose(); damaged.Dispose();
                instances.Dispose(); bars.Dispose(); barCount.Dispose();
            }
        }

        [BurstCompile]
        private struct LerpJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> From, To;
            public float T;
            [WriteOnly] public NativeArray<float2> Out;

            public void Execute(int i) => Out[i] = math.lerp(From[i], To[i], T);
        }
    }
}

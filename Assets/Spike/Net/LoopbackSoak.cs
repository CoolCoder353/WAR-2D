using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using kcp2k;
using Mirror;
using Unity.Collections;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The project's KcpTransport with its server reachable, so the soak can read the per-connection
    /// send queues the transport would otherwise keep to itself.
    /// </summary>
    public sealed class SpikeKcpTransport : KcpTransport
    {
        /// <summary>The kcp server the transport drives.</summary>
        public KcpServer Server => server;

        /// <summary>The configuration the transport built from its inspector settings.</summary>
        public KcpConfig Settings => config;
    }

    /// <summary>
    /// The <c>soak</c> bench, Stage B of the replication row: the same encoder streams Stage A counts
    /// are pushed through the project's real KCP transport on loopback, to see whether the transport
    /// carries them and what the host's transport update costs.
    ///
    /// <para><b>How it is wired.</b> A <see cref="SpikeKcpTransport"/> (the project's
    /// <c>KcpTransport</c>, port 7778, KCP's 1,200-byte MTU and 4,096-deep windows) starts a server in
    /// the process, and <c>-clients</c> raw <see cref="KcpClient"/>s connect to it on 127.0.0.1. Raw
    /// clients avoid running eight game clients on one CPU and let the bench count received bytes
    /// directly. Every tick the encoder's output for client <c>i</c> is sent to connection <c>i</c>:
    /// the reliable stream on the reliable channel, the corrections on the unreliable one, each
    /// message prefixed with an eight-byte send timestamp so the client can measure its latency.</para>
    ///
    /// <para><b>Why the transport directly.</b> Mirror's <c>NetworkConnectionToClient.Send</c> for raw
    /// bytes is internal to the Mirror assembly, so the soak calls the same method that call ends in -
    /// <c>KcpTransport.ServerSend(connectionId, segment, channel)</c> - with the connection ids the
    /// transport's server knows. Mirror's batching (the eight-byte per-batch timestamp, the MTU-sized
    /// batches) is what Stage A accounts for; here the transport's own segmenting and windowing is
    /// what is under test.</para>
    ///
    /// <para><b>Metrics.</b> <c>soak.latency</c> (the p95 server-to-client latency per client),
    /// <c>soak.sendqueue</c> (the maximum <c>SendQueueCount + SendBufferCount</c> across peers each
    /// tick; a queue that keeps growing means the transport cannot carry the load),
    /// <c>soak.server.ms</c> (transport update time a tick), <c>soak.kbps</c> (received KB/s a client)
    /// and <c>soak.sent</c> (payload KB/s a client, the Stage A number for the same streams).</para>
    /// </summary>
    public static class LoopbackSoak
    {
        /// <summary>The project's KCP port: the game's own transport setting.</summary>
        public const ushort Port = 7778;

        /// <summary>KCP's default MTU, the project's transport setting.</summary>
        public const int Mtu = 1200;

        /// <summary>KCP window depth, the project's transport setting.</summary>
        public const uint Window = 4096;

        /// <summary>Ticks the soak runs when <c>-ticks</c> is not given: 30 seconds at 20 Hz.</summary>
        private const int DefaultTicks = 600;

        /// <summary>Seconds the server waits for the raw clients to finish their handshake.</summary>
        private const double ConnectTimeout = 10.0;

        /// <summary>Ticks in the window the send-queue drift is measured over.</summary>
        private const int DriftWindow = 100;

        private static readonly byte[] Header = new byte[8];
        private static byte[] sendBuffer = new byte[SpikeWire.ReliableMaxMessageSize + Header.Length];

        /// <summary>One raw client: its KCP peer, the bytes it received and its latency samples.</summary>
        internal sealed class RawClient
        {
            public KcpClient Peer;
            public int ConnectionId = -1;
            public long Bytes;
            public long ReliableBytes;
            public long UnreliableBytes;
            public long Messages;
            public long Dropped;
            public readonly SpikeStats Latency = new SpikeStats();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => SpikeDriver.Benches["soak"] = Run;

        public static IEnumerator Run(SpikeArgs a)
        {
            int clients = Mathf.Clamp(a.Clients, 1, 16);
            int ticks = a.Ticks == SpikeArgs.Defaults.Ticks ? DefaultTicks : a.Ticks;

            // The transport: the project's own KcpTransport class, its project settings, driven by hand
            // so the soak can see the server the class keeps protected.
            var transportObject = new GameObject("[Spike] Soak transport");
            var transport = transportObject.AddComponent<SpikeKcpTransport>();
            transport.port = Port;
            // KcpTransport's own callbacks invoke Mirror's transports' Action fields, which only
            // NetworkServer would fill in. The soak subscribes so an error is logged rather than
            // thrown from inside KCP.
            transport.OnServerConnected = id => { };
            transport.OnServerConnectedWithAddress = (id, address) => { };
            transport.OnServerDataReceived = (id, message, channel) => { };
            transport.OnServerDataSent = (id, message, channel) => { };
            transport.OnServerDisconnected = id => Debug.LogWarning($"[Soak] server connection {id} disconnected");
            transport.OnServerError = (id, error, reason) =>
                Debug.LogWarning($"[Soak] server connection {id} error {error}: {reason}");
            transport.OnServerTransportException = (id, exception) =>
                Debug.LogWarning($"[Soak] server connection {id} transport exception {exception.Message}");
            transport.OnClientConnected = () => { };
            transport.OnClientDataReceived = (message, channel) => { };
            transport.OnClientDataSent = (message, channel) => { };
            transport.OnClientDisconnected = () => { };
            transport.OnClientError = (error, reason) => Debug.LogWarning($"[Soak] transport client error {error}: {reason}");
            KcpConfig config = transport.Settings; // Awake built it from the transport's settings (MTU 1,200, windows 4,096)
            var raw = new RawClient[clients];
            try
            {
                transport.ServerStart();
                for (int i = 0; i < clients; i++)
                {
                    var client = new RawClient();
                    int index = i;
                    client.Peer = new KcpClient(
                        () => { },
                        (message, channel) => OnData(client, message, channel),
                        () => Debug.LogWarning($"[Soak] client {index} disconnected"),
                        (error, reason) => Debug.LogWarning($"[Soak] client {index} error {error}: {reason}"),
                        config);
                    client.Peer.Connect("127.0.0.1", Port);
                    raw[i] = client;
                }

                double deadline = Time.realtimeSinceStartupAsDouble + ConnectTimeout;
                while (transport.Server.connections.Count < clients && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    transport.ServerEarlyUpdate();
                    transport.ServerLateUpdate();
                    for (int i = 0; i < clients; i++) raw[i].Peer.Tick();
                    yield return null;
                }
                for (int i = 0; i < clients; i++) raw[i].ConnectionId = FindConnection(transport, raw[i]);
                int connected = 0;
                for (int i = 0; i < clients; i++) if (raw[i].ConnectionId >= 0) connected++;
                Debug.Log($"[Soak] {connected} of {clients} raw clients connected on 127.0.0.1:{Port} " +
                          $"(server connections {transport.Server.connections.Count})");
                if (connected < clients)
                {
                    Debug.LogError("[Soak] not every raw client connected; the soak cannot run");
                    yield break;
                }

                using var match = ReplicationMatch.Create(a, ticks, Allocator.Persistent);
                Debug.Log($"[Soak] {match.Scenario.UnitCount} units, {match.Clients} clients, {match.Teams} teams, " +
                          $"map {match.Map.Width}^2, vision {match.UnitVision}, {ticks} ticks");
                yield return null;

                // A nested coroutine, so the drain at its end can let KCP's interval elapse.
                yield return Measure(match, transport, raw, clients, ticks);
            }
            finally
            {
                transport.ServerStop();
                for (int i = 0; i < clients; i++)
                {
                    if (raw[i] == null) continue;
                    if (raw[i].Peer.connected) raw[i].Peer.Disconnect();
                    raw[i].Peer.TickOutgoing();
                }
                UnityEngine.Object.Destroy(transportObject);
            }
        }

        /// <summary>
        /// The measured loop: the match's tick, the encoder for every client, the send of both streams
        /// to that client's connection, the transport's own update and the raw clients' ticks.
        /// </summary>
        private static IEnumerator Measure(ReplicationMatch match, SpikeKcpTransport transport, RawClient[] raw,
            int clients, int ticks)
        {
            var latency = new SpikeStats();
            var serverMs = new SpikeStats();
            var queue = new SpikeStats();
            var sent = new SpikeStats();
            var received = new SpikeStats();
            var sendMs = new SpikeStats();
            var sw = Stopwatch.StartNew();
            long sentBytes = 0, sentReliable = 0, sentUnreliable = 0, receivedBytes = 0, early = 0, late = 0;
            long receivedReliable = 0, receivedUnreliable = 0;
            int peakQueue = 0;

            var period = Stopwatch.StartNew();
            for (int tick = 0; tick < ticks; tick++)
            {
                // Real time: KCP flushes and retransmits on its own wall-clock interval, so the streams
                // must arrive at 20 Hz, not as fast as the CPU can encode them.
                period.Restart();
                match.Step(tick);
                if (tick % SpikeScenario.TicksPerSecond == 0) match.UpdateViews();
                match.Maintain();
                match.BuildInterests();

                sw.Restart();
                match.EncodeAll(tick);
                for (int client = 0; client < clients; client++)
                {
                    match.Output(client);
                    int connectionId = raw[client].ConnectionId;
                    long reliable = SendStream(transport, connectionId, match.Reliable, KcpChannel.Reliable, match.Encoder.ReliableMessageSizes);
                    long unreliable = SendStream(transport, connectionId, match.Unreliable, KcpChannel.Unreliable, match.Encoder.UnreliableMessageSizes);
                    sentBytes += reliable + unreliable;
                    sentReliable += reliable;
                    sentUnreliable += unreliable;
                }
                sw.Stop();
                sendMs.Add(sw.Elapsed.TotalMilliseconds);

                // The transport's own tick: read the sockets, then flush whatever the segments allow.
                sw.Restart();
                transport.ServerEarlyUpdate();
                transport.ServerLateUpdate();
                for (int client = 0; client < clients; client++) raw[client].Peer.Tick();
                sw.Stop();
                serverMs.Add(sw.Elapsed.TotalMilliseconds);

                // The rest of the tick period: the transport keeps updating every frame, as Mirror's
                // NetworkServer would. A tick that took longer than the period starts late.
                while (period.Elapsed.TotalMilliseconds < SpikeSimRules.TickSeconds * 1000.0)
                {
                    yield return null;
                    transport.ServerEarlyUpdate();
                    transport.ServerLateUpdate();
                    for (int client = 0; client < clients; client++) raw[client].Peer.Tick();
                }

                int window = 0;
                foreach (KcpServerConnection connection in transport.Server.connections.Values)
                    window = Math.Max(window, connection.SendQueueCount + connection.SendBufferCount);
                peakQueue = Math.Max(peakQueue, window);
                queue.Add(window);
                if (tick < DriftWindow) early += window;
                if (tick >= ticks - DriftWindow) late += window;
            }

            // Drain: no more sends, just tick the transport and the clients until the queues empty, so
            // the bytes received at the end cover everything that was sent. A seal below 100 % after
            // the drain means the unreliable channel dropped messages (or something never arrived).
            for (int i = 0; i < 20; i++)
            {
                transport.ServerEarlyUpdate();
                transport.ServerLateUpdate();
                for (int client = 0; client < clients; client++) raw[client].Peer.Tick();
                // KCP flushes on its own 10 ms interval, so the drain has to let wall time pass.
                yield return new WaitForSeconds(0.02f);
            }

            // The last second's share of the traffic is what the numbers describe, so the wire rate
            // covers the steady state rather than the handshake.
            for (int client = 0; client < clients; client++)
            {
                receivedBytes += raw[client].Bytes;
                receivedReliable += raw[client].ReliableBytes;
                receivedUnreliable += raw[client].UnreliableBytes;
                latency.Add(raw[client].Latency.Percentile(95));
                received.Add(raw[client].Bytes / 1024.0 / (ticks / (double)SpikeScenario.TicksPerSecond));
            }
            sent.Add(sentBytes / 1024.0 / (ticks / (double)SpikeScenario.TicksPerSecond) / clients);

            SpikeResults.Write(match.Args, "soak.latency", "ms", latency);
            SpikeResults.Write(match.Args, "soak.sendqueue", "segments", queue);
            SpikeResults.Write(match.Args, "soak.sendqueue.start", "segments", Single(early / (double)Math.Min(DriftWindow, ticks)));
            SpikeResults.Write(match.Args, "soak.sendqueue.end", "segments", Single(late / (double)Math.Min(DriftWindow, ticks)));
            SpikeResults.Write(match.Args, "soak.server.ms", "ms", serverMs);
            SpikeResults.Write(match.Args, "soak.send.ms", "ms", sendMs);
            SpikeResults.Write(match.Args, "soak.sent", "KB/s", sent);
            SpikeResults.Write(match.Args, "soak.kbps", "KB/s", received);
            double sealReliable = sentReliable == 0 ? 1.0 : receivedReliable / (double)sentReliable;
            double sealUnreliable = sentUnreliable == 0 ? 1.0 : receivedUnreliable / (double)sentUnreliable;
            SpikeResults.Write(match.Args, "soak.seal.reliable", "share", Single(sealReliable));
            SpikeResults.Write(match.Args, "soak.seal.unreliable", "share", Single(sealUnreliable));

            Debug.Log($"[Soak] {clients} clients: latency p95 {latency.Max:F1} ms (worst client), " +
                      $"send queue max {peakQueue} (mean {queue.Mean:F1}, p95 {queue.Percentile(95):F0}), " +
                      $"transport {serverMs.Percentile(50):F3} ms p50/{serverMs.Percentile(95):F3} p95 a tick, " +
                      $"encoder+send {sendMs.Percentile(50):F2} ms p50; " +
                      $"sent {sent.Mean:F1} KB/s a client, received {received.Mean:F1} KB/s a client; " +
                      $"seal {(sealReliable + sealUnreliable) / 2:P1} (reliable {sealReliable:P1}, " +
                      $"unreliable {sealUnreliable:P1} - the unreliable channel may drop)");
        }

        /// <summary>
        /// Sends one stream to a connection: the eight-byte send timestamp, then the writer's bytes,
        /// chunked to what the channel's single message may carry. Returns the payload bytes sent.
        /// </summary>
        internal static long SendStream(SpikeKcpTransport transport, int connectionId, NetworkWriter writer,
            KcpChannel channel, NativeArray<int> messageSizes)
        {
            if (writer.Position == 0) return 0;
            ArraySegment<byte> payload = writer.ToArraySegment();
            long sent = 0;
            int offset = 0;
            if (channel == KcpChannel.Unreliable)
            {
                // One datagram per correction message, as packed: an unreliable message that spanned
                // two datagrams would be lost whole if either were.
                for (int i = 0; i < messageSizes.Length; i++)
                {
                    Send(transport, connectionId, payload, offset, messageSizes[i], Channels.Unreliable);
                    offset += messageSizes[i];
                    sent += messageSizes[i];
                }
                return sent;
            }

            // The timestamp header travels inside the same transport message, so it comes out of the limit.
            int max = SpikeWire.ReliableMaxMessageSize - Header.Length;
            while (offset < writer.Position)
            {
                int chunk = Math.Min(max, writer.Position - offset);
                Send(transport, connectionId, payload, offset, chunk, Channels.Reliable);
                offset += chunk;
                sent += chunk;
            }
            return sent;
        }

        private static void Send(SpikeKcpTransport transport, int connectionId, ArraySegment<byte> payload,
            int offset, int count, int channel)
        {
            BitConverter.TryWriteBytes(new Span<byte>(sendBuffer, 0, Header.Length), Stopwatch.GetTimestamp());
            Buffer.BlockCopy(payload.Array, payload.Offset + offset, sendBuffer, Header.Length, count);
            transport.ServerSend(connectionId, new ArraySegment<byte>(sendBuffer, 0, Header.Length + count), channel);
        }

        /// <summary>One arriving message: its timestamp turns into a latency sample.</summary>
        internal static void OnData(RawClient client, ArraySegment<byte> message, KcpChannel channel)
        {
            client.Bytes += message.Count;
            client.Messages++;
            if (channel == KcpChannel.Reliable) client.ReliableBytes += message.Count;
            else client.UnreliableBytes += message.Count;
            if (message.Count >= Header.Length)
            {
                long sent = BitConverter.ToInt64(message.Array, message.Offset);
                double ms = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                client.Latency.Add(ms);
            }
        }

        /// <summary>
        /// The transport connection a raw client holds: kcp2k identifies a connection by its remote
        /// endpoint, which is exactly the client's local endpoint on loopback.
        /// </summary>
        internal static int FindConnection(SpikeKcpTransport transport, RawClient client)
        {
            // Compare ports, not addresses: the transport's socket is dual mode, so the server sees a
            // client that connected to 127.0.0.1 as [::ffff:127.0.0.1] and the strings differ.
            var local = client.Peer.LocalEndPoint as IPEndPoint;
            if (local == null) return -1;
            foreach (KeyValuePair<int, KcpServerConnection> pair in transport.Server.connections)
            {
                if (pair.Value.remoteEndPoint is IPEndPoint remote && remote.Port == local.Port) return pair.Key;
            }
            return -1;
        }

        /// <summary>One-sample statistics, for metrics that are a single number.</summary>
        private static SpikeStats Single(double value)
        {
            var stats = new SpikeStats();
            stats.Add(value);
            return stats;
        }
    }
}

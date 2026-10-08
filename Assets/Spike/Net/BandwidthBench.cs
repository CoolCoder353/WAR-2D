using System.Collections;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The <c>bandwidth</c> bench, Stage A of the replication row: the 60-second scripted match runs
    /// the full simulation and fog, and the encoder writes every client's stream every tick. The bytes
    /// are counted exactly, message by message, and turned into wire bytes with Mirror's batching
    /// (an eight-byte timestamp per batch, batches of at most <see cref="SpikeWire.BatchThreshold"/>
    /// bytes), KCP's segmentation (24 bytes a segment, at most
    /// <see cref="SpikeWire.SegmentPayload"/> payload bytes) and UDP/IPv4 (28 bytes a datagram).
    ///
    /// <para><b>The match.</b> Eight players (the scenario always places eight; <c>-teams</c> picks the
    /// fog's team mapping: eight teams is FFA, two is 4v4) on <c>-map</c>² in the battle layout, an
    /// order pulse every two seconds to a quarter of each army, 20 spawns and 20 deaths a tick, and
    /// four terrain changes a second that rebuild the flow fields the routes come from. The measured
    /// window is <c>-ticks</c> ticks long, and defaults to the plan's 1,200 (60 s) because
    /// <see cref="SpikeArgs"/>'s own default of 600 is the other benches' 30 s.</para>
    ///
    /// <para><b>Measured section.</b> Orders, terrain, the sim tick and the fog tick are not timed;
    /// route maintenance, the interest build and the encoding are (<c>bw.routes.ms</c>,
    /// <c>bw.interest.ms</c>, <c>bw.encode.ms</c>). Four warm-up ticks precede the window, so the fog
    /// has refreshed every team and the armies are already engaged.</para>
    ///
    /// <para><b>The match-start snapshot</b> is the first encode, when every client's knowledge is
    /// empty: its Enter burst is the whole army (40,000 units a client in 4v4) plus its team's initial
    /// fog delta. It is recorded separately (<c>bw.snapshot.*</c>), and its seconds at a 256 KB/s cap
    /// are the time the client spends receiving it, since one message cannot carry it (the reliable
    /// limit is <see cref="SpikeWire.ReliableMaxMessageSize"/> bytes, and the encoder chunks at it).</para>
    /// </summary>
    public static class BandwidthBench
    {
        /// <summary>
        /// Warm-up ticks before the measured window. The fog refreshes a quarter of its teams a tick,
        /// so four ticks give every team a visibility grid - and the orders issued in them are what the
        /// first routes are traced from.
        /// </summary>
        private const int SnapshotTicks = 4;

        /// <summary>The plan's measured window: 60 seconds at 20 Hz.</summary>
        private const int PlanTicks = 1200;

        /// <summary>The cap the match-start snapshot's receive time is quoted at.</summary>
        private const int SnapshotCapBytesPerSecond = 256 * 1024;

        /// <summary>Ticks in the 1-second window behind <c>bw.peak1s</c>.</summary>
        private const int WindowTicks = 20;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => SpikeDriver.Benches["bandwidth"] = Run;

        public static IEnumerator Run(SpikeArgs a)
        {
            // The plan's window is 1,200 ticks; -ticks overrides it.
            int ticks = a.Ticks == SpikeArgs.Defaults.Ticks ? PlanTicks : a.Ticks;
            using var match = ReplicationMatch.Create(a, ticks, Allocator.Persistent);
            var perTickReliable = new long[(long)match.Clients * ticks];
            var perTickUnreliable = new long[(long)match.Clients * ticks];
            var perTickWire = new long[(long)match.Clients * ticks];

            Debug.Log($"[Bandwidth] {match.Scenario.UnitCount} units, {match.Clients} clients, {match.Teams} teams " +
                      $"({(match.Teams == FogTeams.FourVFourTeams ? "4v4" : "FFA")}), map {match.Map.Width}^2, " +
                      $"unit vision {match.UnitVision}, " +
                      $"battle layout, {ticks} measured ticks after {SnapshotTicks} warm-up ticks");
            yield return null;

            // The snapshot: every army's first Enter burst, taken when the client knows nothing.
            for (int t = 0; t < SnapshotTicks; t++)
            {
                match.Step(t);
                match.Maintain();
            }
            match.BuildInterests();
            var snapshotPayload = new SpikeStats();
            var snapshotWire = new SpikeStats();
            var snapshotUnits = new SpikeStats();
            var snapshotSeconds = new SpikeStats();
            for (int client = 0; client < match.Clients; client++)
            {
                match.Reliable.Position = 0;
                match.Unreliable.Position = 0;
                (int reliable, int unreliable) = match.Encode(client, SnapshotTicks);
                long wire = match.LastWireBytes();
                snapshotPayload.Add((reliable + unreliable) / 1024.0);
                snapshotWire.Add(wire / 1024.0);
                snapshotUnits.Add(match.Encoder.Count(client, SpikeMessageType.Enter));
                snapshotSeconds.Add(wire / (double)SnapshotCapBytesPerSecond);
            }
            SpikeResults.Write(a, "bw.snapshot.bytes", "KB", snapshotPayload);
            SpikeResults.Write(a, "bw.snapshot.wire", "KB", snapshotWire);
            SpikeResults.Write(a, "bw.snapshot.units", "units", snapshotUnits);
            SpikeResults.Write(a, "bw.snapshot.seconds", "s", snapshotSeconds);
            Debug.Log($"[Bandwidth] match-start snapshot: {snapshotUnits.Mean:F0} units and " +
                      $"{snapshotPayload.Mean / 1024.0:F1} MB a client ({snapshotWire.Mean / 1024.0:F1} MB on the wire), " +
                      $"{snapshotSeconds.Mean:F1} s at the 256 KB/s cap");
            yield return null;

            Measure(a, match, ticks, perTickReliable, perTickUnreliable, perTickWire);
        }

        /// <summary>
        /// The measured window: the scripted match with terrain changes, the encoder writing every
        /// client's stream every tick, and the byte counts kept per client per tick.
        /// </summary>
        private static void Measure(SpikeArgs a, ReplicationMatch match, int ticks,
            long[] perTickReliable, long[] perTickUnreliable, long[] perTickWire)
        {
            var encodeMs = new SpikeStats();
            var interestMs = new SpikeStats();
            var routeMs = new SpikeStats();
            var deferred = new SpikeStats();
            var sw = Stopwatch.StartNew();
            int clients = match.Clients;

            for (int i = 0; i < ticks; i++)
            {
                int tick = SnapshotTicks + i;
                match.Step(tick);

                sw.Restart();
                match.Maintain();
                sw.Stop();
                routeMs.Add(sw.Elapsed.TotalMilliseconds);

                sw.Restart();
                match.BuildInterests();
                sw.Stop();
                interestMs.Add(sw.Elapsed.TotalMilliseconds);

                sw.Restart();
                for (int client = 0; client < clients; client++)
                {
                    match.Reliable.Position = 0;
                    match.Unreliable.Position = 0;
                    (int reliable, int unreliable) = match.Encode(client, tick);
                    long index = (long)client * ticks + i;
                    perTickReliable[index] = reliable;
                    perTickUnreliable[index] = unreliable;
                    perTickWire[index] = match.LastWireBytes();
                }
                sw.Stop();
                encodeMs.Add(sw.Elapsed.TotalMilliseconds);
                deferred.Add(match.Maintenance.Deferred);

                if ((i + 1) % 200 != 0) continue;
                Debug.Log($"[Bandwidth] tick {tick} ({i + 1}/{ticks}): {encodeMs.Percentile(50):F1} ms encode p50, " +
                          $"route arena {match.Routes.ArenaBytes / (1024.0 * 1024.0):F1} MB " +
                          $"({match.Routes.LiveWaypoints} live waypoints), {match.Maintenance.Deferred} deferred, " +
                          $"{match.Maintenance.Changes} changed of {match.Maintenance.Traces} traces");
            }

            // Per client: the mean and the worst 1-second window, payload and wire.
            var avg = new SpikeStats();
            var avgWire = new SpikeStats();
            var peak = new SpikeStats();
            var peakWire = new SpikeStats();
            for (int client = 0; client < clients; client++)
            {
                long payload = 0, wire = 0, peakPayload = 0, peakWireBytes = 0;
                for (int i = 0; i < ticks; i++)
                {
                    long index = (long)client * ticks + i;
                    payload += perTickReliable[index] + perTickUnreliable[index];
                    wire += perTickWire[index];
                }
                for (int start = 0; start + WindowTicks <= ticks; start += WindowTicks)
                {
                    long windowPayload = 0, windowWire = 0;
                    for (int k = 0; k < WindowTicks; k++)
                    {
                        long index = (long)client * ticks + start + k;
                        windowPayload += perTickReliable[index] + perTickUnreliable[index];
                        windowWire += perTickWire[index];
                    }
                    peakPayload = math.max(peakPayload, windowPayload);
                    peakWireBytes = math.max(peakWireBytes, windowWire);
                }
                double seconds = ticks / (double)SpikeScenario.TicksPerSecond;
                avg.Add(payload / 1024.0 / seconds);
                avgWire.Add(wire / 1024.0 / seconds);
                peak.Add(peakPayload / 1024.0);
                peakWire.Add(peakWireBytes / 1024.0);
            }
            SpikeResults.Write(a, "bw.avg", "KB/s", avg);
            SpikeResults.Write(a, "bw.peak1s", "KB/s", peak);
            SpikeResults.Write(a, "bw.wire.avg", "KB/s", avgWire);
            SpikeResults.Write(a, "bw.wire.peak1s", "KB/s", peakWire);
            SpikeResults.Write(a, "bw.encode.ms", "ms", encodeMs);
            SpikeResults.Write(a, "bw.interest.ms", "ms", interestMs);
            SpikeResults.Write(a, "bw.routes.ms", "ms", routeMs);
            SpikeResults.Write(a, "bw.routes.deferred", "units", deferred);

            // The payload's split by message type, as a share of each client's own bytes.
            var clientBytes = new long[clients];
            for (int client = 0; client < clients; client++)
                for (int type = 1; type <= 7; type++) clientBytes[client] += match.Encoder.Bytes(client, (SpikeMessageType)type);
            foreach (SpikeMessageType type in new[]
                     {
                         SpikeMessageType.MoveOrder, SpikeMessageType.Correction, SpikeMessageType.Health,
                         SpikeMessageType.Enter, SpikeMessageType.Leave, SpikeMessageType.Explosion,
                         SpikeMessageType.FogDelta,
                     })
            {
                var share = new SpikeStats();
                var counts = new SpikeStats();
                for (int client = 0; client < clients; client++)
                {
                    share.Add(clientBytes[client] <= 0 ? 0 : match.Encoder.Bytes(client, type) / (double)clientBytes[client]);
                    counts.Add(match.Encoder.Count(client, type));
                }
                SpikeResults.Write(a, $"bw.bytype.{type.ToString().ToLowerInvariant()}", "share", share);
                SpikeResults.Write(a, $"bw.{type.ToString().ToLowerInvariant()}", "count", counts);
            }

            // The route shape, and what the greedy line-of-sight smoothing pass would have saved.
            RouteMaintenance maintenance = match.Maintenance;
            if (maintenance.Samples > 0)
            {
                SpikeResults.Write(a, "bw.waypoints", "waypoints", Single(maintenance.SampleRaw / (double)maintenance.Samples));
                SpikeResults.Write(a, "bw.waypoints.smoothed", "waypoints", Single(maintenance.SampleSmoothed / (double)maintenance.Samples));
                SpikeResults.Write(a, "bw.waypoints.saved", "share", Single(1.0 - maintenance.SampleSmoothed / (double)maintenance.SampleRaw));
            }
            SpikeResults.Write(a, "bw.units", "units", Single(match.LiveUnits()));
            // Why the corrections are what they are: how many of the units are ordered (so the client
            // predicts movement) and how many are engaged (so the simulation holds them still).
            int live = 0, engaged = 0, ordered = 0;
            for (int i = 0; i < match.Sim.Data.Capacity; i++)
            {
                if (match.Sim.Data.Health[i] <= 0f) continue;
                live++;
                if (match.Sim.Data.Target[i] >= 0) engaged++;
                if (match.Sim.Data.FieldGoal[i] >= 0) ordered++;
            }
            SpikeResults.Write(a, "bw.units.engaged", "share", Single(live == 0 ? 0 : engaged / (double)live));
            SpikeResults.Write(a, "bw.units.ordered", "share", Single(live == 0 ? 0 : ordered / (double)live));
            int allowed = BitSet.Count(match.Interests.Allowed(0));
            SpikeResults.Write(a, "bw.allowed", "units", Single(allowed));
            SpikeResults.Write(a, "bw.corrections.perunit", "corrections", Single(
                allowed <= 0 ? 0 : match.Encoder.Count(0, SpikeMessageType.Correction) / (double)allowed / ticks));
            SpikeResults.Write(a, "bw.route.live", "waypoints", Single(match.Routes.LiveWaypoints));
            SpikeResults.Write(a, "bw.route.arena", "MB", Single(match.Routes.ArenaBytes / (1024.0 * 1024.0)));

            Debug.Log($"[Bandwidth] {clients} clients, {(match.Teams == FogTeams.FourVFourTeams ? "4v4" : "FFA")}, " +
                      $"{match.Map.Width}^2, vision {a.VisionRadius}: payload {avg.Mean:F1} KB/s a client " +
                      $"(worst {avg.Max:F1}, peak 1 s {peak.Max:F1}), wire {avgWire.Mean:F1} KB/s (worst {avgWire.Max:F1}, " +
                      $"peak 1 s {peakWire.Max:F1}); encode {encodeMs.Percentile(50):F2} ms p50/{encodeMs.Percentile(95):F2} p95, " +
                      $"interest {interestMs.Percentile(50):F2}, routes {routeMs.Percentile(50):F2} ms p50 " +
                      $"({maintenance.Changes} changed of {maintenance.Traces} traces, {maintenance.Deferred} deferred); " +
                      $"corrections {match.Encoder.Count(0, SpikeMessageType.Correction)} a client, " +
                      $"enters {match.Encoder.Count(0, SpikeMessageType.Enter)}, " +
                      $"fog cells {match.Encoder.Count(0, SpikeMessageType.FogDelta)}, " +
                      $"live units {match.LiveUnits()}");
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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Dev.Logging;
using WAR2D.Net.Replication;
using WAR2D.Sim;

namespace WAR2D.Net.Replay
{
    /// <summary>
    /// Records a match from the server: a virtual replication client that sees everything (bombs included, no
    /// fog, every unit on the offscreen correction tier), plus explosions, blasts, a once-a-second public
    /// timeline and the outcome. Payloads are copied on the main thread and written by a background thread;
    /// a full queue or the size cap stops the recording (the file stays playable up to there).
    /// </summary>
    public sealed class MatchRecorder : IDisposable
    {
        public const int RecorderOwner = int.MinValue + 1;
        private const int QueueLimit = 4096;

        private readonly ReplayWriter writer;
        private readonly FileStream file;
        private readonly SimContext sim;
        private readonly ReplicationService replication;
        private readonly string partPath, finalPath;
        private readonly long maxBytes;
        private readonly BlockingCollection<(int tick, ReplayChannel channel, byte[] bytes)> queue = new BlockingCollection<(int, ReplayChannel, byte[])>(QueueLimit);
        private readonly Thread thread;
        private int currentTick, firstTick = -1;
        private readonly float tickSeconds;
        private long queuedBytes;
        private NativeArray<byte> fogLast;
        private double nextFogAt;
        private bool fogDropped;
        /// <summary>Past this share of the cap fog is dropped first, so a long match keeps its units and its end (spec 5.1).</summary>
        private const float FogShareOfCap = 0.75f;
        private volatile bool stopped;

        /// <summary>Uncompressed payload bytes accepted so far (the perf run reports its rate).</summary>
        public long QueuedBytes => queuedBytes;

        public bool Stopped => stopped;
        public bool Finished { get; private set; }
        public string FinishedPath => Finished ? finalPath : null;

        public MatchRecorder(ReplicationService replication, SimContext sim, ReplayHeader header, string path, long maxBytes)
        {
            this.sim = sim;
            this.replication = replication;
            tickSeconds = header.TickSeconds;
            finalPath = path;
            partPath = path + ".part";
            this.maxBytes = maxBytes;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            writer = new ReplayWriter(file, header);
            thread = new Thread(WriteLoop) { IsBackground = true, Name = "Replay writer", Priority = ThreadPriority.BelowNormal }; // never ahead of the tick's jobs
            thread.Start();
            replication.AddVirtualClient(RecorderOwner, Receive, seesAll: true);
            // Its units go through ReplicationService's deferred lane: every unit on the offscreen tier, a tick late.
        }

        /// <summary>Call after each settled boundary with its tick, before the replication sink runs for the next.</summary>
        public void OnTick(int tick) => currentTick = tick;

        private void Receive(ArraySegment<byte> payload, int sinkChannel)
        {
            // The tick replication says the payload belongs to (units arrive a tick late through the deferred lane).
            currentTick = replication.SinkTick;
            ReplayChannel channel = sinkChannel switch
            {
                ReplicationService.FogSinkChannel => ReplayChannel.Fog,
                ReplicationService.BuildingSinkChannel => ReplayChannel.Buildings,
                ReplicationService.TerrainSinkChannel => ReplayChannel.Terrain,
                _ when sinkChannel == Mirror.Channels.Unreliable => ReplayChannel.UnitsUnreliable,
                _ => ReplayChannel.UnitsReliable,
            };
            Enqueue(channel, payload.AsSpan());
        }

        public void Explosions(int tick, IReadOnlyList<float2> positions) => Positions(tick, ReplayChannel.Explosions, positions);
        public void Blasts(int tick, IReadOnlyList<float2> positions) => Positions(tick, ReplayChannel.Blasts, positions);
        public void BomberBlasts(int tick, IReadOnlyList<float2> positions) => Positions(tick, ReplayChannel.BomberBlasts, positions);

        private void Positions(int tick, ReplayChannel channel, IReadOnlyList<float2> positions)
        {
            if (positions.Count == 0) return;
            currentTick = tick;
            var bytes = new byte[positions.Count * 8];
            for (int i = 0; i < positions.Count; i++)
            {
                BitConverter.TryWriteBytes(new Span<byte>(bytes, i * 8, 4), positions[i].x);
                BitConverter.TryWriteBytes(new Span<byte>(bytes, i * 8 + 4, 4), positions[i].y);
            }
            Enqueue(channel, bytes);
        }

        /// <summary>The settled tick's shells: each one fired (from, landing, seconds of flight, as floats), and each landing. Attack events never reach a recording (its view box is empty), so these have channels of their own.</summary>
        public void Shells(SimData data, int tick)
        {
            NativeArray<Shell> fired = data.ShellsFired(tick);
            if (fired.Length > 0)
            {
                currentTick = tick;
                var floats = new float[fired.Length * 5];
                for (int i = 0; i < fired.Length; i++)
                {
                    Shell s = fired[i];
                    floats[i * 5] = s.From.x;
                    floats[i * 5 + 1] = s.From.y;
                    floats[i * 5 + 2] = s.Landing.x;
                    floats[i * 5 + 3] = s.Landing.y;
                    floats[i * 5 + 4] = (s.LandTick - s.FiredTick) * tickSeconds;
                }
                var bytes = new byte[floats.Length * sizeof(float)];
                Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
                Enqueue(ReplayChannel.ShellFlights, bytes);
            }
            if (data.ShellLandings.Length == 0) return;
            var points = new List<float2>(data.ShellLandings.Length);
            foreach (ShellLanding landing in data.ShellLandings) points.Add(landing.Point);
            Positions(tick, ReplayChannel.ShellLandings, points);
        }

        /// <summary>Once a second: each player's public numbers, for the viewer's player list.</summary>
        public void Timeline(int tick, IEnumerable<(int owner, float resources, int units, int buildings)> rows)
        {
            currentTick = tick;
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(rows);
            Enqueue(ReplayChannel.Timeline, System.Text.Encoding.UTF8.GetBytes(json));
        }

        /// <summary>The end screen's graphs and timeline (v0.18), written once at game over so the viewer has them from the start of playback.</summary>
        public void Summary(SummaryData summary)
        {
            bool wasStopped = stopped;
            stopped = false; // goes in even when the size cap stopped the rest
            Enqueue(ReplayChannel.Summary, System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(summary)));
            stopped = wasStopped;
        }

        /// <summary>Every <c>Replays/FogSeconds</c> of match time: each player's changed fog cells, on the settled world.</summary>
        public void TeamFog(SimData data, int tick, double now, float fogSeconds)
        {
            if (stopped || fogDropped || now < nextFogAt) return;
            nextFogAt = now + fogSeconds;
            currentTick = tick;
            if (queuedBytes > maxBytes * FogShareOfCap)
            {
                fogDropped = true;
                Enqueue(ReplayChannel.TeamFogDropped, ReadOnlySpan<byte>.Empty);
                WarLog.Warn(LogCategory.Replay, "recording: dropped the team fog channel near the size cap");
                return;
            }
            int total = data.FogCells * SimData.MaxOwners;
            if (!fogLast.IsCreated) fogLast = new NativeArray<byte>(total, Allocator.Persistent);
            using var output = new NativeList<byte>(1024, Allocator.TempJob);
            new TeamFogEncodeJob { Visible = data.Visible, Explored = data.Explored, OwnerBySlot = data.OwnerIdBySlot, Last = fogLast, Out = output, FogCells = data.FogCells, Slots = SimData.MaxOwners }.Run();
            if (output.Length > 0) Enqueue(ReplayChannel.TeamFog, output.AsArray().AsReadOnlySpan());
        }

        /// <summary>Writes the outcome, closes the file and renames it from <c>.part</c>.</summary>
        public void End(int tick, string outcomeJson)
        {
            if (Finished) return;
            currentTick = tick;
            bool wasStopped = stopped;
            stopped = false; // the end record always goes in
            Enqueue(ReplayChannel.End, System.Text.Encoding.UTF8.GetBytes(outcomeJson ?? "{}"));
            stopped = true;
            replication.RemoveVirtualClient(RecorderOwner); // nothing more to record: stop encoding the all-seeing lane
            queue.CompleteAdding();
            thread.Join();
            writer.Dispose();
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partPath, finalPath);
            Finished = true;
            ReplayLibrary.WriteInfo(finalPath, Math.Max(0, tick - Math.Max(0, firstTick)) * tickSeconds);
            WarLog.Info(LogCategory.Replay, $"recording saved: {finalPath} ({new FileInfo(finalPath).Length / 1024} KB{(wasStopped ? ", stopped early at the size cap" : "")})");
        }

        private void Enqueue(ReplayChannel channel, ReadOnlySpan<byte> payload)
        {
            if (stopped) return;
            if (firstTick < 0) firstTick = currentTick;
            if (queuedBytes + payload.Length > maxBytes || !queue.TryAdd((currentTick, channel, payload.ToArray())))
            {
                stopped = true;
                WarLog.Warn(LogCategory.Replay, "recording stopped: " + (queuedBytes + payload.Length > maxBytes ? "size cap reached" : "writer fell behind"));
                return;
            }
            queuedBytes += payload.Length;
        }

        private void WriteLoop()
        {
            foreach (var (tick, channel, bytes) in queue.GetConsumingEnumerable())
            {
                try { writer.Write(tick, channel, bytes); }
                catch (IOException e) { stopped = true; WarLog.Error(LogCategory.Replay, "recording write failed: " + e.Message); }
            }
        }

        public void Dispose()
        {
            if (fogLast.IsCreated) fogLast.Dispose();
            if (!Finished)
            {
                stopped = true;
                replication.RemoveVirtualClient(RecorderOwner);
                if (!queue.IsAddingCompleted) queue.CompleteAdding();
                thread.Join();
                writer.Dispose();
                if (File.Exists(partPath)) File.Delete(partPath); // an unfinished recording (the host left mid-match) is dropped
            }
            queue.Dispose();
        }
    }
}

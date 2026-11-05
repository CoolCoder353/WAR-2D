using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client;
using WAR2D.Net.Replay;
using WAR2D.Net.Replication;
using WAR2D.Sim;

public class MatchRecorderTests
{
    private string dir;
    [SetUp] public void SetUp() { dir = Path.Combine(Path.GetTempPath(), "w2d-rec-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); }
    [TearDown] public void TearDown() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    [Test]
    public void RecordingPlaysIntoTheSameClientStateAsALiveSpectator()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        using var replication = new ReplicationService(sim.Context, sim.Config);
        var live = new ClientUnitStore(sim.Config.Simulation.MaxEntities, sim.Config.Simulation.TickSeconds);
        // A live spectator (an eliminated player watching): the normal boundary path, not the recorder's deferred lane.
        replication.Spectating = owner => owner == -77;
        replication.AddVirtualClient(-77, (p, ch) => { if (ch >= 0) live.Apply(p, 0); });
        string path = Path.Combine(dir, "m" + ReplayFile.Extension);
        var recorder = new MatchRecorder(replication, sim.Context, new ReplayHeader { MapSize = 96, Seed = 1, TickSeconds = sim.Config.Simulation.TickSeconds, GameVersion = "t" }, path, 64 << 20);
        for (int i = 0; i < 30; i++) sim.Spawn(i % 2 == 0 ? SimHarness.OwnerA : SimHarness.OwnerB, new float2(10.5f + i, 10.5f + i % 7));
        for (int t = 0; t < 60; t++) { sim.Tick(); recorder.OnTick(sim.Context.Clock.Tick); }
        recorder.End(sim.Context.Clock.Tick, "{}");
        Assert.IsTrue(recorder.Finished);

        var replayed = new ClientUnitStore(sim.Config.Simulation.MaxEntities, sim.Config.Simulation.TickSeconds);
        using (FileStream f = File.OpenRead(recorder.FinishedPath))
        {
            Assert.IsTrue(ReplayReader.TryOpen(f, out ReplayReader r, out string e), e);
            using (r)
                while (r.TryNext(out int tick, out ReplayChannel ch, out ArraySegment<byte> p))
                    if (ch == ReplayChannel.UnitsReliable || ch == ReplayChannel.UnitsUnreliable) Assert.IsTrue(replayed.Apply(p, 0));
        }
        Assert.AreEqual(live.Count, replayed.Count);
        for (int k = 0; k < live.Count; k++)
        {
            int index = live.IndexAt(k);
            Assert.AreEqual(live.IdOf(index), replayed.IdOf(index));
            Assert.AreEqual(live.StateOf(index), replayed.StateOf(index));
        }
        live.Dispose(); replayed.Dispose(); recorder.Dispose();
    }

    [Test]
    public void ShellsGoToTheirOwnChannelsAndNeverIntoTheUnits()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96), c => c.Simulation.TargetSearchSliceTicks = 1);
        using var replication = new ReplicationService(sim.Context, sim.Config);
        string path = Path.Combine(dir, "shells" + ReplayFile.Extension);
        var recorder = new MatchRecorder(replication, sim.Context, new ReplayHeader { MapSize = 96, TickSeconds = sim.Config.Simulation.TickSeconds, GameVersion = "t" }, path, 64 << 20);
        sim.Context.Settled += (data, tick, count) => recorder.Shells(data, tick);
        float2 landing = new float2(26f, 30f);
        sim.Spawn(SimHarness.OwnerA, new float2(10f, 30f), UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerB, landing);
        for (int t = 0; t < 40; t++) { sim.Tick(); recorder.OnTick(sim.Context.Clock.Tick); }
        recorder.End(sim.Context.Clock.Tick, "{}");

        int flights = 0, landings = 0;
        var store = new ClientUnitStore(sim.Config.Simulation.MaxEntities, sim.Config.Simulation.TickSeconds);
        store.ShellFired += (gun, at, seconds) => Assert.Fail("a recording has no shell messages");
        store.ShellLanded += at => Assert.Fail("a recording has no shell messages");
        using (FileStream f = File.OpenRead(recorder.FinishedPath))
        {
            Assert.IsTrue(ReplayReader.TryOpen(f, out ReplayReader r, out string e), e);
            using (r)
                while (r.TryNext(out int tick, out ReplayChannel ch, out ArraySegment<byte> p))
                {
                    if (ch == ReplayChannel.UnitsReliable || ch == ReplayChannel.UnitsUnreliable) Assert.IsTrue(store.Apply(p, 0));
                    if (ch == ReplayChannel.ShellFlights)
                    {
                        flights++;
                        Assert.AreEqual(20, p.Count);
                        Assert.AreEqual(landing.x, BitConverter.ToSingle(p.Array, p.Offset + 8));
                        Assert.AreEqual(16f / 14f, BitConverter.ToSingle(p.Array, p.Offset + 16), 0.06f);
                    }
                    if (ch == ReplayChannel.ShellLandings)
                    {
                        landings++;
                        Assert.AreEqual(landing.x, BitConverter.ToSingle(p.Array, p.Offset));
                    }
                }
        }
        Assert.AreEqual(1, flights);
        Assert.AreEqual(1, landings);
        store.Dispose(); recorder.Dispose();
    }

    [Test]
    public void StopsAtTheSizeCapAndKeepsAPlayableFile()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        using var replication = new ReplicationService(sim.Context, sim.Config);
        string path = Path.Combine(dir, "cap" + ReplayFile.Extension);
        var recorder = new MatchRecorder(replication, sim.Context, new ReplayHeader { MapSize = 96, TickSeconds = 0.05f, GameVersion = "t" }, path, maxBytes: 2048);
        for (int i = 0; i < 200; i++) sim.Spawn(SimHarness.OwnerA, new float2(5.5f + i % 80, 5.5f + i / 80));
        for (int t = 0; t < 40; t++) { sim.Tick(); recorder.OnTick(sim.Context.Clock.Tick); }
        Assert.IsTrue(recorder.Stopped);
        recorder.End(sim.Context.Clock.Tick, "{}");
        using FileStream f = File.OpenRead(recorder.FinishedPath);
        Assert.IsTrue(ReplayReader.TryOpen(f, out ReplayReader r, out _));
        r.Dispose();
        recorder.Dispose();
    }
}

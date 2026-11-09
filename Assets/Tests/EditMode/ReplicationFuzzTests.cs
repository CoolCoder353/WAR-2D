using System;
using System.Collections.Generic;
using System.IO;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Client;
using WAR2D.Net.Replication;

/// <summary>
/// Structure-aware fuzzing of what a client decodes from the server (2026-11-09). Each test encodes valid messages, mutates
/// them (flipped bits, replaced, inserted, deleted, duplicated and truncated bytes) and feeds them to the decoders. A server
/// or anything in between can send any bytes, and a release build has no bounds checks, so:
/// <list type="bullet">
/// <item>the Burst decoders (<see cref="Messages.TryDecodeMoveOrder"/>, <see cref="Messages.TryDecodeCorrection"/>,
/// <see cref="Messages.TryDecodeHealth"/>) never throw (here, with the editor's bounds checks on, an out-of-range read or
/// write would) and agree with the managed decoders on every input: the same accept or refuse, the same units, the same end;</item>
/// <item>an accepted message keeps the invariants the client's jobs index by (index in range, routes inside the waypoints);</item>
/// <item>the client stores (<see cref="ClientUnitStore"/>, <see cref="ClientCombat"/>, <see cref="BuildingCodec"/>,
/// <see cref="FogCodec"/>) refuse what they can't read and never throw anything else; an error logged by a job fails the test.</item>
/// </list>
/// Seeds are fixed, so a failure reproduces; the iteration counts keep the fixture under a few seconds.
/// </summary>
public class ReplicationFuzzTests
{
    private const int Iterations = 6000;

    // ---- mutation ----

    /// <summary>One to four random edits of <paramref name="valid"/>.</summary>
    private static byte[] Mutate(byte[] valid, System.Random rng)
    {
        var bytes = new List<byte>(valid);
        int edits = rng.Next(1, 5);
        for (int e = 0; e < edits; e++)
        {
            if (bytes.Count == 0) { bytes.Add((byte)rng.Next(256)); continue; }
            int at = rng.Next(bytes.Count);
            switch (rng.Next(9))
            {
                case 0: bytes[at] ^= (byte)(1 << rng.Next(8)); break;                                  // flip a bit
                case 1: bytes[at] = (byte)rng.Next(256); break;                                         // replace a byte
                case 2: bytes[at] = Interesting[rng.Next(Interesting.Length)]; break;                    // an edge value
                case 3: bytes.Insert(at, (byte)rng.Next(256)); break;                                   // insert
                case 4: bytes.RemoveAt(at); break;                                                      // delete
                case 5: bytes.RemoveRange(at, bytes.Count - at); break;                                 // truncate
                case 6:                                                                                 // duplicate a run
                    int len = math.min(rng.Next(1, 9), bytes.Count - at);
                    bytes.InsertRange(at, bytes.GetRange(at, len));
                    break;
                case 7:                                                                                 // a long varint
                    bytes.InsertRange(at, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, (byte)rng.Next(0x10) });
                    break;
                default:                                                                                // swap two bytes
                    int other = rng.Next(bytes.Count);
                    (bytes[at], bytes[other]) = (bytes[other], bytes[at]);
                    break;
            }
        }
        return bytes.ToArray();
    }

    private static readonly byte[] Interesting = { 0x00, 0x01, 0x7F, 0x80, 0x81, 0xFE, 0xFF };

    private static byte[] Bytes(NativeList<byte> list) => list.AsArray().ToArray();

    // ---- valid messages ----

    /// <summary>A MoveOrder for 1..40 units (rising indices, some repeated) whose routes repeat each other often (copy steps).</summary>
    private static byte[] ValidMoveOrder(System.Random rng)
    {
        int n = rng.Next(1, 41);
        using var tiles = new NativeList<int2>(Allocator.Temp);
        var unitList = new RouteUnit[n];
        int index = rng.Next(0, 64);
        var shared = new List<int2>();
        for (int i = 0; i < n; i++)
        {
            index += rng.Next(4) == 0 ? 0 : rng.Next(1, 300);
            int first = tiles.Length;
            int count = rng.Next(1, 24);
            if (shared.Count > 0 && rng.Next(3) == 0)
                foreach (int2 t in shared) tiles.Add(t); // the same route as an earlier unit: a copy step
            else
            {
                var t = new int2(rng.Next(0, 1024), rng.Next(0, 1024));
                shared.Clear();
                for (int k = 0; k < count; k++)
                {
                    t = math.clamp(t + new int2(rng.Next(-3, 4), rng.Next(-3, 4)), 0, 1023);
                    if (rng.Next(20) == 0) t = new int2(rng.Next(0, 1024), rng.Next(0, 1024)); // a jump: an absolute step
                    tiles.Add(t);
                    shared.Add(t);
                }
            }
            unitList[i] = new RouteUnit { Index = math.min(index, NetIdAllocator.MaxCapacity - 1), First = first, Count = tiles.Length - first };
        }
        using var units = new NativeArray<RouteUnit>(unitList, Allocator.Temp);
        using var bytes = new NativeList<byte>(256, Allocator.Temp);
        var sink = new NativeSink(bytes);
        Messages.EncodeMoveOrder(ref sink, rng.Next(0, 1 << 20), tiles.AsArray(), units);
        return Bytes(bytes);
    }

    private static byte[] ValidCorrection(System.Random rng)
    {
        int n = rng.Next(1, 60), index = rng.Next(0, 64);
        var unitList = new CorrectionUnit[n];
        for (int i = 0; i < n; i++)
        {
            index += rng.Next(1, 200);
            unitList[i] = new CorrectionUnit { Index = math.min(index, NetIdAllocator.MaxCapacity - 1), DX = (short)rng.Next(short.MinValue, short.MaxValue),
                DY = (short)rng.Next(short.MinValue, short.MaxValue), Resume = rng.Next(0, 4096), Speed = (byte)rng.Next(0, 65) };
        }
        using var units = new NativeArray<CorrectionUnit>(unitList, Allocator.Temp);
        using var bytes = new NativeList<byte>(256, Allocator.Temp);
        var sink = new NativeSink(bytes);
        Messages.EncodeCorrection(ref sink, rng.Next(0, 1 << 20), (byte)rng.Next(1, 33), units);
        return Bytes(bytes);
    }

    private static byte[] ValidHealth(System.Random rng)
    {
        int n = rng.Next(1, 80), index = rng.Next(0, 64);
        var unitList = new HealthUnit[n];
        for (int i = 0; i < n; i++)
        {
            index += rng.Next(1, 200);
            unitList[i] = new HealthUnit { Index = math.min(index, NetIdAllocator.MaxCapacity - 1), Health = (byte)rng.Next(0, 101) };
        }
        using var units = new NativeArray<HealthUnit>(unitList, Allocator.Temp);
        using var bytes = new NativeList<byte>(256, Allocator.Temp);
        var sink = new NativeSink(bytes);
        Messages.EncodeHealth(ref sink, units);
        return Bytes(bytes);
    }

    /// <summary>A whole unit batch as the client gets one: Enter, MoveOrder, Correction, Health, Leave and Attack messages.</summary>
    private static byte[] ValidUnitBatch(System.Random rng, int idCapacity)
    {
        using var bytes = new NativeList<byte>(512, Allocator.Temp);
        var sink = new NativeSink(bytes);
        int n = rng.Next(1, 12);
        using var tiles = new NativeList<int2>(Allocator.Temp);
        var enterList = new EnterUnit[n];
        for (int i = 0; i < n; i++)
        {
            int first = tiles.Length, count = rng.Next(1, 6);
            var t = new int2(rng.Next(0, 200), rng.Next(0, 200));
            for (int k = 0; k < count; k++) tiles.Add(t += new int2(rng.Next(-2, 3), rng.Next(-2, 3)));
            enterList[i] = new EnterUnit { Id = (rng.Next(1, 8) << 20) | (i * 7 + 1), Type = (byte)rng.Next(0, 4), OwnerId = rng.Next(1, 9),
                X = (ushort)rng.Next(0, 6000), Y = (ushort)rng.Next(0, 6000), Health = (byte)rng.Next(1, 101), First = first, Count = count };
        }
        using var enters = new NativeArray<EnterUnit>(enterList, Allocator.Temp);
        Messages.EncodeEnter(ref sink, tiles.AsArray(), enters);
        foreach (byte b in ValidMoveOrder(rng)) bytes.Add(b);
        foreach (byte b in ValidCorrection(rng)) bytes.Add(b);
        foreach (byte b in ValidHealth(rng)) bytes.Add(b);
        using var leaves = new NativeArray<LeaveUnit>(new[] { new LeaveUnit { Index = rng.Next(0, idCapacity), Reason = LeaveReason.Died } }, Allocator.Temp);
        Messages.EncodeLeave(ref sink, leaves);
        using var attacks = new NativeArray<AttackEvent>(new[] { new AttackEvent { AttackerIndex = rng.Next(0, idCapacity), TargetId = rng.Next(1, 1 << 24) } }, Allocator.Temp);
        Messages.EncodeAttack(ref sink, attacks);
        return Bytes(bytes);
    }

    // ---- the unit stream's decoders: Burst against managed ----

    private static bool Refused(Exception e) => e is InvalidDataException || e is EndOfStreamException;

    [Test]
    public void MoveOrder_BurstAndManagedDecodersAgreeOnMutatedMessages()
    {
        var rng = new System.Random(4401);
        int accepted = 0;
        var mUnits = new List<RouteUnit>();
        var mTiles = new List<int2>();
        using var bUnits = new NativeList<RouteUnit>(64, Allocator.Temp);
        using var bTiles = new NativeList<int2>(256, Allocator.Temp);
        for (int i = 0; i < Iterations; i++)
        {
            byte[] input = i % 10 == 0 ? ValidMoveOrder(rng) : Mutate(ValidMoveOrder(rng), rng);
            mUnits.Clear(); mTiles.Clear(); bUnits.Clear(); bTiles.Clear();

            bool managedOk; int managedTick = 0, managedEnd = 0;
            var reader = new NetworkReader(input);
            try { managedTick = Messages.DecodeMoveOrder(reader, mUnits, mTiles); managedOk = true; managedEnd = reader.Position; }
            catch (Exception e) when (Refused(e)) { managedOk = false; }

            using var native = new NativeArray<byte>(input, Allocator.Temp);
            int at = 0;
            bool burstOk = Messages.TryDecodeMoveOrder(native, ref at, bUnits, bTiles, out int burstTick, out _);

            Assert.AreEqual(managedOk, burstOk, $"iteration {i}: managed {(managedOk ? "accepts" : "refuses")}, Burst {(burstOk ? "accepts" : "refuses")} {Hex(input)}");
            if (!burstOk) continue;
            accepted++;
            Assert.AreEqual((managedTick, managedEnd), (burstTick, at), $"iteration {i}: tick and end {Hex(input)}");
            CollectionAssert.AreEqual(mUnits, bUnits.AsArray().ToArray(), $"iteration {i}: units");
            CollectionAssert.AreEqual(mTiles, bTiles.AsArray().ToArray(), $"iteration {i}: waypoints");
            foreach (RouteUnit u in mUnits)
            {
                Assert.That(u.Index, Is.InRange(0, NetIdAllocator.MaxCapacity - 1));
                Assert.That(u.Count, Is.InRange(1, Messages.MaxRouteWaypoints));
                Assert.That(u.First + u.Count, Is.LessThanOrEqualTo(mTiles.Count));
            }
        }
        Assert.That(accepted, Is.GreaterThan(Iterations / 10), "the mutations still reach the decoders' deep paths");
    }

    [Test]
    public void Correction_BurstAndManagedDecodersAgreeOnMutatedMessages()
    {
        var rng = new System.Random(4402);
        var mUnits = new List<CorrectionUnit>();
        using var bUnits = new NativeList<CorrectionUnit>(64, Allocator.Temp);
        for (int i = 0; i < Iterations; i++)
        {
            byte[] input = i % 10 == 0 ? ValidCorrection(rng) : Mutate(ValidCorrection(rng), rng);
            mUnits.Clear(); bUnits.Clear();
            bool managedOk; int managedTick = 0, managedEnd = 0; byte managedScale = 0;
            var reader = new NetworkReader(input);
            try { managedTick = Messages.DecodeCorrection(reader, mUnits, out managedScale); managedOk = true; managedEnd = reader.Position; }
            catch (Exception e) when (Refused(e)) { managedOk = false; }
            using var native = new NativeArray<byte>(input, Allocator.Temp);
            int at = 0;
            bool burstOk = Messages.TryDecodeCorrection(native, ref at, bUnits, out int burstTick, out byte burstScale);
            Assert.AreEqual(managedOk, burstOk, $"iteration {i}: {Hex(input)}");
            if (!burstOk) continue;
            Assert.AreEqual((managedTick, (int)managedScale, managedEnd), (burstTick, (int)burstScale, at), $"iteration {i}: {Hex(input)}");
            CollectionAssert.AreEqual(mUnits, bUnits.AsArray().ToArray(), $"iteration {i}: units");
            foreach (CorrectionUnit u in mUnits) Assert.That(u.Index, Is.InRange(0, NetIdAllocator.MaxCapacity - 1));
        }
    }

    [Test]
    public void Health_BurstAndManagedDecodersAgreeOnMutatedMessages()
    {
        var rng = new System.Random(4403);
        var mUnits = new List<HealthUnit>();
        using var bUnits = new NativeList<HealthUnit>(64, Allocator.Temp);
        for (int i = 0; i < Iterations; i++)
        {
            byte[] input = i % 10 == 0 ? ValidHealth(rng) : Mutate(ValidHealth(rng), rng);
            mUnits.Clear(); bUnits.Clear();
            bool managedOk; int managedEnd = 0;
            var reader = new NetworkReader(input);
            try { Messages.DecodeHealth(reader, mUnits); managedOk = true; managedEnd = reader.Position; }
            catch (Exception e) when (Refused(e)) { managedOk = false; }
            using var native = new NativeArray<byte>(input, Allocator.Temp);
            int at = 0;
            bool burstOk = Messages.TryDecodeHealth(native, ref at, bUnits);
            Assert.AreEqual(managedOk, burstOk, $"iteration {i}: {Hex(input)}");
            if (!burstOk) continue;
            Assert.AreEqual(managedEnd, at, $"iteration {i}: end {Hex(input)}");
            CollectionAssert.AreEqual(mUnits, bUnits.AsArray().ToArray(), $"iteration {i}: units");
            foreach (HealthUnit u in mUnits) Assert.That(u.Index, Is.InRange(0, NetIdAllocator.MaxCapacity - 1));
        }
    }

    // ---- the client stores ----

    [Test]
    public void UnitStore_MutatedBatchesAreRefusedOrApplied_NeverThrown()
    {
        const int idCapacity = 4096;
        var rng = new System.Random(4404);
        using var store = new ClientUnitStore(idCapacity, 0.05f);
        int refused = 0;
        for (int i = 0; i < 1500; i++)
        {
            byte[] input = i % 8 == 0 ? ValidUnitBatch(rng, idCapacity) : Mutate(ValidUnitBatch(rng, idCapacity), rng);
            bool ok = true;
            Assert.DoesNotThrow(() => ok = store.Apply(new ArraySegment<byte>(input), i), $"iteration {i}: {Hex(input)}");
            if (!ok) refused++;
            if (i % 50 == 0) store.PredictAll(i * 0.05f); // the prediction job then reads whatever the batches left
        }
        Assert.That(refused, Is.InRange(1, 1500 - 150), "mutated batches are both refused and applied");
    }

    [Test]
    public void Combat_MutatedNewsIsRefusedOrApplied_NeverThrown()
    {
        var rng = new System.Random(4405);
        var combat = new ClientCombat();
        int applied = 0;
        for (int i = 0; i < Iterations; i++)
        {
            var news = new CombatNews { Tick = rng.Next(0, 1 << 20) };
            for (int k = rng.Next(0, 12); k > 0; k--) news.Statuses.Add(new StatusEntry { Index = rng.Next(0, 4096), Bits = (byte)rng.Next(256) });
            for (int k = rng.Next(0, 4); k > 0; k--)
                news.Leaps.Add(new LeapEntry { Index = rng.Next(0, 4096), From = Tile(rng), To = Tile(rng), Start = rng.Next(0, 1000), End = rng.Next(1000, 2000) });
            for (int k = rng.Next(0, 4); k > 0; k--) news.Aims.Add(new AimEntry { Index = rng.Next(0, 4096), TargetId = rng.Next(1, 1 << 24) });
            for (int k = rng.Next(0, 4); k > 0; k--)
                news.Shells.Add(new ShellEntry { Full = rng.Next(2) == 0, From = Tile(rng), To = Tile(rng), Launch = rng.Next(0, 1000), Land = rng.Next(1000, 2000) });
            var writer = new NetworkWriter();
            CombatCodec.Write(writer, news);
            byte[] input = i % 10 == 0 ? writer.ToArray() : Mutate(writer.ToArray(), rng);
            bool ok = false;
            Assert.DoesNotThrow(() => ok = combat.Apply(new ArraySegment<byte>(input), quiet: true), $"iteration {i}: {Hex(input)}");
            if (ok) applied++;
        }
        Assert.That(applied, Is.InRange(Iterations / 20, Iterations - 1), "mutated news is both applied and refused");
    }

    [Test]
    public void Buildings_MutatedRecordsAreRefusedOrApplied_NeverThrown()
    {
        var rng = new System.Random(4406);
        var buildings = new ClientBuildings();
        BuildingType[] types = (BuildingType[])Enum.GetValues(typeof(BuildingType));
        int applied = 0;
        for (int i = 0; i < Iterations; i++)
        {
            var writer = new NetworkWriter();
            for (int k = rng.Next(1, 6); k > 0; k--)
            {
                int id = rng.Next(1, 1 << 24);
                BuildingCodec.WriteEnter(writer, new BuildingView { Id = id, OwnerId = rng.Next(1, 9), Type = types[rng.Next(types.Length)],
                    Anchor = new int2(rng.Next(0, 1024), rng.Next(0, 1024)), Rotation = (byte)rng.Next(0, 4), Health = rng.Next(1, 3000), MaxHealth = 3000,
                    Progress = (byte)rng.Next(0, 101) });
                BuildingRecord kind = (BuildingRecord)rng.Next(0, 8);
                BuildingCodec.WriteId(writer, kind, id, rng.Next(0, 101));
            }
            byte[] input = i % 10 == 0 ? writer.ToArray() : Mutate(writer.ToArray(), rng);
            bool ok = false;
            Assert.DoesNotThrow(() => ok = BuildingCodec.Apply(buildings, new ArraySegment<byte>(input)), $"iteration {i}: {Hex(input)}");
            if (ok) applied++;
            if (buildings.Entries.Count > 4096) buildings = new ClientBuildings();
        }
        Assert.That(applied, Is.InRange(Iterations / 20, Iterations - 1), "mutated records are both applied and refused");
    }

    [Test]
    public void Fog_MutatedSnapshotsAreRefusedOrApplied_AndKeepTheGridWhole()
    {
        var rng = new System.Random(4407);
        var fog = new ClientFog();
        int applied = 0;
        for (int i = 0; i < 3000; i++)
        {
            int w = rng.Next(1, 48), h = rng.Next(1, 48);
            var seen = new byte[w * h];
            var known = new byte[w * h];
            for (int c = 0; c < w * h; c++) { seen[c] = (byte)(rng.Next(3) == 0 ? 1 : 0); known[c] = (byte)(seen[c] == 1 || rng.Next(2) == 0 ? 1 : 0); }
            using var visible = new NativeArray<byte>(seen, Allocator.Temp);
            using var explored = new NativeArray<byte>(known, Allocator.Temp);
            var writer = new NetworkWriter();
            FogCodec.WriteSnapshot(writer, w, h, rng.Next(1, 5), visible, explored, 0);
            byte[] input = i % 10 == 0 ? writer.ToArray() : Mutate(writer.ToArray(), rng);
            bool ok = false;
            Assert.DoesNotThrow(() => ok = FogCodec.Apply(fog, new ArraySegment<byte>(input)), $"iteration {i}: {Hex(input)}");
            if (ok) applied++;
            if (fog.State != null) Assert.AreEqual(fog.Width * fog.Height, fog.State.Length, $"iteration {i}: the grid matches its size");
        }
        Assert.That(applied, Is.InRange(3000 / 20, 3000 - 1), "mutated snapshots are both applied and refused");
    }

    private static float2 Tile(System.Random rng) => new float2(rng.Next(0, 1024), rng.Next(0, 1024)) + 0.5f;

    /// <summary>The input as hex, for the failure message (the first 96 bytes).</summary>
    private static string Hex(byte[] bytes) =>
        bytes.Length + " bytes: " + BitConverter.ToString(bytes, 0, math.min(bytes.Length, 96)) + (bytes.Length > 96 ? "..." : "");
}

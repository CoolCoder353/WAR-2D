using System;
using System.Collections.Generic;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Client;
using WAR2D.Net.Replication;

/// <summary>The replication encoder, its messages, and the client store it feeds.</summary>
public class ReplicationTests
{
    private const int IdCapacity = 4096;
    private const float Dt = 0.05f;

    /// <summary>A unit SoA, routes, interest sets, an encoder and one client store per client.</summary>
    private sealed class Rig : IDisposable
    {
        public NativeArray<float2> Positions;
        public NativeArray<float> Health, MaxHealth, Speeds;
        public NativeArray<int> Owner, IdOf, IndexOfId;
        public NativeArray<byte> Type;
        public NativeList<int2> Attacks = new NativeList<int2>(16, Allocator.Persistent);
        public int Count;
        public RouteStore Routes = new RouteStore(IdCapacity, Allocator.Persistent);
        public InterestSets Interests;
        public ReplicationEncoder Encoder;
        public ClientUnitStore[] ClientStores;
        public EncoderConfig Config = new EncoderConfig
        {
            CorrectionThreshold = 0.25f, CorrectionInterval = 1, DeltaScale = 8, OffscreenThreshold = 2f, OffscreenInterval = 20, Dt = Dt,
        };

        public Rig(int capacity, int clients = 1)
        {
            Positions = new NativeArray<float2>(capacity, Allocator.Persistent);
            Health = new NativeArray<float>(capacity, Allocator.Persistent);
            MaxHealth = new NativeArray<float>(capacity, Allocator.Persistent);
            Owner = new NativeArray<int>(capacity, Allocator.Persistent);
            IdOf = new NativeArray<int>(capacity, Allocator.Persistent);
            IndexOfId = new NativeArray<int>(IdCapacity, Allocator.Persistent);
            Type = new NativeArray<byte>(capacity, Allocator.Persistent);
            Speeds = new NativeArray<float>(new[] { 0f, 5f }, Allocator.Persistent);
            Interests = new InterestSets(clients, IdCapacity, Allocator.Persistent);
            Encoder = new ReplicationEncoder(Config, Interests, Routes, IdCapacity, Allocator.Persistent);
            ClientStores = new ClientUnitStore[clients];
            for (int c = 0; c < clients; c++) ClientStores[c] = new ClientUnitStore(IdCapacity, Dt);
            UnitSpeeds.Reset();
        }

        public static int Id(int index, int generation = 1) => (generation << NetIdAllocator.IndexBits) | index;

        public int Add(int index, int owner, float2 position, int generation = 1)
        {
            int slot = Count++;
            Positions[slot] = position;
            Health[slot] = 100f;
            MaxHealth[slot] = 100f;
            Owner[slot] = owner;
            IdOf[slot] = Id(index, generation);
            IndexOfId[index] = slot;
            Type[slot] = 1;
            return slot;
        }

        public ReplicationInput Input => new ReplicationInput
        {
            Count = Count, Positions = Positions, Health = Health, MaxHealth = MaxHealth, OwnerId = Owner,
            Type = Type, IdOf = IdOf, IndexOfId = IndexOfId, SpeedByType = Speeds,
        };

        /// <summary>Builds interest, encodes the tick, and applies each client's bytes to its store.</summary>
        public void Tick(int tick)
        {
            Interests.Build(Input);
            Encoder.EncodeAll(tick, Input, Attacks.AsArray());
            for (int c = 0; c < ClientStores.Length; c++)
            {
                var (reliable, _, unreliable, _) = Encoder.Output(c);
                Assert.IsTrue(ClientStores[c].Apply(new ArraySegment<byte>(reliable.AsArray().ToArray()), tick));
                Assert.IsTrue(ClientStores[c].Apply(new ArraySegment<byte>(unreliable.AsArray().ToArray()), tick));
            }
        }

        public void Dispose()
        {
            Positions.Dispose(); Health.Dispose(); MaxHealth.Dispose(); Owner.Dispose(); IdOf.Dispose(); IndexOfId.Dispose();
            Type.Dispose(); Speeds.Dispose(); Attacks.Dispose(); Routes.Dispose(); Interests.Dispose(); Encoder.Dispose();
            foreach (ClientUnitStore s in ClientStores) s.Dispose();
        }
    }

    private static NetworkReader Reader(NativeList<byte> bytes) => new NetworkReader(bytes.AsArray().ToArray());

    [Test]
    public void VarIntRoundTripsAndRejectsLongValues()
    {
        var writer = new NetworkWriter();
        foreach (uint v in new uint[] { 0, 1, 127, 128, 16383, 16384, uint.MaxValue }) VarInt.Write(writer, v);
        foreach (int v in new[] { 0, -1, 1, -64, 63, int.MinValue, int.MaxValue }) VarInt.WriteInt(writer, v);
        var reader = new NetworkReader(writer.ToArray());
        foreach (uint v in new uint[] { 0, 1, 127, 128, 16383, 16384, uint.MaxValue }) Assert.AreEqual(v, VarInt.Read(reader));
        foreach (int v in new[] { 0, -1, 1, -64, 63, int.MinValue, int.MaxValue }) Assert.AreEqual(v, VarInt.ReadInt(reader));
        Assert.Throws<System.IO.EndOfStreamException>(() => VarInt.Read(new NetworkReader(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })));
    }

    [Test]
    public void MessagesRoundTrip()
    {
        using var bytes = new NativeList<byte>(256, Allocator.Temp);
        var sink = new NativeSink(bytes);
        using var tiles = new NativeArray<int2>(new[] { new int2(5, 5), new int2(7, 5), new int2(400, 9) }, Allocator.Temp);
        using var enters = new NativeArray<EnterUnit>(new[] { new EnterUnit { Id = Rig.Id(3, 7), Type = 1, OwnerId = 42, X = 80, Y = 96, Health = 55, First = 0, Count = 3 } }, Allocator.Temp);
        Messages.EncodeEnter(ref sink, tiles, enters);
        using var corrections = new NativeArray<CorrectionUnit>(new[] { new CorrectionUnit { Index = 3, DX = -5, DY = 300, Resume = 2, Speed = 64 }, new CorrectionUnit { Index = 9, DX = 1, DY = 0, Resume = 0, Speed = 0 } }, Allocator.Temp);
        Messages.EncodeCorrection(ref sink, 1234, 8, corrections);
        using var leaves = new NativeArray<LeaveUnit>(new[] { new LeaveUnit { Index = 3, Reason = LeaveReason.Died } }, Allocator.Temp);
        Messages.EncodeLeave(ref sink, leaves);
        using var attacks = new NativeArray<AttackEvent>(new[] { new AttackEvent { AttackerIndex = 9, TargetId = Rig.Id(2) }, new AttackEvent { AttackerIndex = 4, TargetId = 77 } }, Allocator.Temp);
        Messages.EncodeAttack(ref sink, attacks);

        NetworkReader reader = Reader(bytes);
        var e = new List<EnterUnit>(); var w = new List<int2>();
        Messages.DecodeEnter(reader, e, w);
        Assert.AreEqual(Rig.Id(3, 7), e[0].Id);
        Assert.AreEqual(42, e[0].OwnerId);
        Assert.AreEqual(55, e[0].Health);
        CollectionAssert.AreEqual(new[] { new int2(5, 5), new int2(7, 5), new int2(400, 9) }, w);
        var c = new List<CorrectionUnit>();
        Assert.AreEqual(1234, Messages.DecodeCorrection(reader, c, out byte scale));
        Assert.AreEqual(8, scale);
        Assert.AreEqual(300, c[0].DY);
        Assert.AreEqual(-5, c[0].DX);
        Assert.AreEqual(9, c[1].Index);
        var l = new List<LeaveUnit>();
        Messages.DecodeLeave(reader, l);
        Assert.AreEqual(LeaveReason.Died, l[0].Reason);
        var a = new List<AttackEvent>();
        Messages.DecodeAttack(reader, a);
        Assert.AreEqual(4, a[1].AttackerIndex);
        Assert.AreEqual(77, a[1].TargetId);
        Assert.AreEqual(0, reader.Remaining);
    }

    [Test]
    public void ClientPredictionMatchesTheServerBitForBit()
    {
        using var rig = new Rig(4);
        int slot = rig.Add(7, 1, new float2(10, 10));
        rig.Routes.Set(7, new float2(10, 10), new float2(40, 10), new float2(40, 30));
        rig.Interests.SetClient(0, 1, new int2(0, 0), new int2(100, 100));
        for (int tick = 1; tick < 200; tick++)
        {
            // The real unit walks slower than predicted and drifts, so corrections keep coming.
            rig.Positions[slot] += new float2(3.7f * Dt, 0.02f * math.sin(tick));
            rig.Tick(tick);
            float now = tick * Dt;
            Assert.AreEqual(rig.Encoder.PredictedPosition(0, 7), rig.ClientStores[0].PredictOne(7, now), $"tick {tick}");
        }
    }

    [Test]
    public void EncoderNeverSeesHiddenEnemies()
    {
        using var rig = new Rig(4);
        rig.Add(1, 1, new float2(10, 10));  // own
        rig.Add(2, 2, new float2(15, 15));  // enemy inside the view
        rig.Add(3, 2, new float2(60, 60));  // enemy outside the view
        rig.Interests.SetClient(0, 1, new int2(0, 0), new int2(20, 20));
        for (int tick = 1; tick < 30; tick++)
        {
            rig.Positions[2] += new float2(0.3f, 0f); // keep it busy: it would draw corrections if visible
            rig.Tick(tick);
            Assert.IsFalse(rig.ClientStores[0].IsKnown(3), $"tick {tick}");
        }
        Assert.IsTrue(rig.ClientStores[0].IsKnown(1));
        Assert.IsTrue(rig.ClientStores[0].IsKnown(2));
    }

    [Test]
    public void CorrectionsFitOneDatagramEach()
    {
        using var rig = new Rig(3000);
        for (int i = 0; i < 3000; i++) rig.Add(i + 1, 1, new float2(5 + i % 50, 5 + i / 50));
        rig.Interests.SetClient(0, 1, new int2(0, 0), new int2(200, 200));
        rig.Tick(1);
        for (int i = 0; i < 3000; i++) rig.Positions[i] += new float2(1.5f, -1f);
        rig.Tick(2);
        var (_, _, unreliable, sizes) = rig.Encoder.Output(0);
        Assert.Greater(sizes.Length, 1);
        int total = 0;
        foreach (int size in sizes) { Assert.LessOrEqual(size, WireLimits.CorrectionMessageLimit); total += size; }
        Assert.AreEqual(unreliable.Length, total);
    }

    [Test]
    public void OffscreenUnitsUseTheCoarserThreshold()
    {
        using var rig = new Rig(4);
        int inside = rig.Add(1, 1, new float2(10, 10));
        int outside = rig.Add(2, 1, new float2(80, 80)); // own, so allowed, but outside the view
        rig.Interests.SetClient(0, 1, new int2(0, 0), new int2(20, 20));
        rig.Encoder.SetView(0, new int2(0, 0), new int2(20, 20));
        rig.Tick(20);
        rig.Positions[inside] += new float2(1f, 0f);
        rig.Positions[outside] += new float2(1f, 0f);
        rig.Tick(40); // both checked: 40 is a multiple of the offscreen interval
        var store = rig.ClientStores[0];
        Assert.AreEqual(11f, store.PredictOne(1, 2f).x, 0.2f, "in view: corrected");
        Assert.AreEqual(80f, store.PredictOne(2, 2f).x, 0.2f, "off screen: a 1-tile drift is within 2 tiles");
    }

    [Test]
    public void SpeedCorrectionHoldsAStoppedUnit()
    {
        using var rig = new Rig(2);
        rig.Add(1, 1, new float2(10, 10));
        rig.Routes.Set(1, new float2(10, 10), new float2(60, 10));
        rig.Interests.SetClient(0, 1, new int2(0, 0), new int2(100, 100));
        for (int tick = 1; tick < 10; tick++) rig.Tick(tick); // the unit stands still
        float2 held = rig.ClientStores[0].PredictOne(1, 10 * Dt);
        Assert.AreEqual(held.x, rig.ClientStores[0].PredictOne(1, 100 * Dt).x, 0.3f, "a stopped unit stays put");
        Assert.AreEqual(10f, held.x, 0.3f);
    }

    [Test]
    public void ProjectedResumeTargetsNextSegmentAhead()
    {
        using var w = new NativeArray<float2>(new[] { new float2(0, 0), new float2(10, 0), new float2(10, 10) }, Allocator.Temp);
        Assert.AreEqual(2, MovementPrediction.ProjectedResume(w, 0, 3, 0, new float2(10, 3)));
    }

    [Test]
    public void AttackEventsOnlyForVisibleAttackers()
    {
        using var rig = new Rig(4, clients: 2);
        rig.Add(1, 1, new float2(10, 10));
        rig.Add(2, 2, new float2(60, 60));
        rig.Interests.SetClient(0, 1, new int2(0, 0), new int2(20, 20));
        rig.Encoder.SetView(0, new int2(0, 0), new int2(20, 20));
        rig.Interests.SetClient(1, 2, new int2(50, 50), new int2(70, 70));
        rig.Encoder.SetView(1, new int2(50, 50), new int2(70, 70));
        var seen = new List<int>[] { new List<int>(), new List<int>() };
        for (int c = 0; c < 2; c++) { int client = c; rig.ClientStores[c].Attack += (attacker, _) => seen[client].Add(attacker); }
        rig.Tick(1);
        rig.Attacks.Add(new int2(Rig.Id(2), Rig.Id(1))); // unit 2 (outside client 0's view) shoots unit 1
        rig.Tick(2);
        CollectionAssert.IsEmpty(seen[0]);
        CollectionAssert.AreEqual(new[] { 2 }, seen[1]);
    }

    [Test]
    public void ReusedIndexIsLeaveThenEnter()
    {
        using var rig = new Rig(2);
        int slot = rig.Add(5, 1, new float2(10, 10), generation: 1);
        rig.Interests.SetClient(0, 1, new int2(0, 0), new int2(100, 100));
        int died = 0;
        rig.ClientStores[0].Died += (_, _) => died++;
        rig.Tick(1);
        Assert.AreEqual(Rig.Id(5, 1), rig.ClientStores[0].IdOf(5));
        rig.IdOf[slot] = Rig.Id(5, 2); // the unit died and a new one took its index
        rig.Positions[slot] = new float2(30, 30);
        rig.Tick(2);
        Assert.AreEqual(Rig.Id(5, 2), rig.ClientStores[0].IdOf(5));
        Assert.AreEqual(1, died);
        Assert.AreEqual(30f, rig.ClientStores[0].PredictOne(5, 2 * Dt).x, 0.1f);
    }

    [Test]
    public void MalformedPayloadsNeverThrow()
    {
        using var store = new ClientUnitStore(IdCapacity, Dt);
        var rng = new System.Random(5);
        for (int i = 0; i < 1000; i++)
        {
            var bytes = new byte[rng.Next(0, 64)];
            rng.NextBytes(bytes);
            if (bytes.Length > 0) bytes[0] = (byte)rng.Next(0, 8);
            Assert.DoesNotThrow(() => store.Apply(new ArraySegment<byte>(bytes), i));
        }
    }
}

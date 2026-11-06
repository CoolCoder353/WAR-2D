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
        /// <summary>One fog grid per client (1 tile per cell).</summary>
        public NativeArray<byte> Visible;
        public const int FogSize = 512;
        public NativeList<int2> Attacks = new NativeList<int2>(16, Allocator.Persistent);
        /// <summary>Each attack's building target, parallel to <see cref="Attacks"/> (may stay empty).</summary>
        public NativeList<AttackTarget> Targets = new NativeList<AttackTarget>(16, Allocator.Persistent);
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
            Visible = new NativeArray<byte>(clients * FogSize * FogSize, Allocator.Persistent);
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
            Visible = Visible, FogCellSize = 1, FogW = FogSize, FogH = FogSize, FogCells = FogSize * FogSize,
        };

        /// <summary>
        /// Makes <paramref name="client"/> the player <paramref name="owner"/> (on a team of its own) whose
        /// fog grid sees exactly the inclusive tile box, and sets its camera to the same box.
        /// </summary>
        public void Watch(int client, int owner, int2 min, int2 max)
        {
            Interests.SetClient(client, owner, client);
            int start = client * FogSize * FogSize;
            for (int i = 0; i < FogSize * FogSize; i++) Visible[start + i] = 0;
            for (int y = math.max(0, min.y); y <= math.min(FogSize - 1, max.y); y++)
            for (int x = math.max(0, min.x); x <= math.min(FogSize - 1, max.x); x++)
                Visible[start + y * FogSize + x] = 1;
            Encoder.SetView(client, min, max);
        }

        /// <summary>Builds interest, encodes the tick, and applies each client's bytes to its store.</summary>
        public void Tick(int tick)
        {
            Interests.Build(Input);
            Encoder.EncodeAll(tick, Input, Attacks.AsArray(), Targets.AsArray());
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
            Type.Dispose(); Visible.Dispose(); Speeds.Dispose(); Attacks.Dispose(); Targets.Dispose(); Routes.Dispose(); Interests.Dispose(); Encoder.Dispose();
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
    public void AttackEventsCarryTheAttackerKind()
    {
        using var bytes = new NativeList<byte>(256, Allocator.Temp);
        var sink = new NativeSink(bytes);
        int building = Rig.Id(1000, 5);
        using var attacks = new NativeArray<AttackEvent>(new[]
        {
            new AttackEvent { AttackerIndex = 9, TargetId = Rig.Id(2) },
            new AttackEvent { AttackerKind = AttackerKinds.Building, AttackerId = building, TargetId = Rig.Id(3) },
            new AttackEvent { AttackerIndex = 4, TargetId = 77 }, // the unit delta chain skips the building event
            new AttackEvent { AttackerKind = AttackerKinds.Building, AttackerId = int.MaxValue, TargetId = 0 },
        }, Allocator.Temp);
        Messages.EncodeAttack(ref sink, attacks);
        NetworkReader reader = Reader(bytes);
        var a = new List<AttackEvent>();
        Messages.DecodeAttack(reader, a);
        Assert.AreEqual(0, reader.Remaining);
        Assert.AreEqual((AttackerKinds.Unit, 9, Rig.Id(2)), (a[0].AttackerKind, a[0].AttackerIndex, a[0].TargetId));
        Assert.AreEqual((AttackerKinds.Building, building, Rig.Id(3)), (a[1].AttackerKind, a[1].AttackerId, a[1].TargetId));
        Assert.AreEqual((AttackerKinds.Unit, 4, 77), (a[2].AttackerKind, a[2].AttackerIndex, a[2].TargetId));
        Assert.AreEqual((AttackerKinds.Building, int.MaxValue, 0), (a[3].AttackerKind, a[3].AttackerId, a[3].TargetId));
    }

    [Test]
    public void AttackTargetsRoundTripIncludingWallTiles()
    {
        using var bytes = new NativeList<byte>(256, Allocator.Temp);
        var sink = new NativeSink(bytes);
        using var attacks = new NativeArray<AttackEvent>(new[]
        {
            new AttackEvent { AttackerIndex = 3, TargetId = 0 },
            new AttackEvent { AttackerIndex = 5, TargetId = Rig.Id(1, 1) },
            new AttackEvent { AttackerIndex = 9, TargetId = AttackTargetIds.OfWall(4096) },
            new AttackEvent { AttackerKind = AttackerKinds.Building, AttackerId = Rig.Id(7), TargetId = int.MaxValue },
        }, Allocator.Temp);
        Messages.EncodeAttack(ref sink, attacks);
        NetworkReader reader = Reader(bytes);
        var a = new List<AttackEvent>();
        Messages.DecodeAttack(reader, a);
        Assert.AreEqual(0, reader.Remaining);
        CollectionAssert.AreEqual(new[] { 0, Rig.Id(1, 1), -4097, int.MaxValue }, a.ConvertAll(e => e.TargetId));
        Assert.IsTrue(AttackTargetIds.IsWall(a[2].TargetId));
        Assert.AreEqual(4096, AttackTargetIds.WallTile(a[2].TargetId));
    }

    [Test]
    public void ABuildingAttackWithIdZeroIsRefused()
    {
        var writer = new NetworkWriter();
        writer.WriteByte((byte)MessageType.Attack);
        VarInt.Write(writer, 1);
        VarInt.Write(writer, 1u); // building kind, id 0
        VarInt.Write(writer, 5);
        var reader = new NetworkReader(writer.ToArraySegment());
        Assert.Throws<System.IO.InvalidDataException>(() => Messages.DecodeAttack(reader, new List<AttackEvent>()));
    }

    [Test]
    public void ClientPredictionMatchesTheServerBitForBit()
    {
        using var rig = new Rig(4);
        int slot = rig.Add(7, 1, new float2(10, 10));
        rig.Routes.Set(7, new float2(10, 10), new float2(40, 10), new float2(40, 30));
        rig.Watch(0, 1, new int2(0, 0), new int2(100, 100));
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
    public void RouteWaypointsKeepTheirHalfTiles()
    {
        // Cell centres are x.5: whole tiles on the wire drew the client up to 0.7 tiles off the server (bug review 2026-11-05).
        using var rig = new Rig(4);
        int slot = rig.Add(7, 1, new float2(10.5f, 10.5f));
        rig.Routes.Set(7, new float2(10.5f, 10.5f), new float2(14.5f, 10.5f), new float2(14.5f, 13.3f));
        rig.Watch(0, 1, new int2(0, 0), new int2(100, 100));
        for (int tick = 1; tick < 60; tick++)
        {
            rig.Positions[slot] += new float2(1.9f * Dt, 0f);
            rig.Tick(tick);
            Assert.AreEqual(rig.Encoder.PredictedPosition(0, 7), rig.ClientStores[0].PredictOne(7, tick * Dt), $"tick {tick}");
        }
        float2 end = rig.ClientStores[0].PredictOne(7, 1000f);
        Assert.AreEqual(14.5f, end.x, 1e-4f);
        Assert.AreEqual(13.5f, end.y, 1e-4f, "the server snaps a waypoint to the half-tile grid it sends");
    }

    [Test]
    public void RouteWireIsExactOnHalfTiles()
    {
        Assert.AreEqual(new float2(511.5f, 0.5f), RouteWire.Snap(new float2(511.5f, 0.5f)));
        Assert.AreEqual(new int2(2047, 1), RouteWire.Encode(new float2(1023.5f, 0.5f)));
        Assert.AreEqual(new float2(3f, 7.5f), RouteWire.Snap(new float2(3.1f, 7.4f)));
    }

    [Test]
    public void MoveOrderRoutesCopyTheRunsTheyShareAndDecodeExactly()
    {
        // Protocol 22: units of one order walk one corridor, so later routes copy runs of earlier ones in the same message.
        int2[][] routes =
        {
            new[] { new int2(10, 10), new int2(20, 10), new int2(20, 30), new int2(40, 30), new int2(40, 50), new int2(60, 50) },
            new[] { new int2(12, 14), new int2(20, 14), new int2(20, 30), new int2(40, 30), new int2(40, 50), new int2(62, 50) },
            new[] { new int2(9, 6), new int2(20, 10), new int2(20, 30), new int2(40, 30), new int2(40, 50), new int2(60, 50) },
            new[] { new int2(300, 300), new int2(173, 300), new int2(200, 10) }, // shares nothing; an x step of -127 goes absolute
        };
        var tileList = new List<int2>();
        var unitList = new List<RouteUnit>();
        for (int r = 0; r < routes.Length; r++)
        {
            unitList.Add(new RouteUnit { Index = 3 + r * 4, First = tileList.Count, Count = routes[r].Length });
            tileList.AddRange(routes[r]);
        }
        var tiles = new NativeArray<int2>(tileList.ToArray(), Allocator.Temp);
        var units = new NativeArray<RouteUnit>(unitList.ToArray(), Allocator.Temp);
        var bytes = new NativeList<byte>(256, Allocator.Temp);
        var sink = new NativeSink(bytes);
        Messages.EncodeMoveOrder(ref sink, 5, tiles, units);

        int plain = 1 + VarInt.Size(5) + VarInt.Size((uint)units.Length), previous = 0;
        foreach (RouteUnit u in units) { plain += VarInt.Size((uint)(u.Index - previous)) + Messages.RouteSize(tiles, u.First, u.Count); previous = u.Index; }
        // Route 1 copies a run of 3 (6 bytes of steps for 4), route 2 a run of 5 (10 for 4): 8 bytes saved.
        Assert.AreEqual(plain - 8, bytes.Length, "the shared runs went as copy steps");

        var decoded = new List<RouteUnit>();
        var waypoints = new List<int2>();
        Assert.AreEqual(5, Messages.DecodeMoveOrder(Reader(bytes), decoded, waypoints));
        Assert.AreEqual(units.Length, decoded.Count);
        for (int r = 0; r < routes.Length; r++)
        {
            Assert.AreEqual(unitList[r].Index, decoded[r].Index);
            CollectionAssert.AreEqual(routes[r], waypoints.GetRange(decoded[r].First, decoded[r].Count), $"route {r}");
        }

        // The encoder packs messages by counted size: counting must give exactly the bytes written.
        var counting = new Messages.RouteCopies(Allocator.Temp);
        var writing = new Messages.RouteCopies(Allocator.Temp);
        var written = new NativeList<byte>(256, Allocator.Temp);
        var into = new NativeSink(written);
        foreach (RouteUnit u in units)
        {
            var counted = new CountingSink();
            int before = into.Position;
            counting.Write(ref counted, tiles, u.First, u.Count);
            writing.Write(ref into, tiles, u.First, u.Count);
            Assert.AreEqual(into.Position - before, counted.Position);
        }
        counting.Dispose(); writing.Dispose(); written.Dispose(); bytes.Dispose(); tiles.Dispose(); units.Dispose();
    }

    [Test]
    public void MoveOrderMessagesStayInsideTheBytesAndWaypointsTheDecoderAllows()
    {
        // 1,500 units on one long route: each copies its whole way from the one before, about 12 bytes for 200 waypoints, so the
        // waypoint cap, not the 12 KB chunk, ends a message (code review 2026-11-07).
        const int unitCount = 1500, length = 200;
        var tiles = new NativeArray<int2>(unitCount * length, Allocator.Temp);
        var units = new NativeArray<RouteUnit>(unitCount, Allocator.Temp);
        for (int u = 0; u < unitCount; u++)
        {
            units[u] = new RouteUnit { Index = u, First = u * length, Count = length };
            tiles[u * length] = new int2(u % 50, 0); // its own start
            for (int k = 1; k < length; k++) tiles[u * length + k] = new int2(100 + k, 100 + k % 2);
        }
        var copies = new Messages.RouteCopies(Allocator.Temp);
        int decodedUnits = 0, messages = 0;
        for (int start = 0; start < unitCount; messages++)
        {
            int end = Messages.MoveOrderChunkEnd(tiles, units, start, ref copies);
            Assert.Greater(end, start, "at least one route a message");
            var bytes = new NativeList<byte>(WireLimits.ReliableChunk, Allocator.Temp);
            var sink = new NativeSink(bytes);
            Messages.EncodeMoveOrder(ref sink, 3, tiles, units.GetSubArray(start, end - start));
            Assert.LessOrEqual(bytes.Length, WireLimits.ReliableChunk);
            var decoded = new List<RouteUnit>();
            var waypoints = new List<int2>();
            Messages.DecodeMoveOrder(Reader(bytes), decoded, waypoints);
            Assert.LessOrEqual(waypoints.Count, Messages.MaxMessageWaypoints);
            for (int i = 0; i < decoded.Count; i++)
                Assert.AreEqual(tiles[(start + i) * length + length - 1], waypoints[decoded[i].First + length - 1]);
            decodedUnits += decoded.Count;
            bytes.Dispose();
            start = end;
        }
        Assert.AreEqual(unitCount, decodedUnits);
        Assert.Greater(messages, 2, "the waypoint cap split them");
        copies.Dispose(); tiles.Dispose(); units.Dispose();
    }

    [Test]
    public void RoutesLongerThanAnyServerSendsAreRefusedBeforeTheUnitIsKnown()
    {
        // Four ids hold at most 4 x RouteMaintenance.MaxWaypoints (1,024) live waypoints: an Enter with 1,100 is a hostile host's.
        var store = new ClientUnitStore(4, Dt);
        var tiles = new NativeArray<int2>(1100, Allocator.Temp);
        for (int i = 0; i < tiles.Length; i++) tiles[i] = new int2(10 + i % 2, 10 + i % 3);
        var enter = new NativeArray<EnterUnit>(new[]
        {
            new EnterUnit { Id = (1 << NetIdAllocator.IndexBits) | 1, Type = (byte)UnitType.Tank, OwnerId = 1, X = 100, Y = 100, Health = 100, First = 0, Count = tiles.Length },
        }, Allocator.Temp);
        var bytes = new NativeList<byte>(4096, Allocator.Temp);
        var sink = new NativeSink(bytes);
        Messages.EncodeEnter(ref sink, tiles, enter);
        Assert.IsFalse(store.Apply(new ArraySegment<byte>(bytes.AsArray().ToArray()), 1), "refused, not thrown");
        Assert.AreEqual(0, store.Count, "and the unit isn't half known");
        store.Dispose(); bytes.Dispose(); enter.Dispose(); tiles.Dispose();
    }

    private static byte[] EnterOne(int index, params float2[] route)
    {
        var tiles = new NativeArray<int2>(route.Length, Allocator.Temp);
        for (int i = 0; i < route.Length; i++) tiles[i] = RouteWire.Encode(route[i]);
        var enter = new NativeArray<EnterUnit>(new[]
        {
            new EnterUnit { Id = (1 << NetIdAllocator.IndexBits) | index, Type = (byte)UnitType.Tank, OwnerId = 1, X = Quantise.Encode(route[0].x), Y = Quantise.Encode(route[0].y), Health = 100, First = 0, Count = route.Length },
        }, Allocator.Temp);
        var bytes = new NativeList<byte>(256, Allocator.Temp);
        var sink = new NativeSink(bytes);
        Messages.EncodeEnter(ref sink, tiles, enter);
        byte[] result = bytes.AsArray().ToArray();
        bytes.Dispose(); enter.Dispose(); tiles.Dispose();
        return result;
    }

    private static byte[] Order(int tick, params (int index, float2[] route)[] units)
    {
        var tileList = new List<int2>();
        var unitList = new List<RouteUnit>();
        foreach (var (index, route) in units)
        {
            unitList.Add(new RouteUnit { Index = index, First = tileList.Count, Count = route.Length });
            foreach (float2 p in route) tileList.Add(RouteWire.Encode(p));
        }
        var tiles = new NativeArray<int2>(tileList.ToArray(), Allocator.Temp);
        var list = new NativeArray<RouteUnit>(unitList.ToArray(), Allocator.Temp);
        var bytes = new NativeList<byte>(4096, Allocator.Temp);
        var sink = new NativeSink(bytes);
        Messages.EncodeMoveOrder(ref sink, tick, tiles, list);
        byte[] result = bytes.AsArray().ToArray();
        bytes.Dispose(); list.Dispose(); tiles.Dispose();
        return result;
    }

    [Test]
    public void AUnitOrderedTwiceInOneMessageEndsOnTheSecondRoute()
    {
        // ApplyRoutesJob applies a message's routes in order, as the managed loop did (code review 2026-11-07).
        var store = new ClientUnitStore(16, Dt);
        Assert.IsTrue(store.Apply(new ArraySegment<byte>(EnterOne(3, new float2(10.5f, 10.5f))), 1));
        float2[] first = { new float2(10.5f, 10.5f), new float2(20.5f, 10.5f) }, second = { new float2(10.5f, 10.5f), new float2(10.5f, 30.5f) };
        Assert.IsTrue(store.Apply(new ArraySegment<byte>(Order(2, (3, first), (3, second))), 2));
        var route = new List<float2>();
        Assert.IsTrue(store.CopyRoute(3, route));
        CollectionAssert.AreEqual(second, route);
        store.Dispose();
    }

    [Test]
    public void AMoveOrderPastTheRouteCapChangesNothing()
    {
        // Four ids hold at most 1,024 live waypoints: a MoveOrder with a 1,100-waypoint route is refused before any route changes.
        var store = new ClientUnitStore(4, Dt);
        float2[] start = { new float2(10.5f, 10.5f), new float2(12.5f, 10.5f) };
        Assert.IsTrue(store.Apply(new ArraySegment<byte>(EnterOne(1, start)), 1));
        float2 before = store.PredictOne(1, 0.2f);
        var longRoute = new float2[1100];
        for (int i = 0; i < longRoute.Length; i++) longRoute[i] = new float2(10.5f + i % 2, 10.5f + i % 3);
        Assert.IsFalse(store.Apply(new ArraySegment<byte>(Order(2, (1, longRoute))), 2), "refused, not thrown");
        var route = new List<float2>();
        Assert.IsTrue(store.CopyRoute(1, route));
        CollectionAssert.AreEqual(start, route, "the old route stays");
        Assert.AreEqual(before, store.PredictOne(1, 0.2f), "and so does the prediction");
        Assert.AreEqual(1, store.Count);
        store.Dispose();
    }

    [Test]
    public void CopyStepsThatReachOutsideTheirMessageAreRefused()
    {
        // A MoveOrder of two routes: (10,10) (12,10) (14,10), then four waypoints from (8,10) whose last three copy (back, start, run).
        NativeList<byte> Order(uint back, uint start, uint run)
        {
            var bytes = new NativeList<byte>(64, Allocator.Temp);
            var sink = new NativeSink(bytes);
            sink.WriteByte((byte)MessageType.MoveOrder);
            VarInt.Write(ref sink, 9);
            VarInt.Write(ref sink, 2);
            VarInt.Write(ref sink, 1);
            VarInt.Write(ref sink, 3);
            sink.WriteUShort(10); sink.WriteUShort(10);
            sink.WriteByte(2); sink.WriteByte(0);
            sink.WriteByte(2); sink.WriteByte(0);
            VarInt.Write(ref sink, 1);
            VarInt.Write(ref sink, 4);
            sink.WriteUShort(8); sink.WriteUShort(10);
            sink.WriteByte(Messages.CopyMarker);
            VarInt.Write(ref sink, back); VarInt.Write(ref sink, start); VarInt.Write(ref sink, run);
            return bytes;
        }
        var units = new List<RouteUnit>();
        var waypoints = new List<int2>();
        Messages.DecodeMoveOrder(Reader(Order(1, 0, 3)), units, waypoints);
        CollectionAssert.AreEqual(new[] { new int2(8, 10), new int2(10, 10), new int2(12, 10), new int2(14, 10) }, waypoints.GetRange(units[1].First, 4));
        Assert.Throws<System.IO.InvalidDataException>(() => Messages.DecodeMoveOrder(Reader(Order(2, 0, 3)), new List<RouteUnit>(), new List<int2>()), "no route two back");
        Assert.Throws<System.IO.InvalidDataException>(() => Messages.DecodeMoveOrder(Reader(Order(1, 1, 3)), new List<RouteUnit>(), new List<int2>()), "past the source's end");
        Assert.Throws<System.IO.InvalidDataException>(() => Messages.DecodeMoveOrder(Reader(Order(1, 0, 4)), new List<RouteUnit>(), new List<int2>()), "past this route's count");
        Assert.Throws<System.IO.InvalidDataException>(() => Messages.DecodeMoveOrder(Reader(Order(1, 0, 0)), new List<RouteUnit>(), new List<int2>()), "an empty copy");

        // Only routes of the copy's own message count: decoded after another message into the same lists, a lone route can't copy.
        var lone = new NativeList<byte>(32, Allocator.Temp);
        var l = new NativeSink(lone);
        l.WriteByte((byte)MessageType.MoveOrder);
        VarInt.Write(ref l, 9); VarInt.Write(ref l, 1); VarInt.Write(ref l, 5); VarInt.Write(ref l, 4);
        l.WriteUShort(8); l.WriteUShort(10);
        l.WriteByte(Messages.CopyMarker); VarInt.Write(ref l, 1); VarInt.Write(ref l, 0); VarInt.Write(ref l, 3);
        Assert.Throws<System.IO.InvalidDataException>(() => Messages.DecodeMoveOrder(Reader(lone), units, waypoints), "a copy into the message before");

        // An Enter's route has no earlier routes to copy.
        var enter = new NativeList<byte>(16, Allocator.Temp);
        var e = new NativeSink(enter);
        VarInt.Write(ref e, 2);
        e.WriteUShort(1); e.WriteUShort(1);
        e.WriteByte(Messages.CopyMarker); e.WriteByte(1); e.WriteByte(0); e.WriteByte(1);
        Assert.Throws<System.IO.InvalidDataException>(() => Messages.ReadRoute(Reader(enter), new List<int2>()));
    }

    [Test]
    public void EncoderNeverSeesHiddenEnemies()
    {
        using var rig = new Rig(4);
        rig.Add(1, 1, new float2(10, 10));  // own
        rig.Add(2, 2, new float2(15, 15));  // enemy inside the view
        rig.Add(3, 2, new float2(60, 60));  // enemy outside the view
        rig.Watch(0, 1, new int2(0, 0), new int2(20, 20));
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
        rig.Watch(0, 1, new int2(0, 0), new int2(200, 200));
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
        rig.Watch(0, 1, new int2(0, 0), new int2(20, 20));
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
        rig.Watch(0, 1, new int2(0, 0), new int2(100, 100));
        for (int tick = 1; tick < 10; tick++) rig.Tick(tick); // the unit stands still
        float2 held = rig.ClientStores[0].PredictOne(1, 10 * Dt);
        Assert.AreEqual(held.x, rig.ClientStores[0].PredictOne(1, 100 * Dt).x, 0.3f, "a stopped unit stays put");
        Assert.AreEqual(10f, held.x, 0.3f);
    }

    /// <summary>Owner report (2026-10-10): movement looked jumpy. A correction moves the prediction at once but the drawn unit glides.</summary>
    [Test]
    public void CorrectionsGlideInTheDrawnPositions()
    {
        using var rig = new Rig(2);
        rig.Add(1, 1, new float2(10, 10));
        rig.Routes.Set(1, new float2(10, 10), new float2(60, 10));
        rig.Watch(0, 1, new int2(0, 0), new int2(100, 100));
        ClientUnitStore store = rig.ClientStores[0];
        rig.Tick(1); // Enter: the client predicts the unit setting off along its route
        store.Now = 4 * Dt;
        rig.Tick(4); // it stood still, so a correction pulls it back
        store.PredictAll(4 * Dt);
        Assert.AreEqual(10f, store.Predicted[0].x, 0.3f, "the prediction is corrected at once");
        Assert.Greater(store.Drawn[0].x, store.Predicted[0].x + 0.4f, "the drawn unit starts from where it was drawn");
        store.PredictAll(4 * Dt + 10 * ClientUnitStore.BlendSeconds);
        Assert.AreEqual(store.Predicted[0].x, store.Drawn[0].x, 1e-3f, "and glides onto the prediction");
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
        rig.Watch(0, 1, new int2(0, 0), new int2(20, 20));
        rig.Encoder.SetView(0, new int2(0, 0), new int2(20, 20));
        rig.Watch(1, 2, new int2(50, 50), new int2(70, 70));
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
    public void AttackOnAUnitInTheFogDoesNotNameIt()
    {
        using var rig = new Rig(4);
        rig.Add(2, 2, new float2(10, 10)); // a visible attacker
        rig.Add(3, 3, new float2(30, 10)); // its target, in client 0's fog
        rig.Add(4, 3, new float2(12, 10)); // a visible target
        rig.Watch(0, 1, new int2(0, 0), new int2(20, 20));
        var targets = new List<int>();
        rig.ClientStores[0].Attack += (_, target) => targets.Add(target);
        rig.Tick(1);
        rig.Attacks.Add(new int2(Rig.Id(2), Rig.Id(3)));
        rig.Attacks.Add(new int2(Rig.Id(2), Rig.Id(4)));
        rig.Tick(2);
        CollectionAssert.AreEqual(new[] { 0, Rig.Id(4) }, targets, "the shot is shown, the hidden target's id is not");
    }

    [Test]
    public void AttackOnABuildingNamesItOnlyToItsOwnerOrWhoSeesIt()
    {
        using var rig = new Rig(4);
        rig.Add(2, 2, new float2(10, 10));
        rig.Watch(0, 1, new int2(0, 0), new int2(20, 20));
        var targets = new List<int>();
        rig.ClientStores[0].Attack += (_, target) => targets.Add(target);
        rig.Tick(1);
        (int id, int owner, float2 at, byte hidden)[] buildings =
        {
            (Rig.Id(50), 3, new float2(30, 10), 0), // in the fog
            (Rig.Id(51), 3, new float2(11, 11), 0), // seen
            (Rig.Id(52), 1, new float2(40, 40), 0), // the client's own, in the fog
            (Rig.Id(53), 3, new float2(12, 12), 1), // a seen tile, but a hidden bomb
        };
        foreach (var b in buildings)
        {
            rig.Attacks.Add(new int2(Rig.Id(2), b.id));
            rig.Targets.Add(new AttackTarget { Id = b.id, OwnerId = b.owner, Position = b.at, Hidden = b.hidden });
        }
        rig.Tick(2);
        CollectionAssert.AreEqual(new[] { 0, Rig.Id(51), Rig.Id(52), 0 }, targets);
    }

    [Test]
    public void ReusedIndexIsLeaveThenEnter()
    {
        using var rig = new Rig(2);
        int slot = rig.Add(5, 1, new float2(10, 10), generation: 1);
        rig.Watch(0, 1, new int2(0, 0), new int2(100, 100));
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

    /// <summary>
    /// A rotation retrace of a unit that has only walked further along its route keeps the route (and its
    /// generation), so nothing is resent; a retrace that finds a different way ahead replaces it.
    /// </summary>
    [Test]
    public void RetraceAlongTheSameRouteIsNotResent()
    {
        const int cells = 16, width = 64, height = 32;
        var blockOf = new NativeArray<int>(2, Allocator.TempJob);
        var blocks = new NativeArray<byte>(2 * cells * cells, Allocator.TempJob);
        blockOf[0] = 0; blockOf[1] = 1;
        for (int b = 0; b < 2; b++)
        for (int y = 0; y < cells; y++)
        for (int x = 0; x < cells; x++)
            blocks[b * cells * cells + y * cells + x] = b == 1 && x == cells - 1 ? WAR2D.Pathing.FlowDirections.AtGoal : (byte)0; // east to the far column
        var positions = new NativeArray<float2>(1, Allocator.TempJob);
        var health = new NativeArray<float>(1, Allocator.TempJob);
        var idOf = new NativeArray<int>(1, Allocator.TempJob);
        var orderSlot = new NativeArray<int>(1, Allocator.TempJob);
        health[0] = 1f; idOf[0] = 1; orderSlot[0] = 0;
        var routes = new RouteStore(8, Allocator.TempJob);
        try
        {
            void Run(int tick) => new RouteMaintenance
            {
                Count = 1, Tick = tick, RetraceBudget = 100, Tolerance = 1f,
                Positions = positions, Health = health, IdOf = idOf, OrderSlot = orderSlot,
                BlockOf = blockOf, Blocks = blocks,
                SectorsX = 2, SectorsY = 1, SectorCount = 2, CellsPerSector = cells, CellSize = 2, Width = width, Height = height,
                Arena = routes.Arena, Start = routes.Start, RouteCount = routes.Count, Generation = routes.Generation,
                TracedOrder = routes.TracedOrder, TracedId = routes.TracedId, Counters = routes.Counters,
            }.Run();

            positions[0] = new float2(5f, 16f);
            Run(1);
            int generation = routes.GenerationOf(1);
            Assert.Greater(routes.CountOf(1), 1, "the unit has a route to the goal");

            positions[0] = new float2(20f, 16f);
            Run(RouteMaintenance.RetraceEvery - 1); // this tick's rotation retraces index 1
            Assert.AreEqual(generation, routes.GenerationOf(1), "further along the same route: not resent");

            for (int y = 0; y < cells; y++) blocks[cells * cells + y * cells + cells - 1] = 0;
            for (int y = 0; y < cells; y++) blocks[cells * cells + y * cells + cells - 4] = WAR2D.Pathing.FlowDirections.AtGoal; // the goal moved
            Run(2 * RouteMaintenance.RetraceEvery - 1);
            Assert.AreNotEqual(generation, routes.GenerationOf(1), "a different way ahead replaces the route");
        }
        finally
        {
            routes.Dispose();
            blockOf.Dispose(); blocks.Dispose(); positions.Dispose(); health.Dispose(); idOf.Dispose(); orderSlot.Dispose();
        }
    }

    /// <summary>A staircase of eight-way flow steps (a slope of one half) is sent as a few straight segments that stay within the tolerance.</summary>
    [Test]
    public void StaircaseRoutesAreSimplified()
    {
        const int cells = 16, size = 64, cellsAcross = 32;
        var blockOf = new NativeArray<int>(4, Allocator.TempJob);
        var blocks = new NativeArray<byte>(4 * cells * cells, Allocator.TempJob);
        for (int b = 0; b < 4; b++) blockOf[b] = b;
        for (int y = 0; y < cellsAcross; y++)
        for (int x = 0; x < cellsAcross; x++)
        {
            int block = y / cells * 2 + x / cells;
            byte d = 2 * y > x ? (byte)0 : (byte)1; // east above the line y = x/2, north-east below it
            if (x == cellsAcross - 1) d = WAR2D.Pathing.FlowDirections.AtGoal;
            blocks[block * cells * cells + y % cells * cells + x % cells] = d;
        }
        var positions = new NativeArray<float2>(1, Allocator.TempJob);
        var health = new NativeArray<float>(1, Allocator.TempJob);
        var idOf = new NativeArray<int>(1, Allocator.TempJob);
        var orderSlot = new NativeArray<int>(1, Allocator.TempJob);
        health[0] = 1f; idOf[0] = 1; orderSlot[0] = 0; positions[0] = new float2(1f, 1f);
        var routes = new RouteStore(8, Allocator.TempJob);
        try
        {
            new RouteMaintenance
            {
                Count = 1, Tick = 1, RetraceBudget = 100, Tolerance = 1f,
                Positions = positions, Health = health, IdOf = idOf, OrderSlot = orderSlot,
                BlockOf = blockOf, Blocks = blocks,
                SectorsX = 2, SectorsY = 2, SectorCount = 4, CellsPerSector = cells, CellSize = 2, Width = size, Height = size,
                Arena = routes.Arena, Start = routes.Start, RouteCount = routes.Count, Generation = routes.Generation,
                TracedOrder = routes.TracedOrder, TracedId = routes.TracedId, Counters = routes.Counters,
            }.Run();
            int first = routes.First(1), count = routes.CountOf(1);
            Assert.LessOrEqual(count, 5, "the staircase collapses to a few segments");
            Assert.AreEqual(cellsAcross - 1, (int)(routes.Waypoints[first + count - 1].x / 2), "the route still ends at the goal column");
            for (int k = first + 1; k < first + count - 1; k++)
            {
                float2 w = routes.Waypoints[k];
                Assert.LessOrEqual(math.abs(w.y - (w.x / 2f)), 3f, "kept waypoints stay near the traced line");
            }
        }
        finally
        {
            routes.Dispose();
            blockOf.Dispose(); blocks.Dispose(); positions.Dispose(); health.Dispose(); idOf.Dispose(); orderSlot.Dispose();
        }
    }
}

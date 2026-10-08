using System.Collections.Generic;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Spike;

/// <summary>
/// Correctness tests for the replication format, the interest sets and the encoder. Each test builds
/// its own tiny world - SoA arrays, a fog, a route store and an encoder - so the encoder's inputs are
/// exactly what the test placed, with no simulation in between.
/// </summary>
public class ReplicationTests
{
    private const int MapSize = 64;

    /// <summary>An all-floor map with a blocked border, for open-ground vision.</summary>
    private static SpikeMap OpenMap(int size)
    {
        var tiles = new NativeArray<byte>(size * size, Allocator.Persistent);
        for (int i = 0; i < tiles.Length; i++) tiles[i] = SpikeMap.Floor;
        for (int i = 0; i < size; i++)
        {
            tiles[i] = SpikeMap.Border;
            tiles[(size - 1) * size + i] = SpikeMap.Border;
            tiles[i * size] = SpikeMap.Border;
            tiles[i * size + size - 1] = SpikeMap.Border;
        }
        return new SpikeMap(size, size, tiles);
    }

    /// <summary>
    /// A replication world a test can move units around in: the SoA the encoder reads, a fog, one
    /// route per unit and the interest sets and encoder bound to them.
    /// </summary>
    private sealed class Rig : System.IDisposable
    {
        public readonly SpikeMap Map;
        public readonly FogSystem Fog;
        public NativeArray<float2> Positions;
        public NativeArray<float> Health;
        public NativeArray<byte> Player;
        public NativeArray<int> Ids;
        public NativeArray<int> IndexOfId;
        public readonly RouteStore Routes;
        public readonly InterestSets Interests;
        public readonly ReplicationEncoder Encoder;
        public readonly NetworkWriter Reliable = new NetworkWriter();
        public readonly NetworkWriter Unreliable = new NetworkWriter();
        public readonly int Capacity;

        private readonly Dictionary<int, int> slotOf = new Dictionary<int, int>();
        private readonly int idCapacity;
        private int used;

        public Rig(int capacity, int clients, int teams, int vision, EncoderConfig config)
        {
            Capacity = capacity;
            idCapacity = capacity + 16;
            Map = OpenMap(MapSize);
            Fog = new FogSystem(new FogConfig
            {
                Teams = teams, Hz = 5, UnitVision = vision, BuildingVision = vision,
            }, MapSize, MapSize, Map.Tiles, Allocator.Persistent);
            Positions = new NativeArray<float2>(capacity, Allocator.Persistent);
            Health = new NativeArray<float>(capacity, Allocator.Persistent);
            Player = new NativeArray<byte>(capacity, Allocator.Persistent);
            Ids = new NativeArray<int>(capacity, Allocator.Persistent);
            IndexOfId = new NativeArray<int>(idCapacity, Allocator.Persistent);
            for (int i = 0; i < idCapacity; i++) IndexOfId[i] = -1;

            Fog.SetUnits(Positions, Player, Health, capacity);
            Routes = new RouteStore(idCapacity, Allocator.Persistent);
            Interests = new InterestSets(clients, teams, idCapacity, Allocator.Persistent);
            Interests.SetUnits(Positions, Player, Health, Ids, capacity);
            Interests.SetFog(Fog);
            Interests.SetTileGrid(MapSize, MapSize);
            Encoder = new ReplicationEncoder(config, Interests, Routes, capacity, idCapacity, Allocator.Persistent);
            Encoder.SetUnits(Positions, Health, Player, Ids, IndexOfId, capacity);
            Encoder.SetFog(Fog);
        }

        /// <summary>Adds a unit to the next free slot, at the centre of <paramref name="tile"/>.</summary>
        public int Add(int id, byte player, int2 tile, float health = 100f)
        {
            int slot = used++;
            Positions[slot] = (float2)tile + new float2(0.5f);
            Health[slot] = health;
            Player[slot] = player;
            Ids[slot] = id;
            IndexOfId[id] = slot;
            slotOf[id] = slot;
            var route = new NativeList<int2>(1, Allocator.Temp);
            route.Add(tile);
            Routes.Set(id, route);
            route.Dispose();
            return slot;
        }

        public int Slot(int id) => slotOf[id];

        public void Move(int id, float2 position) => Positions[Slot(id)] = position;

        public void Hurt(int id, float health) => Health[Slot(id)] = health;

        /// <summary>Replaces a unit's route with a traced tile polyline.</summary>
        public void Route(int id, params int2[] tiles)
        {
            var route = new NativeList<int2>(tiles.Length, Allocator.Temp);
            for (int i = 0; i < tiles.Length; i++) route.Add(tiles[i]);
            Routes.Set(id, route);
            route.Dispose();
        }

        /// <summary>The fog update plus interest build the encoder expects before an encode.</summary>
        public void Refresh()
        {
            Interests.Build();
        }

        /// <summary>Encodes one tick for one client and returns (reliable bytes, unreliable bytes).</summary>
        public (int reliable, int unreliable) Encode(int client, int tick)
        {
            Reliable.Position = 0;
            Unreliable.Position = 0;
            Encoder.Encode(client, tick, Reliable, Unreliable);
            return (Reliable.Position, Unreliable.Position);
        }

        public NetworkReader ReliableReader() => new NetworkReader(Reliable.ToArray());

        public NetworkReader UnreliableReader() => new NetworkReader(Unreliable.ToArray());

        public void Dispose()
        {
            Encoder.Dispose();
            Interests.Dispose();
            Routes.Dispose();
            Fog.Dispose();
            Positions.Dispose();
            Health.Dispose();
            Player.Dispose();
            Ids.Dispose();
            IndexOfId.Dispose();
            Map.Dispose();
        }
    }

    /// <summary>One decoded message's type, its byte length and the unit ids it mentioned.</summary>
    private static List<(SpikeMessageType Type, int Bytes, int Id, int Count)> Decode(NetworkReader reader)
    {
        var messages = new List<(SpikeMessageType, int, int, int)>();
        while (reader.Remaining > 0)
        {
            int start = reader.Position;
            SpikeMessageType type = SpikeMessages.PeekType(reader);
            switch (type)
            {
                case SpikeMessageType.MoveOrder:
                {
                    MoveOrderMessage message = SpikeMessages.DecodeMoveOrder(reader, Allocator.Temp);
                    for (int i = 0; i < message.Units.Length; i++)
                        messages.Add((type, reader.Position - start, message.Units[i].Id, message.Waypoints.Length));
                    message.Units.Dispose();
                    message.Waypoints.Dispose();
                    break;
                }
                case SpikeMessageType.Correction:
                {
                    CorrectionMessage message = SpikeMessages.DecodeCorrection(reader, Allocator.Temp);
                    for (int i = 0; i < message.Units.Length; i++)
                        messages.Add((type, reader.Position - start, message.Units[i].Id, message.Units.Length));
                    message.Units.Dispose();
                    break;
                }
                case SpikeMessageType.Health:
                {
                    HealthMessage message = SpikeMessages.DecodeHealth(reader, Allocator.Temp);
                    for (int i = 0; i < message.Units.Length; i++)
                        messages.Add((type, reader.Position - start, message.Units[i].Id, message.Units.Length));
                    message.Units.Dispose();
                    break;
                }
                case SpikeMessageType.Enter:
                {
                    EnterMessage message = SpikeMessages.DecodeEnter(reader, Allocator.Temp);
                    for (int i = 0; i < message.Units.Length; i++)
                        messages.Add((type, reader.Position - start, message.Units[i].Id, message.Units.Length));
                    message.Units.Dispose();
                    message.Waypoints.Dispose();
                    break;
                }
                case SpikeMessageType.Leave:
                {
                    LeaveMessage message = SpikeMessages.DecodeLeave(reader, Allocator.Temp);
                    for (int i = 0; i < message.Units.Length; i++)
                        messages.Add((type, reader.Position - start, message.Units[i].Id, message.Units.Length));
                    message.Units.Dispose();
                    break;
                }
                case SpikeMessageType.Explosion:
                {
                    ExplosionMessage message = SpikeMessages.DecodeExplosion(reader, Allocator.Temp);
                    messages.Add((type, reader.Position - start, -1, message.X.Length));
                    message.X.Dispose();
                    message.Y.Dispose();
                    break;
                }
                case SpikeMessageType.FogDelta:
                {
                    FogDeltaMessage message = SpikeMessages.DecodeFogDelta(reader, Allocator.Temp);
                    messages.Add((type, reader.Position - start, -1, message.Cells.Length));
                    message.Cells.Dispose();
                    break;
                }
                default:
                    Assert.Fail($"unknown message type {(byte)type}");
                    break;
            }
        }
        return messages;
    }

    /// <summary>Counts the messages that mention an id, by type.</summary>
    private static int Mentions(List<(SpikeMessageType Type, int Bytes, int Id, int Count)> messages,
        SpikeMessageType type, int id)
    {
        int count = 0;
        foreach ((SpikeMessageType kind, int _, int mentioned, int _) in messages)
            if (kind == type && mentioned == id) count++;
        return count;
    }

    // ---- Step 1: the failing tests ----

    /// <summary>Every varint the plan lists round-trips, with the exact byte lengths it fixes.</summary>
    [Test]
    public void VarIntRoundTrips()
    {
        uint[] values = { 0, 1, 127, 128, 16383, 16384, uint.MaxValue };
        int[] sizes = { 1, 1, 1, 2, 2, 3, 5 };

        for (int i = 0; i < values.Length; i++)
        {
            var writer = new NetworkWriter();
            VarInt.Write(writer, values[i]);
            Assert.AreEqual(sizes[i], writer.Position, $"value {values[i]} must take {sizes[i]} bytes");
            Assert.AreEqual(sizes[i], VarInt.Size(values[i]), "Size must agree with the encoder");

            var reader = new NetworkReader(writer.ToArray());
            Assert.AreEqual(values[i], VarInt.Read(reader), $"value {values[i]} must round-trip");
            Assert.AreEqual(0, reader.Remaining);
        }

        var stream = new NetworkWriter();
        for (int i = 0; i < values.Length; i++) VarInt.Write(stream, values[i]);
        var streamReader = new NetworkReader(stream.ToArray());
        for (int i = 0; i < values.Length; i++) Assert.AreEqual(values[i], VarInt.Read(streamReader));

        int[] signed = { 0, -1, 1, -64, 63, int.MinValue, int.MaxValue };
        foreach (int value in signed)
        {
            Assert.AreEqual(value, VarInt.UnZigZag(VarInt.ZigZag(value)), $"zigzag {value}");
            var writer = new NetworkWriter();
            VarInt.WriteInt(writer, value);
            Assert.AreEqual(VarInt.SizeInt(value), writer.Position);
            Assert.AreEqual(value, VarInt.ReadInt(new NetworkReader(writer.ToArray())));
        }
    }

    /// <summary>Every message type encodes and decodes to the same content.</summary>
    [Test]
    public void MessagesRoundTrip()
    {
        var waypoints = new NativeArray<int2>(8, Allocator.Temp);
        waypoints[0] = new int2(10, 12);
        waypoints[1] = new int2(14, 12);
        waypoints[2] = new int2(14, 18);
        waypoints[3] = new int2(300, 200); // far enough to need the 0x80 escape
        waypoints[4] = new int2(301, 199);
        waypoints[5] = new int2(305, 199);
        waypoints[6] = new int2(305, 210);
        waypoints[7] = new int2(305, 211);

        // MoveOrder: two units, one route each, ticks and ids delta-coded.
        var moveUnits = new NativeArray<RouteUnit>(2, Allocator.Temp);
        moveUnits[0] = new RouteUnit { Id = 7, First = 0, Count = 3 };
        moveUnits[1] = new RouteUnit { Id = 20003, First = 3, Count = 5 };
        var moveWriter = new NetworkWriter();
        SpikeMessages.Encode(moveWriter, new MoveOrderMessage
        {
            Tick = 1234, SpeedClass = 1, Waypoints = waypoints, Units = moveUnits,
        });
        var moveReader = new NetworkReader(moveWriter.ToArray());
        MoveOrderMessage move = SpikeMessages.DecodeMoveOrder(moveReader, Allocator.Temp);
        Assert.AreEqual(1234, move.Tick);
        Assert.AreEqual(1, move.SpeedClass);
        Assert.AreEqual(2, move.Units.Length);
        Assert.AreEqual(7, move.Units[0].Id);
        Assert.AreEqual(3, move.Units[0].Count);
        Assert.AreEqual(20003, move.Units[1].Id);
        Assert.AreEqual(5, move.Units[1].Count);
        Assert.AreEqual(8, move.Waypoints.Length);
        for (int i = 0; i < 8; i++) Assert.AreEqual(waypoints[i], move.Waypoints[i], $"waypoint {i}");
        Assert.AreEqual(moveWriter.Position, moveReader.Position, "the MoveOrder decoder must consume exactly the message");
        move.Units.Dispose();
        move.Waypoints.Dispose();

        // Correction.
        var correctionUnits = new NativeArray<CorrectionUnit>(2, Allocator.Temp);
        correctionUnits[0] = new CorrectionUnit { Id = 9, X = 160, Y = 33, Resume = 2 };
        correctionUnits[1] = new CorrectionUnit { Id = 40000, X = 65535, Y = 1, Resume = 0 };
        var correctionWriter = new NetworkWriter();
        SpikeMessages.Encode(correctionWriter, new CorrectionMessage { Tick = 999, Units = correctionUnits });
        var correctionReader = new NetworkReader(correctionWriter.ToArray());
        CorrectionMessage correction = SpikeMessages.DecodeCorrection(correctionReader, Allocator.Temp);
        Assert.AreEqual(999, correction.Tick);
        Assert.AreEqual(2, correction.Units.Length);
        Assert.AreEqual(9, correction.Units[0].Id);
        Assert.AreEqual(160, correction.Units[0].X);
        Assert.AreEqual(33, correction.Units[0].Y);
        Assert.AreEqual(2, correction.Units[0].Resume);
        Assert.AreEqual(40000, correction.Units[1].Id);
        Assert.AreEqual(65535, correction.Units[1].X);
        Assert.AreEqual(0, correction.Units[1].Resume);
        Assert.AreEqual(correctionWriter.Position, correctionReader.Position);
        correction.Units.Dispose();

        // Health.
        var healthUnits = new NativeArray<HealthUnit>(2, Allocator.Temp);
        healthUnits[0] = new HealthUnit { Id = 3, Health = 10 };
        healthUnits[1] = new HealthUnit { Id = 100000, Health = 100 };
        var healthWriter = new NetworkWriter();
        SpikeMessages.Encode(healthWriter, new HealthMessage { Units = healthUnits });
        var healthReader = new NetworkReader(healthWriter.ToArray());
        HealthMessage health = SpikeMessages.DecodeHealth(healthReader, Allocator.Temp);
        Assert.AreEqual(2, health.Units.Length);
        Assert.AreEqual(3, health.Units[0].Id);
        Assert.AreEqual(10, health.Units[0].Health);
        Assert.AreEqual(100000, health.Units[1].Id);
        Assert.AreEqual(100, health.Units[1].Health);
        Assert.AreEqual(healthWriter.Position, healthReader.Position);
        health.Units.Dispose();

        // Enter.
        var enterUnits = new NativeArray<EnterUnit>(2, Allocator.Temp);
        enterUnits[0] = new EnterUnit { Id = 11, Type = 0, Owner = 3, X = 48, Y = 96, Health = 55, First = 0, Count = 3 };
        enterUnits[1] = new EnterUnit { Id = 12, Type = 1, Owner = 7, X = 1, Y = 16383, Health = 0, First = 3, Count = 5 };
        var enterWriter = new NetworkWriter();
        SpikeMessages.Encode(enterWriter, new EnterMessage { Waypoints = waypoints, Units = enterUnits });
        var enterReader = new NetworkReader(enterWriter.ToArray());
        EnterMessage enter = SpikeMessages.DecodeEnter(enterReader, Allocator.Temp);
        Assert.AreEqual(2, enter.Units.Length);
        Assert.AreEqual(11, enter.Units[0].Id);
        Assert.AreEqual(3, enter.Units[0].Owner);
        Assert.AreEqual(48, enter.Units[0].X);
        Assert.AreEqual(96, enter.Units[0].Y);
        Assert.AreEqual(55, enter.Units[0].Health);
        Assert.AreEqual(3, enter.Units[0].Count);
        Assert.AreEqual(7, enter.Units[1].Owner);
        Assert.AreEqual(16383, enter.Units[1].Y);
        Assert.AreEqual(0, enter.Units[1].Health);
        for (int i = 0; i < 8; i++) Assert.AreEqual(waypoints[i], enter.Waypoints[i], $"enter waypoint {i}");
        Assert.AreEqual(enterWriter.Position, enterReader.Position);
        enter.Units.Dispose();
        enter.Waypoints.Dispose();

        // Leave.
        var leaveUnits = new NativeArray<LeaveUnit>(2, Allocator.Temp);
        leaveUnits[0] = new LeaveUnit { Id = 4, Reason = LeaveReason.Died };
        leaveUnits[1] = new LeaveUnit { Id = 77, Reason = LeaveReason.LeftVision };
        var leaveWriter = new NetworkWriter();
        SpikeMessages.Encode(leaveWriter, new LeaveMessage { Units = leaveUnits });
        var leaveReader = new NetworkReader(leaveWriter.ToArray());
        LeaveMessage leave = SpikeMessages.DecodeLeave(leaveReader, Allocator.Temp);
        Assert.AreEqual(2, leave.Units.Length);
        Assert.AreEqual(4, leave.Units[0].Id);
        Assert.AreEqual(LeaveReason.Died, leave.Units[0].Reason);
        Assert.AreEqual(77, leave.Units[1].Id);
        Assert.AreEqual(LeaveReason.LeftVision, leave.Units[1].Reason);
        Assert.AreEqual(leaveWriter.Position, leaveReader.Position);
        leave.Units.Dispose();

        // Explosion.
        var explosionX = new NativeArray<ushort>(2, Allocator.Temp);
        var explosionY = new NativeArray<ushort>(2, Allocator.Temp);
        explosionX[0] = 5; explosionY[0] = 6;
        explosionX[1] = 30000; explosionY[1] = 7;
        var explosionWriter = new NetworkWriter();
        SpikeMessages.Encode(explosionWriter, new ExplosionMessage { X = explosionX, Y = explosionY });
        var explosionReader = new NetworkReader(explosionWriter.ToArray());
        ExplosionMessage explosion = SpikeMessages.DecodeExplosion(explosionReader, Allocator.Temp);
        Assert.AreEqual(2, explosion.X.Length);
        Assert.AreEqual(5, explosion.X[0]);
        Assert.AreEqual(6, explosion.Y[0]);
        Assert.AreEqual(30000, explosion.X[1]);
        Assert.AreEqual(7, explosion.Y[1]);
        Assert.AreEqual(explosionWriter.Position, explosionReader.Position);
        explosion.X.Dispose();
        explosion.Y.Dispose();

        // FogDelta: ascending cells, sent as gaps.
        var cells = new NativeArray<int>(3, Allocator.Temp);
        cells[0] = 5;
        cells[1] = 9;
        cells[2] = 4000;
        var fogWriter = new NetworkWriter();
        SpikeMessages.Encode(fogWriter, new FogDeltaMessage { Cells = cells });
        var fogReader = new NetworkReader(fogWriter.ToArray());
        FogDeltaMessage fog = SpikeMessages.DecodeFogDelta(fogReader, Allocator.Temp);
        Assert.AreEqual(3, fog.Cells.Length);
        Assert.AreEqual(5, fog.Cells[0]);
        Assert.AreEqual(9, fog.Cells[1]);
        Assert.AreEqual(4000, fog.Cells[2]);
        Assert.AreEqual(fogWriter.Position, fogReader.Position);
        fog.Cells.Dispose();

        waypoints.Dispose();
        moveUnits.Dispose();
        correctionUnits.Dispose();
        healthUnits.Dispose();
        enterUnits.Dispose();
        leaveUnits.Dispose();
        explosionX.Dispose();
        explosionY.Dispose();
        cells.Dispose();
    }

    /// <summary>A thousand corrections split into messages that never cross the MTU's 1,194 bytes.</summary>
    [Test]
    public void CorrectionMessagesFitMtu()
    {
        const int units = 1000;
        var config = EncoderConfig.Defaults;
        config.CorrectionInterval = 1; // every unit is checked every tick, so one tick carries all of them
        using var rig = new Rig(units + 8, clients: 1, teams: 8, vision: 8, config);

        for (int i = 0; i < units; i++)
        {
            // Every unit holds at its own tile while its route says it is somewhere else, so its
            // prediction is off by far more than the threshold.
            rig.Add(id: i + 1, player: 0, tile: new int2(4, 4));
            rig.Move(i + 1, new float2(30f + i % 8, 30f));
            rig.Route(i + 1, new int2(4, 4));
        }

        rig.Refresh();
        (int burst, int _) = rig.Encode(0, 0); // the Enter burst; corrections start on the next check
        rig.Refresh();
        (int reliable, int unreliable) = rig.Encode(0, 1);

        Assert.Greater(burst, 0, "the Enter burst goes down the reliable channel");
        Assert.Greater(unreliable, 0, "the corrections go down the unreliable channel");

        var reader = rig.UnreliableReader();
        int decoded = 0, messages = 0, biggest = 0;
        while (reader.Remaining > 0)
        {
            int start = reader.Position;
            SpikeMessageType type = SpikeMessages.PeekType(reader);
            Assert.AreEqual(SpikeMessageType.Correction, type);
            CorrectionMessage message = SpikeMessages.DecodeCorrection(reader, Allocator.Temp);
            int size = reader.Position - start;
            biggest = math.max(biggest, size);
            messages++;
            decoded += message.Units.Length;
            message.Units.Dispose();
        }

        Assert.AreEqual(units, decoded, "every correction must be carried");
        Assert.Greater(messages, 1, "1,000 corrections cannot fit one MTU-sized message");
        Assert.LessOrEqual(biggest, SpikeWire.UnreliableMaxMessageSize,
            $"a correction message may not exceed {SpikeWire.UnreliableMaxMessageSize} bytes");
    }

    /// <summary>
    /// The structural guarantee: a unit the client's team cannot see never appears in its bytes, and
    /// walking into and out of vision produces exactly one Enter and one Leave.
    /// </summary>
    [Test]
    public void EncoderNeverSeesHiddenEnemies()
    {
        using var rig = new Rig(capacity: 8, clients: 2, teams: 8, vision: 8, EncoderConfig.Defaults);
        rig.Add(id: 1, player: 0, tile: new int2(10, 10));
        rig.Add(id: 2, player: 1, tile: new int2(40, 40)); // far outside the scout's vision
        rig.Add(id: 3, player: 1, tile: new int2(44, 44));

        rig.Fog.UpdateTeam(0);
        rig.Fog.UpdateTeam(1);
        var seen = new List<(SpikeMessageType Type, int Bytes, int Id, int Count)>();

        rig.Refresh();
        rig.Encode(0, 0);
        seen.AddRange(Decode(rig.ReliableReader()));
        seen.AddRange(Decode(rig.UnreliableReader()));
        Assert.AreEqual(1, Mentions(seen, SpikeMessageType.Enter, 1), "the client's own unit enters");
        Assert.AreEqual(0, Mentions(seen, SpikeMessageType.Enter, 2), "a hidden enemy must not enter");

        // The enemy walks into vision.
        rig.Move(2, new float2(12.5f, 10.5f));
        rig.Fog.UpdateTeam(0);
        rig.Refresh();
        rig.Encode(0, 2);
        var entered = Decode(rig.ReliableReader());
        var enteredUnreliable = Decode(rig.UnreliableReader());
        Assert.AreEqual(1, Mentions(entered, SpikeMessageType.Enter, 2), "walking into view is one Enter");
        Assert.AreEqual(0, Mentions(entered, SpikeMessageType.Leave, 2));
        seen.AddRange(entered);
        seen.AddRange(enteredUnreliable);

        // And back out.
        rig.Move(2, new float2(40.5f, 40.5f));
        rig.Fog.UpdateTeam(0);
        rig.Refresh();
        rig.Encode(0, 4);
        var departed = Decode(rig.ReliableReader());
        var departedUnreliable = Decode(rig.UnreliableReader());
        Assert.AreEqual(1, Mentions(departed, SpikeMessageType.Leave, 2), "walking out of view is one Leave");
        Assert.AreEqual(0, Mentions(departed, SpikeMessageType.Enter, 2));
        seen.AddRange(departed);
        seen.AddRange(departedUnreliable);

        // No message of any kind ever mentioned the hidden enemy except that pair.
        int total = 0;
        foreach ((SpikeMessageType type, int _, int id, int _) in seen)
        {
            if (id != 2) continue;
            total++;
            Assert.IsTrue(type == SpikeMessageType.Enter || type == SpikeMessageType.Leave,
                $"client 0 was sent a {type} for a unit it must not see");
        }
        Assert.AreEqual(2, total, "id 2 appears exactly twice: its Enter and its Leave");
        Assert.IsFalse(rig.Interests.IsAllowed(0, 2), "and it is not in the allowed set at the end");
    }

    /// <summary>
    /// The server's prediction is the client's: <see cref="PathFollowerJob"/> over the same route
    /// slice, speed and start time must produce the same bits.
    /// </summary>
    [Test]
    public void PredictionMatchesClient()
    {
        var config = EncoderConfig.Defaults;
        config.CorrectionInterval = 1;
        // The threshold is wide open: this test is about the prediction function, not the correction
        // rule, so every check must stay a plain route leg.
        config.CorrectionThreshold = 1000f;
        using var rig = new Rig(capacity: 4, clients: 1, teams: 8, vision: 8, config);
        int slot = rig.Add(id: 1, player: 0, tile: new int2(10, 10));
        rig.Route(1, new int2(10, 10), new int2(16, 10), new int2(16, 18));

        rig.Refresh();
        rig.Encode(0, 0);
        rig.Refresh();
        rig.Encode(0, 1);
        rig.Refresh();
        rig.Encode(0, 2);

        Assert.IsTrue(rig.Encoder.PredictedLeg(0, 1, out int first, out int count, out float startTime),
            "the unit is mid-route, so its prediction is a plain route leg");
        Assert.Greater(count, 1, "the route has more than one waypoint left");

        var waypointStart = new NativeArray<int>(2, Allocator.TempJob);
        var speed = new NativeArray<float>(1, Allocator.TempJob);
        var start = new NativeArray<float>(1, Allocator.TempJob);
        var positions = new NativeArray<float2>(1, Allocator.TempJob);
        var rotations = new NativeArray<float>(1, Allocator.TempJob);
        try
        {
            waypointStart[0] = first;
            waypointStart[1] = first + count;
            speed[0] = config.Speed;
            start[0] = startTime;
            new PathFollowerJob
            {
                Waypoints = rig.Routes.Waypoints,
                WaypointStart = waypointStart,
                Speed = speed,
                StartTime = start,
                Now = rig.Encoder.LastNow(0),
                Positions = positions,
                Rotations = rotations,
            }.Schedule(1, 1).Complete();

            float2 server = rig.Encoder.PredictedPosition(0, 1);
            Assert.AreEqual(math.asuint(positions[0].x), math.asuint(server.x),
                "the prediction's x must be bit-identical to the client's");
            Assert.AreEqual(math.asuint(positions[0].y), math.asuint(server.y),
                "the prediction's y must be bit-identical to the client's");
        }
        finally
        {
            waypointStart.Dispose();
            speed.Dispose();
            start.Dispose();
            positions.Dispose();
            rotations.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>The spike replication protocol's message types, in the order the v0.3 plan lists them.</summary>
    public enum SpikeMessageType : byte
    {
        /// <summary>Unit ids the encoder knows about but is not allowed to see. Never sent.</summary>
        None = 0,

        /// <summary>Reliable: a unit took a new route (its waypoints follow).</summary>
        MoveOrder = 1,

        /// <summary>Unreliable, at most <see cref="SpikeWire.UnreliableMaxMessageSize"/> bytes: a predicted position was wrong.</summary>
        Correction = 2,

        /// <summary>Reliable: a unit's health changed.</summary>
        Health = 3,

        /// <summary>Reliable: a unit spawned, or walked into the client's vision.</summary>
        Enter = 4,

        /// <summary>Reliable: a unit died, or walked out of the client's vision.</summary>
        Leave = 5,

        /// <summary>Reliable: something died here.</summary>
        Explosion = 6,

        /// <summary>Reliable: cells whose visibility flipped for the receiving team.</summary>
        FogDelta = 7,
    }

    /// <summary>Why a client stopped knowing about a unit.</summary>
    public enum LeaveReason : byte
    {
        /// <summary>The unit died (an <see cref="SpikeMessageType.Explosion"/> follows).</summary>
        Died = 0,

        /// <summary>The unit is alive but left the client's vision.</summary>
        LeftVision = 1,
    }

    /// <summary>
    /// The wire budget the plan fixes: KcpTransport's MTU 1200 with Mirror's 5-byte message metadata
    /// (channel byte and security cookie) and Kcp's 24-byte segment header on top.
    /// </summary>
    public static class SpikeWire
    {
        /// <summary>KcpTransport's MTU (kcp2k's default).</summary>
        public const int Mtu = 1200;

        /// <summary>Mirror's per-message metadata: one channel byte and a four-byte cookie.</summary>
        public const int MetadataSize = 5;

        /// <summary>Kcp's per-segment header.</summary>
        public const int SegmentOverhead = 24;

        /// <summary>Mirror's per-batch timestamp (<c>Batcher.TimestampSize</c>).</summary>
        public const int BatchTimestampSize = 8;

        /// <summary>IPv4 plus UDP header bytes per datagram.</summary>
        public const int UdpIpv4Overhead = 28;

        /// <summary>Mirror's unreliable message limit: MTU less metadata and the channel byte.</summary>
        public const int UnreliableMaxMessageSize = Mtu - MetadataSize - 1;

        /// <summary>
        /// The size a correction message is packed to: the unreliable limit less the soak's eight-byte
        /// send timestamp, so Stage B can send each message in one datagram exactly as Stage A counts it.
        /// </summary>
        public const int CorrectionMessageLimit = UnreliableMaxMessageSize - 8;

        /// <summary>Mirror's batching threshold, the same number: a batch may be this large.</summary>
        public const int BatchThreshold = UnreliableMaxMessageSize;

        /// <summary>Bytes of segment payload a KCP segment can carry: MTU less headers and metadata.</summary>
        public const int SegmentPayload = Mtu - SegmentOverhead - MetadataSize;

        /// <summary>
        /// kcp2k's reliable message limit for MTU 1200 and a 4096-deep window (capped at 255
        /// fragments): 297,433 bytes. A bigger message must be chunked.
        /// </summary>
        public const int ReliableMaxMessageSize = (Mtu - SegmentOverhead - MetadataSize) * (255 - 1) - 1;

        /// <summary>Batch bytes the timestamp leaves for messages, before their varint size prefixes.</summary>
        public const int BatchPayload = BatchThreshold - BatchTimestampSize;

        /// <summary>Bytes a message of <paramref name="size"/> payload bytes costs inside a batch.</summary>
        public static int BatchCost(int size) => VarInt.Size((uint)size) + size;

        /// <summary>Datagrams (KCP segments) a batch of <paramref name="batchBytes"/> becomes.</summary>
        public static int Segments(int batchBytes) => (batchBytes + SegmentPayload - 1) / SegmentPayload;

        /// <summary>
        /// The wire bytes a run of messages costs: their payloads, Mirror's 8-byte timestamp per
        /// batch, Kcp's 24 bytes per segment and UDP/IPv4's 28 bytes per datagram. The messages are
        /// packed in order, exactly as Mirror's <c>Batcher</c> packs them (a new batch starts when the
        /// next message would cross the threshold, so a single larger message is its own batch).
        /// </summary>
        public static long WireBytes(NativeArray<int> messageSizes)
        {
            long total = 0;
            int batch = 0, messages = 0;
            for (int i = 0; i < messageSizes.Length; i++)
            {
                int cost = BatchCost(messageSizes[i]);
                if (messages > 0 && batch + cost > BatchThreshold)
                {
                    total += BatchWire(batch);
                    batch = 0;
                    messages = 0;
                }
                batch += cost;
                messages++;
            }
            if (messages > 0) total += BatchWire(batch);
            return total;
        }

        /// <summary>The wire bytes one batch of <paramref name="batchBytes"/> (timestamp included) costs.</summary>
        public static long BatchWire(int batchBytes)
        {
            int segments = Segments(batchBytes);
            return batchBytes
                   + (long)SegmentOverhead * segments
                   + (long)UdpIpv4Overhead * segments;
        }
    }

    /// <summary>Positions on the wire: 1/16 tile as a ushort, which covers a 1024-tile map.</summary>
    public static class SpikeQuantise
    {
        /// <summary>Sub-tile units a quantised position counts, i.e. 1/16 tile.</summary>
        public const float Scale = 16f;

        /// <summary>Map side in tiles a ushort position covers: 65,536 / 16.</summary>
        public const int MaxTiles = (int)(ushort.MaxValue / Scale);

        /// <summary>The wire value of a tile coordinate.</summary>
        public static ushort Encode(float tile)
        {
            float scaled = tile * Scale;
            return (ushort)math.clamp(math.round(scaled), 0f, ushort.MaxValue);
        }

        /// <summary>The tile coordinate a wire value means.</summary>
        public static float Decode(ushort value) => value / Scale;

        /// <summary>The wire value of a whole tile (waypoints are stored on tile centres).</summary>
        public static ushort EncodeTile(int2 tile) => Encode(tile.x + 0.5f);
    }

    /// <summary>One unit's route inside a <see cref="SpikeMessageType.MoveOrder"/> or <see cref="SpikeMessageType.Enter"/> message.</summary>
    public struct RouteUnit
    {
        /// <summary>Unit id, ascending inside one message.</summary>
        public int Id;

        /// <summary>Index of the unit's first waypoint in the message's waypoint array.</summary>
        public int First;

        /// <summary>Waypoints in the unit's route; at least one.</summary>
        public int Count;
    }

    /// <summary>A <see cref="SpikeMessageType.MoveOrder"/>: one batch of units that took new routes.</summary>
    public struct MoveOrderMessage
    {
        /// <summary>The tick the routes were computed at.</summary>
        public int Tick;

        /// <summary>Movement speed class of every unit in the message (the spike has one).</summary>
        public byte SpeedClass;

        /// <summary>Every unit's waypoints, back to back, in tiles.</summary>
        public NativeArray<int2> Waypoints;

        /// <summary>The units the message carries, ascending by id.</summary>
        public NativeArray<RouteUnit> Units;
    }

    /// <summary>One unit's entry in a <see cref="SpikeMessageType.Correction"/> message.</summary>
    public struct CorrectionUnit
    {
        /// <summary>Unit id, ascending inside one message.</summary>
        public int Id;

        /// <summary>Quantised x, 1/16 tile.</summary>
        public ushort X;

        /// <summary>Quantised y, 1/16 tile.</summary>
        public ushort Y;

        /// <summary>
        /// Delta mode only: the real position minus the client's own prediction, in
        /// 1/<see cref="CorrectionMessage.DeltaScale"/> tile. Both sides evaluate the same prediction,
        /// so the client adds this to it.
        /// </summary>
        public short DX, DY;

        /// <summary>Index of the waypoint the client should steer toward next.</summary>
        public int Resume;
    }

    /// <summary>An unreliable batch of predicted positions that were wrong.</summary>
    public struct CorrectionMessage
    {
        /// <summary>The tick the corrections were computed at.</summary>
        public int Tick;

        /// <summary>
        /// 0: absolute positions (<see cref="CorrectionUnit.X"/>, <see cref="CorrectionUnit.Y"/>, 1/16
        /// tile). Otherwise the entries carry <see cref="CorrectionUnit.DX"/>/<see cref="CorrectionUnit.DY"/>
        /// against the prediction, in 1/DeltaScale tile, as zigzag varints (the bandwidth ladder's
        /// coarser quantisation).
        /// </summary>
        public byte DeltaScale;

        /// <summary>The corrected units, ascending by id.</summary>
        public NativeArray<CorrectionUnit> Units;
    }

    /// <summary>One unit's entry in a <see cref="SpikeMessageType.Health"/> message.</summary>
    public struct HealthUnit
    {
        /// <summary>Unit id, ascending inside one message.</summary>
        public int Id;

        /// <summary>Health as a percentage of maximum, 0 to 100.</summary>
        public byte Health;
    }

    /// <summary>A reliable batch of health changes.</summary>
    public struct HealthMessage
    {
        /// <summary>The changed units, ascending by id.</summary>
        public NativeArray<HealthUnit> Units;
    }

    /// <summary>One unit's entry in a <see cref="SpikeMessageType.Enter"/> message.</summary>
    public struct EnterUnit
    {
        /// <summary>Unit id, ascending inside one message.</summary>
        public int Id;

        /// <summary>Unit type; the spike has one, so this stays 0.</summary>
        public byte Type;

        /// <summary>Player index that owns the unit.</summary>
        public byte Owner;

        /// <summary>Quantised x, 1/16 tile.</summary>
        public ushort X;

        /// <summary>Quantised y, 1/16 tile.</summary>
        public ushort Y;

        /// <summary>Health as a percentage of maximum.</summary>
        public byte Health;

        /// <summary>Index of the unit's first waypoint in the message's waypoint array.</summary>
        public int First;

        /// <summary>Waypoints in the unit's remaining route; at least one.</summary>
        public int Count;
    }

    /// <summary>A unit spawned, or walked into a client's vision.</summary>
    public struct EnterMessage
    {
        /// <summary>Every unit's waypoints, back to back, in tiles.</summary>
        public NativeArray<int2> Waypoints;

        /// <summary>The units that entered, ascending by id.</summary>
        public NativeArray<EnterUnit> Units;
    }

    /// <summary>One unit's entry in a <see cref="SpikeMessageType.Leave"/> message.</summary>
    public struct LeaveUnit
    {
        /// <summary>Unit id, ascending inside one message.</summary>
        public int Id;

        /// <summary>Either death or lost vision.</summary>
        public LeaveReason Reason;
    }

    /// <summary>A reliable batch of units the client no longer knows about.</summary>
    public struct LeaveMessage
    {
        /// <summary>The departing units, ascending by id.</summary>
        public NativeArray<LeaveUnit> Units;
    }

    /// <summary>An explosion: where a unit died.</summary>
    public struct ExplosionMessage
    {
        /// <summary>Quantised x, 1/16 tile.</summary>
        public NativeArray<ushort> X;

        /// <summary>Quantised y, 1/16 tile.</summary>
        public NativeArray<ushort> Y;
    }

    /// <summary>Cells whose visibility flipped in the receiving team's last fog update, ascending.</summary>
    public struct FogDeltaMessage
    {
        /// <summary>Ascending cell indices (<c>y * width + x</c>).</summary>
        public NativeArray<int> Cells;
    }

    /// <summary>
    /// The replication messages' wire format. Each encoder writes its type byte first, so a reader
    /// walks a stream by reading a type and dispatching. Ids inside one message are delta-encoded
    /// varints against the previous id (the first runs against zero), and waypoints are tiles with
    /// the first one absolute (<c>ushort</c> x and y) and the rest one-<c>sbyte</c>-per-axis deltas
    /// whose <c>0x80</c> escape means an absolute <c>ushort</c> pair follows.
    /// </summary>
    public static class SpikeMessages
    {
        /// <summary>The largest reliable message the encoder produces: kcp2k's limit, so a burst is chunked at it.</summary>
        public const int ReliableChunk = SpikeWire.ReliableMaxMessageSize;

        /// <summary>A unit's route as it appears in a MoveOrder: its first waypoint absolute, the rest delta-coded.</summary>
        public static int RouteSize(NativeArray<int2> waypoints, int first, int count)
        {
            int size = VarInt.Size((uint)count) + 4;
            for (int i = 1; i < count; i++)
            {
                int2 previous = waypoints[first + i - 1];
                int2 tile = waypoints[first + i];
                int dx = tile.x - previous.x, dy = tile.y - previous.y;
                size += dx >= -127 && dx <= 127 && dy >= -127 && dy <= 127 ? 2 : 5;
            }
            return size;
        }

        /// <summary>Bytes a MoveOrder unit entry costs, delta against <paramref name="previousId"/> included.</summary>
        public static int MoveOrderUnitSize(NativeArray<int2> waypoints, int first, int count, int id, int previousId) =>
            VarInt.Size((uint)(id - previousId)) + RouteSize(waypoints, first, count);

        /// <summary>Bytes a Correction entry costs, delta against <paramref name="previousId"/> included.</summary>
        public static int CorrectionUnitSize(int id, int previousId, int resume) =>
            VarInt.Size((uint)(id - previousId)) + 4 + VarInt.Size((uint)resume);

        /// <summary>Bytes a delta-mode Correction entry costs.</summary>
        public static int CorrectionDeltaUnitSize(int id, int previousId, int dx, int dy, int resume) =>
            VarInt.Size((uint)(id - previousId)) + VarInt.SizeInt(dx) + VarInt.SizeInt(dy) + VarInt.Size((uint)resume);

        /// <summary>Bytes an Enter entry costs, delta against <paramref name="previousId"/> included.</summary>
        public static int EnterUnitSize(NativeArray<int2> waypoints, int first, int count, int id, int previousId) =>
            VarInt.Size((uint)(id - previousId)) + 7 + RouteSize(waypoints, first, count);

        /// <summary>Bytes a Health entry costs, delta against <paramref name="previousId"/> included.</summary>
        public static int HealthUnitSize(int id, int previousId) => VarInt.Size((uint)(id - previousId)) + 1;

        /// <summary>Bytes a Leave entry costs, delta against <paramref name="previousId"/> included.</summary>
        public static int LeaveUnitSize(int id, int previousId) => VarInt.Size((uint)(id - previousId)) + 1;

        /// <summary>Writes a MoveOrder message, type byte included.</summary>
        public static void Encode(NetworkWriter writer, in MoveOrderMessage message)
        {
            var sink = new NetworkSink(writer);
            Encode(ref sink, message);
        }

        /// <summary>The same message into any byte sink; the Burst encoder writes into a native list.</summary>
        public static void Encode<W>(ref W writer, in MoveOrderMessage message) where W : struct, ISpikeWriter
        {
            writer.WriteByte((byte)SpikeMessageType.MoveOrder);
            VarInt.Write(ref writer, (uint)message.Tick);
            writer.WriteByte(message.SpeedClass);
            VarInt.Write(ref writer, (uint)message.Units.Length);
            int previous = 0;
            for (int i = 0; i < message.Units.Length; i++)
            {
                RouteUnit unit = message.Units[i];
                VarInt.Write(ref writer, (uint)(unit.Id - previous));
                previous = unit.Id;
                WriteRoute(ref writer, message.Waypoints, unit.First, unit.Count);
            }
        }

        /// <summary>Reads a MoveOrder message; the type byte must be the next byte.</summary>
        public static MoveOrderMessage DecodeMoveOrder(NetworkReader reader, Allocator allocator)
        {
            Expect(reader, SpikeMessageType.MoveOrder);
            var message = new MoveOrderMessage
            {
                Tick = (int)VarInt.Read(reader),
                SpeedClass = reader.ReadByte(),
            };
            int count = (int)VarInt.Read(reader);
            var units = new NativeArray<RouteUnit>(count, allocator);
            var waypoints = new NativeList<int2>(count * 4, allocator);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                int id = previous + (int)VarInt.Read(reader);
                previous = id;
                int first = waypoints.Length;
                int waypointCount = ReadRoute(reader, waypoints);
                units[i] = new RouteUnit { Id = id, First = first, Count = waypointCount };
            }
            message.Units = units;
            message.Waypoints = new NativeArray<int2>(waypoints.AsArray(), allocator);
            waypoints.Dispose();
            return message;
        }

        /// <summary>Writes a Correction message, type byte included.</summary>
        public static void Encode(NetworkWriter writer, in CorrectionMessage message)
        {
            var sink = new NetworkSink(writer);
            Encode(ref sink, message);
        }

        /// <summary>The same message into any byte sink; the Burst encoder writes into a native list.</summary>
        public static void Encode<W>(ref W writer, in CorrectionMessage message) where W : struct, ISpikeWriter
        {
            writer.WriteByte((byte)SpikeMessageType.Correction);
            VarInt.Write(ref writer, (uint)message.Tick);
            writer.WriteByte(message.DeltaScale);
            VarInt.Write(ref writer, (uint)message.Units.Length);
            int previous = 0;
            for (int i = 0; i < message.Units.Length; i++)
            {
                CorrectionUnit unit = message.Units[i];
                VarInt.Write(ref writer, (uint)(unit.Id - previous));
                previous = unit.Id;
                if (message.DeltaScale == 0)
                {
                    writer.WriteUShort(unit.X);
                    writer.WriteUShort(unit.Y);
                }
                else
                {
                    VarInt.WriteInt(ref writer, unit.DX);
                    VarInt.WriteInt(ref writer, unit.DY);
                }
                VarInt.Write(ref writer, (uint)unit.Resume);
            }
        }

        /// <summary>Reads a Correction message; the type byte must be the next byte.</summary>
        public static CorrectionMessage DecodeCorrection(NetworkReader reader, Allocator allocator)
        {
            Expect(reader, SpikeMessageType.Correction);
            int tick = (int)VarInt.Read(reader);
            byte scale = reader.ReadByte();
            int count = (int)VarInt.Read(reader);
            var units = new NativeArray<CorrectionUnit>(count, allocator);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                int id = previous + (int)VarInt.Read(reader);
                previous = id;
                var unit = new CorrectionUnit { Id = id };
                if (scale == 0)
                {
                    unit.X = reader.ReadUShort();
                    unit.Y = reader.ReadUShort();
                }
                else
                {
                    unit.DX = (short)VarInt.ReadInt(reader);
                    unit.DY = (short)VarInt.ReadInt(reader);
                }
                unit.Resume = (int)VarInt.Read(reader);
                units[i] = unit;
            }
            return new CorrectionMessage { Tick = tick, DeltaScale = scale, Units = units };
        }

        /// <summary>Writes a Health message, type byte included.</summary>
        public static void Encode(NetworkWriter writer, in HealthMessage message)
        {
            var sink = new NetworkSink(writer);
            Encode(ref sink, message);
        }

        /// <summary>The same message into any byte sink; the Burst encoder writes into a native list.</summary>
        public static void Encode<W>(ref W writer, in HealthMessage message) where W : struct, ISpikeWriter
        {
            writer.WriteByte((byte)SpikeMessageType.Health);
            VarInt.Write(ref writer, (uint)message.Units.Length);
            int previous = 0;
            for (int i = 0; i < message.Units.Length; i++)
            {
                HealthUnit unit = message.Units[i];
                VarInt.Write(ref writer, (uint)(unit.Id - previous));
                previous = unit.Id;
                writer.WriteByte(unit.Health);
            }
        }

        /// <summary>Reads a Health message; the type byte must be the next byte.</summary>
        public static HealthMessage DecodeHealth(NetworkReader reader, Allocator allocator)
        {
            Expect(reader, SpikeMessageType.Health);
            int count = (int)VarInt.Read(reader);
            var units = new NativeArray<HealthUnit>(count, allocator);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                int id = previous + (int)VarInt.Read(reader);
                previous = id;
                units[i] = new HealthUnit { Id = id, Health = reader.ReadByte() };
            }
            return new HealthMessage { Units = units };
        }

        /// <summary>Writes an Enter message, type byte included.</summary>
        public static void Encode(NetworkWriter writer, in EnterMessage message)
        {
            var sink = new NetworkSink(writer);
            Encode(ref sink, message);
        }

        /// <summary>The same message into any byte sink; the Burst encoder writes into a native list.</summary>
        public static void Encode<W>(ref W writer, in EnterMessage message) where W : struct, ISpikeWriter
        {
            writer.WriteByte((byte)SpikeMessageType.Enter);
            VarInt.Write(ref writer, (uint)message.Units.Length);
            int previous = 0;
            for (int i = 0; i < message.Units.Length; i++)
            {
                EnterUnit unit = message.Units[i];
                VarInt.Write(ref writer, (uint)(unit.Id - previous));
                previous = unit.Id;
                writer.WriteByte(unit.Type);
                writer.WriteByte(unit.Owner);
                writer.WriteUShort(unit.X);
                writer.WriteUShort(unit.Y);
                writer.WriteByte(unit.Health);
                WriteRoute(ref writer, message.Waypoints, unit.First, unit.Count);
            }
        }

        /// <summary>Reads an Enter message; the type byte must be the next byte.</summary>
        public static EnterMessage DecodeEnter(NetworkReader reader, Allocator allocator)
        {
            Expect(reader, SpikeMessageType.Enter);
            int count = (int)VarInt.Read(reader);
            var units = new NativeArray<EnterUnit>(count, allocator);
            var waypoints = new NativeList<int2>(count * 4, allocator);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                int id = previous + (int)VarInt.Read(reader);
                previous = id;
                var unit = new EnterUnit
                {
                    Id = id,
                    Type = reader.ReadByte(),
                    Owner = reader.ReadByte(),
                    X = reader.ReadUShort(),
                    Y = reader.ReadUShort(),
                    Health = reader.ReadByte(),
                    First = waypoints.Length,
                };
                unit.Count = ReadRoute(reader, waypoints);
                units[i] = unit;
            }
            var result = new EnterMessage
            {
                Units = units,
                Waypoints = new NativeArray<int2>(waypoints.AsArray(), allocator),
            };
            waypoints.Dispose();
            return result;
        }

        /// <summary>Writes a Leave message, type byte included.</summary>
        public static void Encode(NetworkWriter writer, in LeaveMessage message)
        {
            var sink = new NetworkSink(writer);
            Encode(ref sink, message);
        }

        /// <summary>The same message into any byte sink; the Burst encoder writes into a native list.</summary>
        public static void Encode<W>(ref W writer, in LeaveMessage message) where W : struct, ISpikeWriter
        {
            writer.WriteByte((byte)SpikeMessageType.Leave);
            VarInt.Write(ref writer, (uint)message.Units.Length);
            int previous = 0;
            for (int i = 0; i < message.Units.Length; i++)
            {
                LeaveUnit unit = message.Units[i];
                VarInt.Write(ref writer, (uint)(unit.Id - previous));
                previous = unit.Id;
                writer.WriteByte((byte)unit.Reason);
            }
        }

        /// <summary>Reads a Leave message; the type byte must be the next byte.</summary>
        public static LeaveMessage DecodeLeave(NetworkReader reader, Allocator allocator)
        {
            Expect(reader, SpikeMessageType.Leave);
            int count = (int)VarInt.Read(reader);
            var units = new NativeArray<LeaveUnit>(count, allocator);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                int id = previous + (int)VarInt.Read(reader);
                previous = id;
                units[i] = new LeaveUnit { Id = id, Reason = (LeaveReason)reader.ReadByte() };
            }
            return new LeaveMessage { Units = units };
        }

        /// <summary>Writes an Explosion message, type byte included.</summary>
        public static void Encode(NetworkWriter writer, in ExplosionMessage message)
        {
            var sink = new NetworkSink(writer);
            Encode(ref sink, message);
        }

        /// <summary>The same message into any byte sink; the Burst encoder writes into a native list.</summary>
        public static void Encode<W>(ref W writer, in ExplosionMessage message) where W : struct, ISpikeWriter
        {
            writer.WriteByte((byte)SpikeMessageType.Explosion);
            VarInt.Write(ref writer, (uint)message.X.Length);
            for (int i = 0; i < message.X.Length; i++)
            {
                writer.WriteUShort(message.X[i]);
                writer.WriteUShort(message.Y[i]);
            }
        }

        /// <summary>Reads an Explosion message; the type byte must be the next byte.</summary>
        public static ExplosionMessage DecodeExplosion(NetworkReader reader, Allocator allocator)
        {
            Expect(reader, SpikeMessageType.Explosion);
            int count = (int)VarInt.Read(reader);
            var x = new NativeArray<ushort>(count, allocator);
            var y = new NativeArray<ushort>(count, allocator);
            for (int i = 0; i < count; i++)
            {
                x[i] = reader.ReadUShort();
                y[i] = reader.ReadUShort();
            }
            return new ExplosionMessage { X = x, Y = y };
        }

        /// <summary>Writes a FogDelta message (ascending cells as gaps), type byte included.</summary>
        public static void Encode(NetworkWriter writer, in FogDeltaMessage message)
        {
            var sink = new NetworkSink(writer);
            Encode(ref sink, message);
        }

        /// <summary>The same message into any byte sink; the Burst encoder writes into a native list.</summary>
        public static void Encode<W>(ref W writer, in FogDeltaMessage message) where W : struct, ISpikeWriter
        {
            writer.WriteByte((byte)SpikeMessageType.FogDelta);
            VarInt.Write(ref writer, (uint)message.Cells.Length);
            int previous = 0;
            for (int i = 0; i < message.Cells.Length; i++)
            {
                int cell = message.Cells[i];
                VarInt.Write(ref writer, (uint)(cell - previous));
                previous = cell;
            }
        }

        /// <summary>Reads a FogDelta message; the type byte must be the next byte.</summary>
        public static FogDeltaMessage DecodeFogDelta(NetworkReader reader, Allocator allocator)
        {
            Expect(reader, SpikeMessageType.FogDelta);
            int count = (int)VarInt.Read(reader);
            var cells = new NativeArray<int>(count, allocator);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                previous += (int)VarInt.Read(reader);
                cells[i] = previous;
            }
            return new FogDeltaMessage { Cells = cells };
        }

        /// <summary>Every message type's type byte, read and consumed.</summary>
        public static SpikeMessageType ReadType(NetworkReader reader) => (SpikeMessageType)reader.ReadByte();

        /// <summary>The next message's type, without consuming it; the decoders read the byte themselves.</summary>
        public static SpikeMessageType PeekType(NetworkReader reader)
        {
            int position = reader.Position;
            var type = (SpikeMessageType)reader.ReadByte();
            reader.Position = position;
            return type;
        }

        /// <summary>Writes one unit's route: count, absolute first tile, delta-coded or escaped rest.</summary>
        public static void WriteRoute<W>(ref W writer, NativeArray<int2> waypoints, int first, int count) where W : struct, ISpikeWriter
        {
            VarInt.Write(ref writer, (uint)count);
            int2 tile = waypoints[first];
            writer.WriteUShort((ushort)tile.x);
            writer.WriteUShort((ushort)tile.y);
            for (int i = 1; i < count; i++)
            {
                int2 previous = waypoints[first + i - 1];
                tile = waypoints[first + i];
                int dx = tile.x - previous.x, dy = tile.y - previous.y;
                if (dx >= -127 && dx <= 127 && dy >= -127 && dy <= 127)
                {
                    writer.WriteByte((byte)(sbyte)dx);
                    writer.WriteByte((byte)(sbyte)dy);
                }
                else
                {
                    writer.WriteByte(0x80);
                    writer.WriteUShort((ushort)tile.x);
                    writer.WriteUShort((ushort)tile.y);
                }
            }
        }

        /// <summary>Reads one route into <paramref name="waypoints"/>, returning its length.</summary>
        public static int ReadRoute(NetworkReader reader, NativeList<int2> waypoints)
        {
            int count = (int)VarInt.Read(reader);
            var tile = new int2(reader.ReadUShort(), reader.ReadUShort());
            waypoints.Add(tile);
            for (int i = 1; i < count; i++)
            {
                sbyte dx = reader.ReadSByte();
                if ((byte)dx == 0x80)
                {
                    tile = new int2(reader.ReadUShort(), reader.ReadUShort());
                }
                else
                {
                    sbyte dy = reader.ReadSByte();
                    tile = new int2(tile.x + dx, tile.y + dy);
                }
                waypoints.Add(tile);
            }
            return count;
        }

        /// <summary>
        /// Reads every message in a stream and appends the unit ids they mention. Deprecated messages
        /// throw; the stream must end exactly on a message boundary.
        /// </summary>
        public static void CollectIds(NetworkReader reader, List<int> ids)
        {
            while (reader.Remaining > 0)
            {
                switch (PeekType(reader))
                {
                    case SpikeMessageType.MoveOrder:
                    {
                        MoveOrderMessage message = DecodeMoveOrder(reader, Allocator.Temp);
                        for (int i = 0; i < message.Units.Length; i++) ids.Add(message.Units[i].Id);
                        message.Units.Dispose();
                        message.Waypoints.Dispose();
                        break;
                    }
                    case SpikeMessageType.Correction:
                    {
                        CorrectionMessage message = DecodeCorrection(reader, Allocator.Temp);
                        for (int i = 0; i < message.Units.Length; i++) ids.Add(message.Units[i].Id);
                        message.Units.Dispose();
                        break;
                    }
                    case SpikeMessageType.Health:
                    {
                        HealthMessage message = DecodeHealth(reader, Allocator.Temp);
                        for (int i = 0; i < message.Units.Length; i++) ids.Add(message.Units[i].Id);
                        message.Units.Dispose();
                        break;
                    }
                    case SpikeMessageType.Enter:
                    {
                        EnterMessage message = DecodeEnter(reader, Allocator.Temp);
                        for (int i = 0; i < message.Units.Length; i++) ids.Add(message.Units[i].Id);
                        message.Units.Dispose();
                        message.Waypoints.Dispose();
                        break;
                    }
                    case SpikeMessageType.Leave:
                    {
                        LeaveMessage message = DecodeLeave(reader, Allocator.Temp);
                        for (int i = 0; i < message.Units.Length; i++) ids.Add(message.Units[i].Id);
                        message.Units.Dispose();
                        break;
                    }
                    case SpikeMessageType.Explosion:
                    {
                        ExplosionMessage message = DecodeExplosion(reader, Allocator.Temp);
                        message.X.Dispose();
                        message.Y.Dispose();
                        break;
                    }
                    case SpikeMessageType.FogDelta:
                    {
                        FogDeltaMessage message = DecodeFogDelta(reader, Allocator.Temp);
                        message.Cells.Dispose();
                        break;
                    }
                    default:
                        throw new InvalidDataException("unknown replication message type");
                }
            }
        }

        private static void Expect(NetworkReader reader, SpikeMessageType type)
        {
            var actual = (SpikeMessageType)reader.ReadByte();
            if (actual != type) throw new InvalidDataException($"expected message {type}, got {actual}");
        }
    }
}

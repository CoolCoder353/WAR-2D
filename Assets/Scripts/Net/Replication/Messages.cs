using System.Collections.Generic;
using System.IO;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Net.Replication
{
    /// <summary>Unit replication message types. Every message starts with this byte.</summary>
    public enum MessageType : byte
    {
        None = 0,
        MoveOrder = 1,
        Correction = 2,
        Health = 3,
        Enter = 4,
        Leave = 5,
        Attack = 6,
    }

    /// <summary>Why a unit left a client's knowledge.</summary>
    public enum LeaveReason : byte
    {
        Died = 0,
        LeftView = 1,
    }

    // Units are addressed by their id's index (NetIdAllocator.IndexOf) in ascending order, delta coded.
    // Only Enter carries the full id (index + generation); the client maps index → id from then on, and
    // a reused index always arrives as a Leave followed by an Enter.

    /// <summary>A unit and its route (a slice of a shared waypoint list).</summary>
    public struct RouteUnit
    {
        public int Index;
        public int First;
        public int Count;
    }

    /// <summary>A position correction against the client's prediction.</summary>
    public struct CorrectionUnit
    {
        public int Index;
        /// <summary>Offset from the predicted position, in 1/DeltaScale tile.</summary>
        public short DX, DY;
        /// <summary>The route waypoint to resume from.</summary>
        public int Resume;
        /// <summary>Speed in 1/64 of the unit's full speed.</summary>
        public byte Speed;
    }

    public struct HealthUnit
    {
        public int Index;
        /// <summary>Percent of max health, 0..100.</summary>
        public byte Health;
    }

    public struct EnterUnit
    {
        public int Id;
        public byte Type;
        public int OwnerId;
        public ushort X, Y;
        public byte Health;
        public int First, Count;
    }

    public struct LeaveUnit
    {
        public int Index;
        public LeaveReason Reason;
    }

    /// <summary>An attack for the client's tracer: attacker index, target id (unit or building).</summary>
    public struct AttackEvent
    {
        public int AttackerIndex;
        public int TargetId;
    }

    /// <summary>
    /// Encoders (Burst-compatible, through <see cref="IWireWriter"/>) and decoders (managed, into
    /// caller-owned lists) for every message. Decoders throw <see cref="InvalidDataException"/> or
    /// <see cref="EndOfStreamException"/> on malformed input and never allocate unbounded memory.
    /// </summary>
    public static class Messages
    {
        /// <summary>Most waypoints one route may carry.</summary>
        public const int MaxRouteWaypoints = 4096;

        // ---------- sizes, for packing ----------

        public static int RouteSize(NativeArray<int2> waypoints, int first, int count)
        {
            int size = VarInt.Size((uint)count) + 4;
            for (int i = 1; i < count; i++)
            {
                int2 d = waypoints[first + i] - waypoints[first + i - 1];
                size += d.x >= -127 && d.x <= 127 && d.y >= -127 && d.y <= 127 ? 2 : 5;
            }
            return size;
        }

        public static int EnterUnitSize(NativeArray<int2> waypoints, in EnterUnit u, int previousIndex) =>
            VarInt.Size((uint)(NetIdAllocator.IndexOf(u.Id) - previousIndex)) + VarInt.Size((uint)NetIdAllocator.GenerationOf(u.Id))
            + 1 + VarInt.Size((uint)u.OwnerId) + 5 + RouteSize(waypoints, u.First, u.Count);

        public static int CorrectionUnitSize(in CorrectionUnit u, int previousIndex) =>
            VarInt.Size((uint)(u.Index - previousIndex)) + VarInt.SizeInt(u.DX) + VarInt.SizeInt(u.DY) + VarInt.Size((uint)u.Resume) + 1;

        // ---------- encoders ----------

        public static void WriteRoute<W>(ref W writer, NativeArray<int2> waypoints, int first, int count) where W : struct, IWireWriter
        {
            VarInt.Write(ref writer, (uint)count);
            int2 tile = waypoints[first];
            writer.WriteUShort((ushort)tile.x);
            writer.WriteUShort((ushort)tile.y);
            for (int i = 1; i < count; i++)
            {
                int2 previous = waypoints[first + i - 1];
                tile = waypoints[first + i];
                int2 d = tile - previous;
                if (d.x >= -127 && d.x <= 127 && d.y >= -127 && d.y <= 127)
                {
                    writer.WriteByte((byte)(sbyte)d.x);
                    writer.WriteByte((byte)(sbyte)d.y);
                }
                else
                {
                    writer.WriteByte(0x80);
                    writer.WriteUShort((ushort)tile.x);
                    writer.WriteUShort((ushort)tile.y);
                }
            }
        }

        public static void EncodeEnter<W>(ref W writer, NativeArray<int2> waypoints, NativeArray<EnterUnit> units) where W : struct, IWireWriter
        {
            writer.WriteByte((byte)MessageType.Enter);
            VarInt.Write(ref writer, (uint)units.Length);
            int previous = 0;
            for (int i = 0; i < units.Length; i++)
            {
                EnterUnit u = units[i];
                int index = NetIdAllocator.IndexOf(u.Id);
                VarInt.Write(ref writer, (uint)(index - previous));
                previous = index;
                VarInt.Write(ref writer, (uint)NetIdAllocator.GenerationOf(u.Id));
                writer.WriteByte(u.Type);
                VarInt.Write(ref writer, (uint)u.OwnerId);
                writer.WriteUShort(u.X);
                writer.WriteUShort(u.Y);
                writer.WriteByte(u.Health);
                WriteRoute(ref writer, waypoints, u.First, u.Count);
            }
        }

        public static void EncodeMoveOrder<W>(ref W writer, int tick, NativeArray<int2> waypoints, NativeArray<RouteUnit> units) where W : struct, IWireWriter
        {
            writer.WriteByte((byte)MessageType.MoveOrder);
            VarInt.Write(ref writer, (uint)tick);
            VarInt.Write(ref writer, (uint)units.Length);
            int previous = 0;
            for (int i = 0; i < units.Length; i++)
            {
                RouteUnit u = units[i];
                VarInt.Write(ref writer, (uint)(u.Index - previous));
                previous = u.Index;
                WriteRoute(ref writer, waypoints, u.First, u.Count);
            }
        }

        public static void EncodeCorrection<W>(ref W writer, int tick, byte deltaScale, NativeArray<CorrectionUnit> units) where W : struct, IWireWriter
        {
            writer.WriteByte((byte)MessageType.Correction);
            VarInt.Write(ref writer, (uint)tick);
            writer.WriteByte((byte)(deltaScale & 0x7F));
            VarInt.Write(ref writer, (uint)units.Length);
            int previous = 0;
            for (int i = 0; i < units.Length; i++)
            {
                CorrectionUnit u = units[i];
                VarInt.Write(ref writer, (uint)(u.Index - previous));
                previous = u.Index;
                VarInt.WriteInt(ref writer, u.DX);
                VarInt.WriteInt(ref writer, u.DY);
                VarInt.Write(ref writer, (uint)u.Resume);
                writer.WriteByte(u.Speed);
            }
        }

        public static void EncodeHealth<W>(ref W writer, NativeArray<HealthUnit> units) where W : struct, IWireWriter
        {
            writer.WriteByte((byte)MessageType.Health);
            VarInt.Write(ref writer, (uint)units.Length);
            int previous = 0;
            for (int i = 0; i < units.Length; i++)
            {
                VarInt.Write(ref writer, (uint)(units[i].Index - previous));
                previous = units[i].Index;
                writer.WriteByte(units[i].Health);
            }
        }

        public static void EncodeLeave<W>(ref W writer, NativeArray<LeaveUnit> units) where W : struct, IWireWriter
        {
            writer.WriteByte((byte)MessageType.Leave);
            VarInt.Write(ref writer, (uint)units.Length);
            int previous = 0;
            for (int i = 0; i < units.Length; i++)
            {
                VarInt.Write(ref writer, (uint)(units[i].Index - previous));
                previous = units[i].Index;
                writer.WriteByte((byte)units[i].Reason);
            }
        }

        public static void EncodeAttack<W>(ref W writer, NativeArray<AttackEvent> events) where W : struct, IWireWriter
        {
            writer.WriteByte((byte)MessageType.Attack);
            VarInt.Write(ref writer, (uint)events.Length);
            int previous = 0;
            for (int i = 0; i < events.Length; i++)
            {
                VarInt.WriteInt(ref writer, events[i].AttackerIndex - previous);
                previous = events[i].AttackerIndex;
                VarInt.Write(ref writer, (uint)events[i].TargetId);
            }
        }

        // ---------- decoders ----------

        public static MessageType PeekType(NetworkReader reader)
        {
            if (reader.Remaining < 1) throw new EndOfStreamException("no message type");
            int position = reader.Position;
            var type = (MessageType)reader.ReadByte();
            reader.Position = position;
            return type;
        }

        private static void Expect(NetworkReader reader, MessageType type)
        {
            var actual = (MessageType)reader.ReadByte();
            if (actual != type) throw new InvalidDataException($"expected {type}, got {actual}");
        }

        /// <summary>Reads an element count, rejecting counts the remaining bytes cannot hold.</summary>
        private static int Count(NetworkReader reader, int minBytesEach)
        {
            uint count = VarInt.Read(reader);
            if (count > (uint)(reader.Remaining / math.max(1, minBytesEach))) throw new InvalidDataException($"count {count} exceeds the payload");
            return (int)count;
        }

        private static int NextIndex(NetworkReader reader, ref int previous)
        {
            uint delta = VarInt.Read(reader);
            long index = (long)previous + delta;
            if (index >= NetIdAllocator.MaxCapacity) throw new InvalidDataException("unit index out of range");
            previous = (int)index;
            return previous;
        }

        public static int ReadRoute(NetworkReader reader, List<int2> waypoints)
        {
            int count = (int)VarInt.Read(reader);
            if (count < 1 || count > MaxRouteWaypoints || count * 2 + 2 > reader.Remaining) throw new InvalidDataException($"route of {count} waypoints");
            var tile = new int2(reader.ReadUShort(), reader.ReadUShort());
            waypoints.Add(tile);
            for (int i = 1; i < count; i++)
            {
                sbyte dx = reader.ReadSByte();
                if ((byte)dx == 0x80) tile = new int2(reader.ReadUShort(), reader.ReadUShort());
                else tile += new int2(dx, reader.ReadSByte());
                waypoints.Add(tile);
            }
            return count;
        }

        public static void DecodeEnter(NetworkReader reader, List<EnterUnit> units, List<int2> waypoints)
        {
            Expect(reader, MessageType.Enter);
            int count = Count(reader, 12);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                int index = NextIndex(reader, ref previous);
                uint generation = VarInt.Read(reader);
                if (generation == 0 || generation > 2047) throw new InvalidDataException("bad id generation");
                var u = new EnterUnit
                {
                    Id = ((int)generation << NetIdAllocator.IndexBits) | index,
                    Type = reader.ReadByte(),
                    OwnerId = (int)VarInt.Read(reader),
                    X = reader.ReadUShort(),
                    Y = reader.ReadUShort(),
                    Health = reader.ReadByte(),
                    First = waypoints.Count,
                };
                u.Count = ReadRoute(reader, waypoints);
                units.Add(u);
            }
        }

        public static int DecodeMoveOrder(NetworkReader reader, List<RouteUnit> units, List<int2> waypoints)
        {
            Expect(reader, MessageType.MoveOrder);
            int tick = (int)VarInt.Read(reader);
            int count = Count(reader, 6);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                var u = new RouteUnit { Index = NextIndex(reader, ref previous), First = waypoints.Count };
                u.Count = ReadRoute(reader, waypoints);
                units.Add(u);
            }
            return tick;
        }

        public static int DecodeCorrection(NetworkReader reader, List<CorrectionUnit> units, out byte deltaScale)
        {
            Expect(reader, MessageType.Correction);
            int tick = (int)VarInt.Read(reader);
            deltaScale = (byte)(reader.ReadByte() & 0x7F);
            int count = Count(reader, 5);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                var u = new CorrectionUnit { Index = NextIndex(reader, ref previous) };
                u.DX = (short)math.clamp(VarInt.ReadInt(reader), short.MinValue, short.MaxValue);
                u.DY = (short)math.clamp(VarInt.ReadInt(reader), short.MinValue, short.MaxValue);
                u.Resume = (int)math.min(VarInt.Read(reader), (uint)MaxRouteWaypoints);
                u.Speed = reader.ReadByte();
                units.Add(u);
            }
            return tick;
        }

        public static void DecodeHealth(NetworkReader reader, List<HealthUnit> units)
        {
            Expect(reader, MessageType.Health);
            int count = Count(reader, 2);
            int previous = 0;
            for (int i = 0; i < count; i++)
                units.Add(new HealthUnit { Index = NextIndex(reader, ref previous), Health = reader.ReadByte() });
        }

        public static void DecodeLeave(NetworkReader reader, List<LeaveUnit> units)
        {
            Expect(reader, MessageType.Leave);
            int count = Count(reader, 2);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                int index = NextIndex(reader, ref previous);
                byte reason = reader.ReadByte();
                if (reason > (byte)LeaveReason.LeftView) throw new InvalidDataException("bad leave reason");
                units.Add(new LeaveUnit { Index = index, Reason = (LeaveReason)reason });
            }
        }

        public static void DecodeAttack(NetworkReader reader, List<AttackEvent> events)
        {
            Expect(reader, MessageType.Attack);
            int count = Count(reader, 2);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                int index = previous + VarInt.ReadInt(reader);
                if ((uint)index >= NetIdAllocator.MaxCapacity) throw new InvalidDataException("attacker index out of range");
                previous = index;
                events.Add(new AttackEvent { AttackerIndex = index, TargetId = (int)VarInt.Read(reader) });
            }
        }
    }
}

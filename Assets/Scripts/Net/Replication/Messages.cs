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
        /// <summary>The unit's id index (<see cref="NetIdAllocator.IndexOf"/>).</summary>
        public int Index;
        /// <summary>Where the unit's waypoints start in the shared waypoint list.</summary>
        public int First;
        /// <summary>How many waypoints the route has.</summary>
        public int Count;
    }

    /// <summary>A position correction against the client's prediction.</summary>
    public struct CorrectionUnit
    {
        /// <summary>The unit's id index (<see cref="NetIdAllocator.IndexOf"/>).</summary>
        public int Index;
        /// <summary>Offset from the predicted position, in 1/DeltaScale tile.</summary>
        public short DX, DY;
        /// <summary>The route waypoint to resume from.</summary>
        public int Resume;
        /// <summary>Speed in 1/64 of the unit's full speed.</summary>
        public byte Speed;
    }

    /// <summary>A unit whose health changed.</summary>
    public struct HealthUnit
    {
        /// <summary>The unit's id index (<see cref="NetIdAllocator.IndexOf"/>).</summary>
        public int Index;
        /// <summary>Percent of max health, 0..100.</summary>
        public byte Health;
    }

    /// <summary>A unit entering a client's interest: full id, type, owner, position, health and route.</summary>
    public struct EnterUnit
    {
        /// <summary>The full unit id (generation and index).</summary>
        public int Id;
        /// <summary>The <see cref="UnitType"/> value.</summary>
        public byte Type;
        /// <summary>The owner's player id.</summary>
        public int OwnerId;
        /// <summary>Position in <see cref="Quantise"/> units.</summary>
        public ushort X, Y;
        /// <summary>Percent of max health, 0..100.</summary>
        public byte Health;
        /// <summary>The route as a slice of the shared waypoint list.</summary>
        public int First, Count;
    }

    /// <summary>A unit leaving a client's interest.</summary>
    public struct LeaveUnit
    {
        /// <summary>The unit's id index (<see cref="NetIdAllocator.IndexOf"/>).</summary>
        public int Index;
        /// <summary>Why it left.</summary>
        public LeaveReason Reason;
    }

    /// <summary>What fired an <see cref="AttackEvent"/> (v0.13).</summary>
    public static class AttackerKinds
    {
        /// <summary>A unit, named by its id index (the client knows the unit from its Enter).</summary>
        public const byte Unit = 0;
        /// <summary>A building (a Gun turret), named by its full id (the client knows buildings by id from their records).</summary>
        public const byte Building = 1;
    }

    /// <summary>
    /// An attack for the client's tracer: the attacker (a unit's id index, or since v0.13 a building's full id) and
    /// the target id (unit or building, 0 when the client may not know it).
    /// </summary>
    public struct AttackEvent
    {
        /// <summary>The attacking unit's id index (unit attackers only).</summary>
        public int AttackerIndex;
        /// <summary>The target's full id.</summary>
        public int TargetId;
        /// <summary><see cref="AttackerKinds"/>.</summary>
        public byte AttackerKind;
        /// <summary>The attacking building's full id (building attackers only).</summary>
        public int AttackerId;
    }

    /// <summary>
    /// Encoders (Burst-compatible, through <see cref="IWireWriter"/>) and decoders (managed, into
    /// caller-owned lists) for every message. Decoders throw <see cref="InvalidDataException"/> or
    /// <see cref="EndOfStreamException"/> on malformed input and never allocate unbounded memory.
    /// </summary>
    /// <summary>
    /// Route waypoints on the wire, in half tiles: cell centres are x.5, and whole tiles cut them by up to 0.7 tiles, so the
    /// client drew units off the server's copy of its prediction (bug review 2026-11-05). The server snaps its routes to this grid
    /// (<see cref="Snap"/>), so both sides follow the same points exactly. A 1024 map is 2048 half tiles, inside a ushort.
    /// </summary>
    public static class RouteWire
    {
        /// <summary>Wire units per tile.</summary>
        public const float Scale = 2f;

        /// <summary>A position in wire units.</summary>
        public static int2 Encode(float2 p) => (int2)math.round(p * Scale);

        /// <summary>A wire point back in tiles (exact: halves are exact floats).</summary>
        public static float2 Decode(int2 w) => (float2)w / Scale;

        /// <summary>A position as the client will read it.</summary>
        public static float2 Snap(float2 p) => Decode(Encode(p));
    }

    public static class Messages
    {
        /// <summary>Most waypoints one route may carry.</summary>
        public const int MaxRouteWaypoints = 4096;

        /// <summary>Most waypoints one MoveOrder message may decode to (copy steps make routes cheap, so the payload no longer bounds them).</summary>
        public const int MaxMessageWaypoints = 1 << 17;

        /// <summary>
        /// Route step markers, read where a step's x byte would be: an absolute point follows, or (MoveOrder only, protocol 22) a copy
        /// of an earlier route's run in the same message. So a step's x delta is never -128 or -127.
        /// </summary>
        public const byte AbsoluteMarker = 0x80, CopyMarker = 0x81;

        /// <summary>The shortest run a copy step is used for: it costs about four bytes, a waypoint step two.</summary>
        public const int MinCopy = 3;

        // ---------- sizes, for packing ----------

        /// <summary>Bytes a route takes on the wire without copy steps (Enter).</summary>
        public static int RouteSize(NativeArray<int2> waypoints, int first, int count)
        {
            int size = VarInt.Size((uint)count) + 4;
            for (int i = 1; i < count; i++) size += IsStep(waypoints[first + i] - waypoints[first + i - 1]) ? 2 : 5;
            return size;
        }

        /// <summary>
        /// Where the MoveOrder message that starts at <paramref name="start"/> ends: as many routes as fit <see cref="WireLimits.ReliableChunk"/>
        /// bytes, each sized as written (its copy steps depend on the routes before it), and <see cref="MaxMessageWaypoints"/> waypoints,
        /// which the decoder holds a message to. Always at least one route.
        /// </summary>
        public static int MoveOrderChunkEnd(NativeArray<int2> tiles, NativeArray<RouteUnit> units, int start, ref RouteCopies copies)
        {
            copies.Clear();
            int index = start, size = 8, previous = 0, waypoints = 0;
            while (index < units.Length)
            {
                RouteUnit u = units[index];
                var counted = new CountingSink();
                copies.Write(ref counted, tiles, u.First, u.Count);
                int cost = VarInt.Size((uint)(u.Index - previous)) + counted.Position;
                if (index > start && (size + cost > WireLimits.ReliableChunk || waypoints + u.Count > MaxMessageWaypoints)) break;
                size += cost;
                waypoints += u.Count;
                previous = u.Index;
                index++;
            }
            return index;
        }

        /// <summary>True when a waypoint's delta from the last one fits a two-byte step.</summary>
        private static bool IsStep(int2 d) => d.x >= -126 && d.x <= 127 && d.y >= -127 && d.y <= 127;

        /// <summary>Bytes one Enter entry takes on the wire.</summary>
        public static int EnterUnitSize(NativeArray<int2> waypoints, in EnterUnit u, int previousIndex) =>
            VarInt.Size((uint)(NetIdAllocator.IndexOf(u.Id) - previousIndex)) + VarInt.Size((uint)NetIdAllocator.GenerationOf(u.Id))
            + 1 + VarInt.Size((uint)u.OwnerId) + 5 + RouteSize(waypoints, u.First, u.Count);

        /// <summary>Bytes one Correction entry takes on the wire.</summary>
        public static int CorrectionUnitSize(in CorrectionUnit u, int previousIndex) =>
            VarInt.Size((uint)(u.Index - previousIndex)) + VarInt.SizeInt(u.DX) + VarInt.SizeInt(u.DY) + VarInt.Size((uint)u.Resume) + 1;

        // ---------- encoders ----------

        /// <summary>Writes a route (<see cref="RouteWire"/> half tiles): the first point, then signed byte steps (an escape byte starts an absolute point).</summary>
        public static void WriteRoute<W>(ref W writer, NativeArray<int2> waypoints, int first, int count) where W : struct, IWireWriter
        {
            VarInt.Write(ref writer, (uint)count);
            int2 tile = waypoints[first];
            writer.WriteUShort((ushort)tile.x);
            writer.WriteUShort((ushort)tile.y);
            for (int i = 1; i < count; i++) WriteStep(ref writer, ref tile, waypoints[first + i]);
        }

        /// <summary>One waypoint after <paramref name="previous"/>: a two-byte step, or the absolute marker and the point.</summary>
        private static void WriteStep<W>(ref W writer, ref int2 previous, int2 tile) where W : struct, IWireWriter
        {
            int2 d = tile - previous;
            if (IsStep(d))
            {
                writer.WriteByte((byte)(sbyte)d.x);
                writer.WriteByte((byte)(sbyte)d.y);
            }
            else
            {
                writer.WriteByte(AbsoluteMarker);
                writer.WriteUShort((ushort)tile.x);
                writer.WriteUShort((ushort)tile.y);
            }
            previous = tile;
        }

        /// <summary>
        /// The routes already written to one MoveOrder message, so a later route can copy runs of them (protocol 22): units of one
        /// order walk the same corridor, so their routes share most of their waypoints. Remembers where each waypoint value last
        /// appeared (entry in the message, position in its route). Make one per message; it never reaches past the message.
        /// </summary>
        public struct RouteCopies : System.IDisposable
        {
            private NativeHashMap<int2, int2> lastSeen;
            private NativeList<int2> entries; // per route written: first, count in the waypoint array

            public RouteCopies(Allocator allocator)
            {
                lastSeen = new NativeHashMap<int2, int2>(256, allocator);
                entries = new NativeList<int2>(64, allocator);
            }

            /// <summary>Forgets every route (a new message starts).</summary>
            public void Clear() { lastSeen.Clear(); entries.Clear(); }

            public void Dispose() { lastSeen.Dispose(); entries.Dispose(); }

            /// <summary>
            /// Writes a route as <see cref="WriteRoute"/> does, except that any run of at least <see cref="MinCopy"/> waypoints that repeats
            /// an earlier route of the message becomes one copy step: <see cref="CopyMarker"/>, then varints back (routes before this
            /// one), start (in that route) and length. Then remembers the route for the ones after it.
            /// </summary>
            public void Write<W>(ref W writer, NativeArray<int2> waypoints, int first, int count) where W : struct, IWireWriter
            {
                int entry = entries.Length;
                VarInt.Write(ref writer, (uint)count);
                int2 tile = waypoints[first];
                writer.WriteUShort((ushort)tile.x);
                writer.WriteUShort((ushort)tile.y);
                for (int i = 1; i < count;)
                {
                    int2 next = waypoints[first + i];
                    if (lastSeen.TryGetValue(next, out int2 at))
                    {
                        int2 other = entries[at.x];
                        int run = 0;
                        while (i + run < count && at.y + run < other.y && waypoints[other.x + at.y + run].Equals(waypoints[first + i + run])) run++;
                        if (run >= MinCopy)
                        {
                            writer.WriteByte(CopyMarker);
                            VarInt.Write(ref writer, (uint)(entry - at.x));
                            VarInt.Write(ref writer, (uint)at.y);
                            VarInt.Write(ref writer, (uint)run);
                            i += run;
                            tile = waypoints[first + i - 1];
                            continue;
                        }
                    }
                    WriteStep(ref writer, ref tile, next);
                    i++;
                }
                entries.Add(new int2(first, count));
                for (int k = 0; k < count; k++) lastSeen[waypoints[first + k]] = new int2(entry, k);
            }
        }

        /// <summary>Encodes an Enter message for the given units.</summary>
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

        /// <summary>Encodes a MoveOrder message for the given units.</summary>
        public static void EncodeMoveOrder<W>(ref W writer, int tick, NativeArray<int2> waypoints, NativeArray<RouteUnit> units) where W : struct, IWireWriter
        {
            writer.WriteByte((byte)MessageType.MoveOrder);
            VarInt.Write(ref writer, (uint)tick);
            VarInt.Write(ref writer, (uint)units.Length);
            int previous = 0;
            var copies = new RouteCopies(Allocator.Temp);
            for (int i = 0; i < units.Length; i++)
            {
                RouteUnit u = units[i];
                VarInt.Write(ref writer, (uint)(u.Index - previous));
                previous = u.Index;
                copies.Write(ref writer, waypoints, u.First, u.Count);
            }
            copies.Dispose();
        }

        /// <summary>Encodes a Correction message for the given units.</summary>
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

        /// <summary>Encodes a Health message for the given units.</summary>
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

        /// <summary>The end (exclusive) of the Health message that starts at <paramref name="start"/>: as many units as fit <see cref="WireLimits.ReliableChunk"/>.</summary>
        public static int HealthChunkEnd(NativeArray<HealthUnit> units, int start)
        {
            int size = 4, previous = 0, index = start; // 4: the type and the count
            while (index < units.Length)
            {
                int cost = VarInt.Size((uint)(units[index].Index - previous)) + 1;
                if (index > start && size + cost > WireLimits.ReliableChunk) break;
                size += cost;
                previous = units[index].Index;
                index++;
            }
            return index;
        }

        /// <summary>Encodes a Leave message for the given units.</summary>
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

        /// <summary>
        /// Encodes an Attack message. Each event's first varint carries the attacker kind in its low bit (v0.13): 0 then
        /// the zigzag delta of a unit's id index (delta coded over the unit events only), 1 then a building's full id. The
        /// target follows, zigzag coded (v0.16: negative names a wall tile, <see cref="AttackTargetIds"/>).
        /// </summary>
        public static void EncodeAttack<W>(ref W writer, NativeArray<AttackEvent> events) where W : struct, IWireWriter
        {
            writer.WriteByte((byte)MessageType.Attack);
            VarInt.Write(ref writer, (uint)events.Length);
            int previous = 0;
            for (int i = 0; i < events.Length; i++)
            {
                if (events[i].AttackerKind == AttackerKinds.Building)
                    VarInt.Write(ref writer, (uint)events[i].AttackerId << 1 | 1u);
                else
                {
                    VarInt.Write(ref writer, VarInt.ZigZag(events[i].AttackerIndex - previous) << 1);
                    previous = events[i].AttackerIndex;
                }
                VarInt.WriteInt(ref writer, events[i].TargetId); // zigzag: a wall target is negative (v0.16)
            }
        }

        // ---------- decoders ----------

        /// <summary>The type of the next message, without consuming it.</summary>
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

        /// <summary>Reads a route into the waypoint list and returns its length; throws on a malformed route.</summary>
        public static int ReadRoute(NetworkReader reader, List<int2> waypoints)
        {
            int count = (int)VarInt.Read(reader);
            if (count < 1 || count > MaxRouteWaypoints || count * 2 + 2 > reader.Remaining) throw new InvalidDataException($"route of {count} waypoints");
            var tile = new int2(reader.ReadUShort(), reader.ReadUShort());
            waypoints.Add(tile);
            for (int i = 1; i < count; i++)
            {
                byte marker = reader.ReadByte();
                if (marker == AbsoluteMarker) tile = new int2(reader.ReadUShort(), reader.ReadUShort());
                else if (marker == CopyMarker) throw new InvalidDataException("a copy step outside a MoveOrder");
                else tile += new int2((sbyte)marker, reader.ReadSByte());
                waypoints.Add(tile);
            }
            return count;
        }

        /// <summary>
        /// Reads a MoveOrder route, whose copy steps (<see cref="RouteCopies"/>) repeat runs of the routes read before it from the same
        /// message (<paramref name="units"/> from <paramref name="firstOfMessage"/> on); returns its length. Throws on a malformed route,
        /// a copy that reaches outside those routes, or a message past <see cref="MaxMessageWaypoints"/>.
        /// </summary>
        public static int ReadRouteCopying(NetworkReader reader, List<int2> waypoints, List<RouteUnit> units, int firstOfMessage, int messageStart)
        {
            int count = (int)VarInt.Read(reader);
            if (count < 1 || count > MaxRouteWaypoints || 4 > reader.Remaining) throw new InvalidDataException($"route of {count} waypoints");
            if (waypoints.Count - messageStart + count > MaxMessageWaypoints) throw new InvalidDataException("a MoveOrder's routes are too long");
            var tile = new int2(reader.ReadUShort(), reader.ReadUShort());
            waypoints.Add(tile);
            for (int i = 1; i < count;)
            {
                byte marker = reader.ReadByte();
                if (marker == CopyMarker)
                {
                    uint back = VarInt.Read(reader), start = VarInt.Read(reader), run = VarInt.Read(reader);
                    int earlier = units.Count - firstOfMessage;
                    if (back < 1 || back > earlier) throw new InvalidDataException($"a copy from route {back} back of {earlier}");
                    RouteUnit from = units[units.Count - (int)back];
                    if (run < 1 || run > count - i || start >= from.Count || run > from.Count - start)
                        throw new InvalidDataException($"a copy of {run} from {start} in a route of {from.Count}");
                    for (int k = 0; k < run; k++) waypoints.Add(waypoints[from.First + (int)start + k]);
                    i += (int)run;
                    tile = waypoints[waypoints.Count - 1];
                    continue;
                }
                if (marker == AbsoluteMarker) tile = new int2(reader.ReadUShort(), reader.ReadUShort());
                else tile += new int2((sbyte)marker, reader.ReadSByte());
                waypoints.Add(tile);
                i++;
            }
            return count;
        }

        /// <summary>Decodes an Enter message. Throws <see cref="InvalidDataException"/> on malformed input.</summary>
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
                // Types index per-type tables on the client, some in Burst jobs without bounds checks.
                if (u.Type >= UnitTypeCount) throw new InvalidDataException($"unit type {u.Type}");
                u.Count = ReadRoute(reader, waypoints);
                units.Add(u);
            }
        }

        /// <summary>How many <see cref="UnitType"/> values there are: an Enter's type is below it.</summary>
        private static readonly int UnitTypeCount = System.Enum.GetValues(typeof(UnitType)).Length;

        /// <summary>Decodes a MoveOrder message and returns its tick.</summary>
        public static int DecodeMoveOrder(NetworkReader reader, List<RouteUnit> units, List<int2> waypoints)
        {
            Expect(reader, MessageType.MoveOrder);
            int tick = (int)VarInt.Read(reader);
            int count = Count(reader, 6);
            int previous = 0, firstOfMessage = units.Count, messageStart = waypoints.Count;
            for (int i = 0; i < count; i++)
            {
                var u = new RouteUnit { Index = NextIndex(reader, ref previous), First = waypoints.Count };
                u.Count = ReadRouteCopying(reader, waypoints, units, firstOfMessage, messageStart);
                units.Add(u);
            }
            return tick;
        }

        /// <summary>Decodes a Correction message and returns its tick.</summary>
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

        /// <summary>Decodes a Health message.</summary>
        public static void DecodeHealth(NetworkReader reader, List<HealthUnit> units)
        {
            Expect(reader, MessageType.Health);
            int count = Count(reader, 2);
            int previous = 0;
            for (int i = 0; i < count; i++)
                units.Add(new HealthUnit { Index = NextIndex(reader, ref previous), Health = reader.ReadByte() });
        }

        /// <summary>Decodes a Leave message.</summary>
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

        /// <summary>Decodes an Attack message.</summary>
        public static void DecodeAttack(NetworkReader reader, List<AttackEvent> events)
        {
            Expect(reader, MessageType.Attack);
            int count = Count(reader, 2);
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                uint head = VarInt.Read(reader);
                if ((head & 1) != 0)
                {
                    uint id = head >> 1;
                    if (id == 0 || id > int.MaxValue) throw new InvalidDataException("bad attacker building id");
                    events.Add(new AttackEvent { AttackerKind = AttackerKinds.Building, AttackerId = (int)id, TargetId = VarInt.ReadInt(reader) });
                    continue;
                }
                int index = previous + VarInt.UnZigZag(head >> 1);
                if ((uint)index >= NetIdAllocator.MaxCapacity) throw new InvalidDataException("attacker index out of range");
                previous = index;
                events.Add(new AttackEvent { AttackerIndex = index, TargetId = VarInt.ReadInt(reader) });
            }
        }
    }
}

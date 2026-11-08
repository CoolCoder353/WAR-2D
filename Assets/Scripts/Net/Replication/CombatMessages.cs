using WAR2D.Dev.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using Mirror;
using Unity.Mathematics;

namespace WAR2D.Net.Replication
{
    /// <summary>
    /// v0.20 Onslaught: one client's combat news for a tick (status bits, leaps, aims and shells), built by
    /// <see cref="ReplicationService"/> from the settled world under the leak rule (spec §1, §4.3, §5.3) and sent after the tick's
    /// unit batches, so every unit it names is already known to the client.
    /// </summary>
    public struct CombatBatch : NetworkMessage
    {
        public ArraySegment<byte> Payload;
    }

    /// <summary>A known unit's status bits (<c>UnitStatusBits</c>); 0 clears them.</summary>
    public struct StatusEntry
    {
        public int Index;
        public byte Bits;
    }

    /// <summary>A Bomber's leap: its id index, the line it flies and the ticks it leaves the ground and lands.</summary>
    public struct LeapEntry
    {
        public int Index;
        public float2 From, To;
        public int Start, End;
    }

    /// <summary>A Sniper starting to aim: its id index and its target (a full id; 0 when the client may not know it).</summary>
    public struct AimEntry
    {
        public int Index;
        public int TargetId;
    }

    /// <summary>
    /// A shell: the whole flight (<see cref="Full"/>: from its gun, launched at <see cref="Launch"/>) for a client that saw it fired, or
    /// only its landing point and tick for one that sees just where it lands (spec §4.3: drawn falling steeply, so the gun's
    /// direction isn't given away).
    /// </summary>
    public struct ShellEntry
    {
        public bool Full;
        public float2 From, To;
        public int Launch, Land;
    }

    /// <summary>What a <see cref="CombatBatch"/> carries, decoded.</summary>
    public sealed class CombatNews
    {
        public int Tick;
        public readonly List<StatusEntry> Statuses = new List<StatusEntry>();
        public readonly List<LeapEntry> Leaps = new List<LeapEntry>();
        public readonly List<AimEntry> Aims = new List<AimEntry>();
        public readonly List<ShellEntry> Shells = new List<ShellEntry>();

        public bool Empty => Statuses.Count == 0 && Leaps.Count == 0 && Aims.Count == 0 && Shells.Count == 0;

        public void Clear()
        {
            Tick = 0;
            Statuses.Clear();
            Leaps.Clear();
            Aims.Clear();
            Shells.Clear();
        }
    }

    /// <summary>
    /// The <see cref="CombatBatch"/> payload: the tick, then four counted lists (statuses and leaps and aims by ascending id index,
    /// delta coded; shells by landing tick). Positions are <see cref="Quantise"/> ushorts. The decoder throws
    /// <see cref="InvalidDataException"/> or <see cref="EndOfStreamException"/> on malformed input and never allocates unbounded memory.
    /// </summary>
    public static class CombatCodec
    {
        /// <summary>Most entries of one kind a payload may carry.</summary>
        public const int MaxEntries = 16384;

        /// <summary>Writes <paramref name="news"/> (its lists sorted as the format needs) into <paramref name="writer"/>.</summary>
        public static void Write(NetworkWriter writer, CombatNews news)
        {
            WriteVar(writer, (uint)news.Tick);
            news.Statuses.Sort((a, b) => a.Index.CompareTo(b.Index));
            WriteVar(writer, (uint)news.Statuses.Count);
            int previous = 0;
            foreach (StatusEntry s in news.Statuses)
            {
                WriteVar(writer, (uint)(s.Index - previous));
                previous = s.Index;
                writer.WriteByte(s.Bits);
            }
            news.Leaps.Sort((a, b) => a.Index.CompareTo(b.Index));
            WriteVar(writer, (uint)news.Leaps.Count);
            previous = 0;
            foreach (LeapEntry l in news.Leaps)
            {
                WriteVar(writer, (uint)(l.Index - previous));
                previous = l.Index;
                WritePoint(writer, l.From);
                WritePoint(writer, l.To);
                WriteVar(writer, (uint)l.Start);
                WriteVar(writer, (uint)math.max(0, l.End - l.Start));
            }
            news.Aims.Sort((a, b) => a.Index.CompareTo(b.Index));
            WriteVar(writer, (uint)news.Aims.Count);
            previous = 0;
            foreach (AimEntry a in news.Aims)
            {
                WriteVar(writer, (uint)(a.Index - previous));
                previous = a.Index;
                WriteVar(writer, VarInt.ZigZag(a.TargetId));
            }
            WriteVar(writer, (uint)news.Shells.Count);
            foreach (ShellEntry s in news.Shells)
            {
                writer.WriteByte((byte)(s.Full ? 1 : 0));
                if (s.Full)
                {
                    WritePoint(writer, s.From);
                    WriteVar(writer, (uint)s.Launch);
                }
                WritePoint(writer, s.To);
                WriteVar(writer, (uint)s.Land);
            }
        }

        /// <summary>Decodes a payload into <paramref name="news"/> (cleared first).</summary>
        public static void Read(ArraySegment<byte> payload, CombatNews news)
        {
            news.Clear();
            using NetworkReaderPooled reader = NetworkReaderPool.Get(payload);
            news.Tick = (int)VarInt.Read(reader);
            int count = Count(reader, 2);
            int previous = 0;
            for (int i = 0; i < count; i++)
                news.Statuses.Add(new StatusEntry { Index = Next(reader, ref previous), Bits = reader.ReadByte() });
            count = Count(reader, 11);
            previous = 0;
            for (int i = 0; i < count; i++)
            {
                var l = new LeapEntry { Index = Next(reader, ref previous), From = ReadPoint(reader), To = ReadPoint(reader), Start = (int)VarInt.Read(reader) };
                uint ticks = VarInt.Read(reader);
                if (ticks > 1000) throw new InvalidDataException("leap too long");
                l.End = l.Start + (int)ticks;
                news.Leaps.Add(l);
            }
            count = Count(reader, 2);
            previous = 0;
            for (int i = 0; i < count; i++)
                news.Aims.Add(new AimEntry { Index = Next(reader, ref previous), TargetId = VarInt.UnZigZag(VarInt.Read(reader)) });
            count = Count(reader, 6);
            for (int i = 0; i < count; i++)
            {
                byte full = reader.ReadByte();
                if (full > 1) throw new InvalidDataException("bad shell kind");
                var s = new ShellEntry { Full = full == 1 };
                if (s.Full)
                {
                    s.From = ReadPoint(reader);
                    s.Launch = (int)VarInt.Read(reader);
                }
                s.To = ReadPoint(reader);
                s.Land = (int)VarInt.Read(reader);
                if (s.Full && s.Land < s.Launch) throw new InvalidDataException("shell lands before it is fired");
                news.Shells.Add(s);
            }
        }

        private static void WriteVar(NetworkWriter writer, uint value) => VarInt.Write(writer, value);

        private static void WritePoint(NetworkWriter writer, float2 p)
        {
            writer.WriteUShort(Quantise.Encode(p.x));
            writer.WriteUShort(Quantise.Encode(p.y));
        }

        private static float2 ReadPoint(NetworkReader reader) => new float2(Quantise.Decode(reader.ReadUShort()), Quantise.Decode(reader.ReadUShort()));

        private static int Count(NetworkReader reader, int minBytesEach)
        {
            uint count = VarInt.Read(reader);
            if (count > MaxEntries || count > (uint)(reader.Remaining / math.max(1, minBytesEach))) throw new InvalidDataException($"count {count} exceeds the payload");
            return (int)count;
        }

        private static int Next(NetworkReader reader, ref int previous)
        {
            long index = (long)previous + VarInt.Read(reader);
            if (index >= NetIdAllocator.MaxCapacity) throw new InvalidDataException("unit index out of range");
            previous = (int)index;
            return previous;
        }
    }

    /// <summary>
    /// The client's copy of the combat news (v0.20): each known unit's status bits, and this match's leaps, aims and shells for the
    /// drawing to pick up (<see cref="Received"/>). Status is forgotten when a unit leaves (<see cref="Forget"/>).
    /// </summary>
    public sealed class ClientCombat
    {
        /// <summary>The live match's (or replay's) combat news.</summary>
        public static ClientCombat Current { get; private set; } = new ClientCombat();

        /// <summary>Raised with each decoded batch (its lists are reused: read them during the call).</summary>
        public static event Action<CombatNews> Received;

        private readonly Dictionary<int, byte> status = new Dictionary<int, byte>();
        private readonly CombatNews scratch = new CombatNews();

        /// <summary>A unit's status bits by id index (0 when none).</summary>
        public byte StatusOf(int index) => status.TryGetValue(index, out byte bits) ? bits : (byte)0;

        /// <summary>Units with any status bit set, by id index.</summary>
        public IReadOnlyDictionary<int, byte> Statuses => status;

        /// <summary>Drops a unit's status (it left the client's knowledge or died).</summary>
        public void Forget(int index) => status.Remove(index);

        /// <summary>
        /// Applies a payload; false (nothing applied) when it is malformed. <paramref name="quiet"/> (a replay seeking) keeps the
        /// statuses but raises nothing to draw, so a skipped span's shells and leaps don't all play at once.
        /// </summary>
        public bool Apply(ArraySegment<byte> payload, bool quiet = false)
        {
            try { CombatCodec.Read(payload, scratch); }
            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException)
            {
                return false;
            }
            foreach (StatusEntry s in scratch.Statuses)
            {
                if (s.Bits == 0) status.Remove(s.Index);
                else status[s.Index] = s.Bits;
            }
            if (!quiet) Received?.Invoke(scratch);
            return true;
        }

        /// <summary>The network handler.</summary>
        private static readonly Unity.Profiling.ProfilerMarker ReceiveMarker = new Unity.Profiling.ProfilerMarker("Receive.Combat");

        public static void Receive(CombatBatch batch)
        {
            using var marker = ReceiveMarker.Auto();
            if (!Current.Apply(batch.Payload)) WarLog.Warn(LogCategory.Replication, "dropped a malformed combat batch");
        }

        /// <summary>Forgets everything (a new match, or leaving one).</summary>
        public static void Clear() => Current = new ClientCombat();
    }
}

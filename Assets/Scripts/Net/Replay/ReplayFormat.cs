using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace WAR2D.Net.Replay
{
    public enum ReplayChannel : byte { UnitsReliable = 0, UnitsUnreliable = 1, Buildings = 2, Terrain = 3, Fog = 4, Explosions = 10, Blasts = 11, Timeline = 12, End = 13 }

    public sealed class ReplayPlayerInfo { public int OwnerId; public string Nickname; public int Colour; public int Team; }

    /// <summary>A recording's header: what the viewer needs to rebuild the map and label the players. Never Steam IDs.</summary>
    public sealed class ReplayHeader
    {
        public int Format = ReplayFile.Format;
        public string GameVersion;
        public int MapSize;
        public uint Seed;
        public MatchSettings Settings;
        public List<ReplayPlayerInfo> Players = new List<ReplayPlayerInfo>();
        public double StartedUtc;
        public float TickSeconds;
    }

    /// <summary>File layout: GZip of ("W2DR", u16 format, u32 header length, header JSON, records). Record: varint tick delta, u8 channel, varint length, payload.</summary>
    public static class ReplayFile
    {
        public const int Format = 1;
        public const string Extension = ".war2drec";
        public static readonly byte[] Magic = { (byte)'W', (byte)'2', (byte)'D', (byte)'R' };
        public const int MaxRecordBytes = 4 << 20;
        public const int MaxHeaderBytes = 1 << 20;
    }

    public sealed class ReplayWriter : IDisposable
    {
        private readonly GZipStream gzip;
        private readonly BinaryWriter writer;
        private int lastTick;

        public long BytesWritten { get; private set; }

        public ReplayWriter(Stream output, ReplayHeader header, bool leaveOpen = false)
        {
            gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen);
            writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: false);
            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(header));
            writer.Write(ReplayFile.Magic);
            writer.Write((ushort)ReplayFile.Format);
            writer.Write((uint)json.Length);
            writer.Write(json);
            BytesWritten = 10 + json.Length;
        }

        public void Write(int tick, ReplayChannel channel, ReadOnlySpan<byte> payload)
        {
            int delta = Math.Max(0, tick - lastTick);
            lastTick = Math.Max(lastTick, tick);
            // Order matters (the reader expects it): tick delta, channel, length, payload.
            int n = WriteVarint(delta);
            writer.Write((byte)channel);
            n += 1 + WriteVarint(payload.Length);
            writer.Write(payload);
            BytesWritten += n + payload.Length;
        }

        private int WriteVarint(int value)
        {
            uint v = (uint)value;
            int n = 0;
            while (v >= 0x80) { writer.Write((byte)(v | 0x80)); v >>= 7; n++; }
            writer.Write((byte)v);
            return n + 1;
        }

        public void Dispose() => writer.Dispose(); // closes the GZip stream (and the output unless leaveOpen)
    }

    public sealed class ReplayReader : IDisposable
    {
        private readonly BinaryReader reader;
        private byte[] buffer = new byte[64 * 1024];
        private int tick;

        public ReplayHeader Header { get; private set; }
        /// <summary>Why reading stopped early, or null at a clean end.</summary>
        public string Error { get; private set; }

        private ReplayReader(BinaryReader reader) { this.reader = reader; }

        public static bool TryOpen(Stream input, out ReplayReader replay, out string error)
        {
            replay = null;
            try
            {
                var r = new BinaryReader(new GZipStream(input, CompressionMode.Decompress), Encoding.UTF8);
                byte[] magic = r.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != 'W' || magic[1] != '2' || magic[2] != 'D' || magic[3] != 'R') { error = "not a WAR-2D replay"; return false; }
                int format = r.ReadUInt16();
                if (format != ReplayFile.Format) { error = $"replay format {format} is not supported"; return false; }
                uint length = r.ReadUInt32();
                if (length > ReplayFile.MaxHeaderBytes) { error = "header too large"; return false; }
                byte[] json = r.ReadBytes((int)length);
                if (json.Length != length) { error = "truncated header"; return false; }
                ReplayHeader header = JsonConvert.DeserializeObject<ReplayHeader>(Encoding.UTF8.GetString(json));
                if (header == null || header.MapSize < 0 || header.TickSeconds <= 0f) { error = "bad header"; return false; }
                replay = new ReplayReader(r) { Header = header };
                error = null;
                return true;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is EndOfStreamException || e is ArgumentException)
            {
                error = "unreadable replay: " + e.Message;
                return false;
            }
        }

        /// <summary>The next record; false at the end or on a bad record (then <see cref="Error"/> says why). The payload is valid until the next call.</summary>
        public bool TryNext(out int recordTick, out ReplayChannel channel, out ArraySegment<byte> payload)
        {
            recordTick = 0; channel = 0; payload = default;
            if (Error != null) return false;
            try
            {
                if (!TryVarint(out int delta, allowEnd: true)) return false;
                tick += delta;
                channel = (ReplayChannel)reader.ReadByte();
                if (!TryVarint(out int length, allowEnd: false)) return false;
                if (length < 0 || length > ReplayFile.MaxRecordBytes) { Error = "record too large"; return false; }
                if (buffer.Length < length) buffer = new byte[Math.Max(length, buffer.Length * 2)];
                int read = 0;
                while (read < length) { int n = reader.Read(buffer, read, length - read); if (n <= 0) { Error = "truncated record"; return false; } read += n; }
                recordTick = tick;
                payload = new ArraySegment<byte>(buffer, 0, length);
                return true;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException)
            {
                Error = "unreadable record: " + e.Message;
                return false;
            }
        }

        private bool TryVarint(out int value, bool allowEnd)
        {
            value = 0;
            int shift = 0;
            while (true)
            {
                int b = reader.BaseStream.ReadByte();
                if (b < 0) { if (!(allowEnd && shift == 0)) Error = "truncated record"; return false; }
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift > 28) { Error = "bad varint"; return false; }
            }
        }

        public void Dispose() => reader.Dispose();
    }
}

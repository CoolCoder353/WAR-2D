using System.IO;
using Mirror;
using Unity.Collections;

namespace WAR2D.Net.Replication
{
    /// <summary>LEB128 variable-length integers (7 bits a byte) with zig-zag for signed values.</summary>
    public static class VarInt
    {
        /// <summary>Writes an unsigned varint to a wire writer.</summary>
        public static void Write<W>(ref W writer, uint value) where W : struct, IWireWriter
        {
            while (value >= 0x80)
            {
                writer.WriteByte((byte)(value | 0x80u));
                value >>= 7;
            }
            writer.WriteByte((byte)value);
        }

        /// <summary>Writes an unsigned varint to a Mirror writer.</summary>
        public static void Write(NetworkWriter writer, uint value)
        {
            var sink = new NetworkSink(writer);
            Write(ref sink, value);
        }

        /// <summary>Reads a varint; throws <see cref="EndOfStreamException"/> past five bytes or past the end.</summary>
        public static uint Read(NetworkReader reader)
        {
            uint value = 0;
            int shift = 0;
            while (true)
            {
                byte b = reader.ReadByte();
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                shift += 7;
                if (shift > 28) throw new EndOfStreamException("varint is longer than five bytes");
            }
        }

        /// <summary>Bytes an unsigned varint takes.</summary>
        public static int Size(uint value)
        {
            int size = 1;
            while (value >= 0x80) { value >>= 7; size++; }
            return size;
        }

        /// <summary>Writes a signed varint (zig-zag) to a wire writer.</summary>
        public static void WriteInt<W>(ref W writer, int value) where W : struct, IWireWriter => Write(ref writer, ZigZag(value));
        /// <summary>Writes a signed varint (zig-zag) to a Mirror writer.</summary>
        public static void WriteInt(NetworkWriter writer, int value) => Write(writer, ZigZag(value));
        /// <summary>Reads a signed varint (zig-zag).</summary>
        public static int ReadInt(NetworkReader reader) => UnZigZag(Read(reader));
        /// <summary>Bytes a signed varint takes.</summary>
        public static int SizeInt(int value) => Size(ZigZag(value));
        /// <summary>Maps a signed value to an unsigned one so small magnitudes stay small.</summary>
        public static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));
        /// <summary>The inverse of <see cref="ZigZag"/>.</summary>
        public static int UnZigZag(uint value) => (int)(value >> 1) ^ -(int)(value & 1);
    }

    /// <summary>A byte sink the encoders write through: Mirror's writer or a native list inside a job.</summary>
    public interface IWireWriter
    {
        int Position { get; }
        void WriteByte(byte value);
        void WriteUShort(ushort value);
    }

    /// <summary>Writes into a Mirror <see cref="NetworkWriter"/>.</summary>
    public struct NetworkSink : IWireWriter
    {
        private readonly NetworkWriter writer;
        public NetworkSink(NetworkWriter writer) => this.writer = writer;
        /// <summary>Bytes written so far.</summary>
        public int Position => writer.Position;
        /// <summary>Appends one byte.</summary>
        public void WriteByte(byte value) => writer.WriteByte(value);
        /// <summary>Appends a little-endian ushort.</summary>
        public void WriteUShort(ushort value) => writer.WriteUShort(value);
    }

    /// <summary>Appends to a <see cref="NativeList{T}"/> of bytes, little-endian (Mirror's order).</summary>
    public struct NativeSink : IWireWriter
    {
        /// <summary>The bytes written.</summary>
        public NativeList<byte> Bytes;
        public NativeSink(NativeList<byte> bytes) => Bytes = bytes;
        /// <summary>Bytes written so far.</summary>
        public int Position => Bytes.Length;
        /// <summary>Appends one byte.</summary>
        public void WriteByte(byte value) => Bytes.Add(value);
        /// <summary>Appends a little-endian ushort.</summary>
        public void WriteUShort(ushort value)
        {
            Bytes.Add((byte)value);
            Bytes.Add((byte)(value >> 8));
        }
    }

    /// <summary>Mirror and KCP's size limits (KCP MTU 1200), and the wire-cost model the perf gate uses.</summary>
    public static class WireLimits
    {
        /// <summary>KCP's maximum transmission unit in bytes.</summary>
        public const int Mtu = 1200;
        /// <summary>Mirror's per-message header bytes.</summary>
        public const int MetadataSize = 5;
        /// <summary>KCP's per-segment header bytes.</summary>
        public const int SegmentOverhead = 24;
        /// <summary>UDP and IPv4 header bytes per datagram.</summary>
        public const int UdpIpv4Overhead = 28;
        /// <summary>Largest unreliable message Mirror will send.</summary>
        public const int UnreliableMaxMessageSize = Mtu - MetadataSize - 1;
        /// <summary>Largest correction message the encoder packs, leaving room for the batch header.</summary>
        public const int CorrectionMessageLimit = UnreliableMaxMessageSize - 64;
        /// <summary>Largest reliable message (KCP fragments it into at most 254 segments).</summary>
        public const int ReliableMaxMessageSize = (Mtu - SegmentOverhead - MetadataSize) * (255 - 1) - 1;
        /// <summary>Reliable chunk the encoder packs a message type into (well under Mirror's ~16 KB message cap).</summary>
        public const int ReliableChunk = 12 * 1024;

        /// <summary>Approximate bytes on the wire for a payload sent as one batch.</summary>
        public static long WireBytes(int payloadBytes)
        {
            int segmentPayload = Mtu - SegmentOverhead - MetadataSize;
            int segments = (payloadBytes + segmentPayload - 1) / segmentPayload;
            return payloadBytes + (long)(SegmentOverhead + UdpIpv4Overhead) * segments;
        }
    }

    /// <summary>Positions on the wire: 1/16 tile in a ushort (maps up to 4095 tiles).</summary>
    public static class Quantise
    {
        /// <summary>Wire units per tile.</summary>
        public const float Scale = 16f;
        /// <summary>A tile coordinate as wire units (clamped to the ushort range).</summary>
        public static ushort Encode(float tile) => (ushort)Unity.Mathematics.math.clamp(Unity.Mathematics.math.round(tile * Scale), 0f, ushort.MaxValue);
        /// <summary>Wire units back to a tile coordinate.</summary>
        public static float Decode(ushort value) => value / Scale;
    }
}

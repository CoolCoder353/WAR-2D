using System.IO;
using Mirror;
using Unity.Collections;

namespace WAR2D.Net.Replication
{
    /// <summary>LEB128 variable-length integers (7 bits a byte) with zig-zag for signed values.</summary>
    public static class VarInt
    {
        public static void Write<W>(ref W writer, uint value) where W : struct, IWireWriter
        {
            while (value >= 0x80)
            {
                writer.WriteByte((byte)(value | 0x80u));
                value >>= 7;
            }
            writer.WriteByte((byte)value);
        }

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

        public static int Size(uint value)
        {
            int size = 1;
            while (value >= 0x80) { value >>= 7; size++; }
            return size;
        }

        public static void WriteInt<W>(ref W writer, int value) where W : struct, IWireWriter => Write(ref writer, ZigZag(value));
        public static void WriteInt(NetworkWriter writer, int value) => Write(writer, ZigZag(value));
        public static int ReadInt(NetworkReader reader) => UnZigZag(Read(reader));
        public static int SizeInt(int value) => Size(ZigZag(value));
        public static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));
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
        public int Position => writer.Position;
        public void WriteByte(byte value) => writer.WriteByte(value);
        public void WriteUShort(ushort value) => writer.WriteUShort(value);
    }

    /// <summary>Appends to a <see cref="NativeList{T}"/> of bytes, little-endian (Mirror's order).</summary>
    public struct NativeSink : IWireWriter
    {
        public NativeList<byte> Bytes;
        public NativeSink(NativeList<byte> bytes) => Bytes = bytes;
        public int Position => Bytes.Length;
        public void WriteByte(byte value) => Bytes.Add(value);
        public void WriteUShort(ushort value)
        {
            Bytes.Add((byte)value);
            Bytes.Add((byte)(value >> 8));
        }
    }

    /// <summary>Mirror and KCP's size limits (KCP MTU 1200), and the wire-cost model the perf gate uses.</summary>
    public static class WireLimits
    {
        public const int Mtu = 1200;
        public const int MetadataSize = 5;
        public const int SegmentOverhead = 24;
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
        public const float Scale = 16f;
        public static ushort Encode(float tile) => (ushort)Unity.Mathematics.math.clamp(Unity.Mathematics.math.round(tile * Scale), 0f, ushort.MaxValue);
        public static float Decode(ushort value) => value / Scale;
    }
}

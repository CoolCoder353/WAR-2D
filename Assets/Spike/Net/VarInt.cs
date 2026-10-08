using System.IO;
using Mirror;

namespace WAR2D.Spike
{
    /// <summary>
    /// LEB128 variable-length integers, the spike replication format's id and count encoding. A value
    /// is sent seven bits at a time, low bits first, with the high bit set on every byte that is
    /// followed by another: 0 to 127 take one byte, 128 to 16,383 two, and <see cref="uint.MaxValue"/>
    /// five. Signed values go through the usual zigzag so small negatives stay one byte.
    ///
    /// <para>The encoders run on the main thread after the tick's jobs complete, so they write into
    /// Mirror's managed <see cref="NetworkWriter"/>. <see cref="Size"/> lets a packer know a value's
    /// cost before writing it, which is what keeps the unreliable channel's messages inside the MTU.</para>
    /// </summary>
    public static class VarInt
    {
        /// <summary>Appends <paramref name="value"/> as a LEB128 varint.</summary>
        public static void Write(NetworkWriter writer, uint value)
        {
            var sink = new NetworkSink(writer);
            Write(ref sink, value);
        }

        /// <summary>Appends <paramref name="value"/> as a LEB128 varint to any byte sink.</summary>
        public static void Write<W>(ref W writer, uint value) where W : struct, ISpikeWriter
        {
            while (value >= 0x80)
            {
                writer.WriteByte((byte)(value | 0x80u));
                value >>= 7;
            }
            writer.WriteByte((byte)value);
        }

        /// <summary>Reads one LEB128 varint. Throws on a value longer than five bytes (32 bits plus slack).</summary>
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

        /// <summary>Bytes <see cref="Write"/> would append for <paramref name="value"/>.</summary>
        public static int Size(uint value)
        {
            int size = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                size++;
            }
            return size;
        }

        /// <summary>Appends a signed value as a zigzag varint.</summary>
        public static void WriteInt(NetworkWriter writer, int value) => Write(writer, ZigZag(value));

        /// <summary>Appends a signed value as a zigzag varint to any byte sink.</summary>
        public static void WriteInt<W>(ref W writer, int value) where W : struct, ISpikeWriter => Write(ref writer, ZigZag(value));

        /// <summary>Reads a zigzag varint.</summary>
        public static int ReadInt(NetworkReader reader) => UnZigZag(Read(reader));

        /// <summary>Bytes <see cref="WriteInt"/> would append for <paramref name="value"/>.</summary>
        public static int SizeInt(int value) => Size(ZigZag(value));

        /// <summary>Maps a signed value onto an unsigned one that keeps small magnitudes small.</summary>
        public static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));

        /// <summary>The signed value <see cref="ZigZag"/> encoded.</summary>
        public static int UnZigZag(uint value) => (int)(value >> 1) ^ -(int)(value & 1);
    }

    /// <summary>
    /// Where encoded bytes go. Mirror's <see cref="NetworkWriter"/> is managed, so the message writers
    /// are generic over this: tests and the soak write into a NetworkWriter through
    /// <see cref="NetworkSink"/>, and the Burst encoder writes into a native list through
    /// <see cref="NativeSink"/>. Multi-byte values are little-endian, as Mirror writes them.
    /// </summary>
    public interface ISpikeWriter
    {
        int Position { get; }
        void WriteByte(byte value);
        void WriteUShort(ushort value);
    }

    /// <summary>A <see cref="NetworkWriter"/> as a sink (managed; not for Burst).</summary>
    public struct NetworkSink : ISpikeWriter
    {
        private readonly NetworkWriter writer;
        public NetworkSink(NetworkWriter writer) => this.writer = writer;
        public int Position => writer.Position;
        public void WriteByte(byte value) => writer.WriteByte(value);
        public void WriteUShort(ushort value) => writer.WriteUShort(value);
    }

    /// <summary>A native byte list as a sink, for Burst jobs.</summary>
    public struct NativeSink : ISpikeWriter
    {
        public Unity.Collections.NativeList<byte> Bytes;
        public NativeSink(Unity.Collections.NativeList<byte> bytes) => Bytes = bytes;
        public int Position => Bytes.Length;
        public void WriteByte(byte value) => Bytes.Add(value);
        public void WriteUShort(ushort value)
        {
            Bytes.Add((byte)value);
            Bytes.Add((byte)(value >> 8));
        }
    }
}

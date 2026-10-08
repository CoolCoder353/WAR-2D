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

        /// <summary>Reads a zigzag varint.</summary>
        public static int ReadInt(NetworkReader reader) => UnZigZag(Read(reader));

        /// <summary>Bytes <see cref="WriteInt"/> would append for <paramref name="value"/>.</summary>
        public static int SizeInt(int value) => Size(ZigZag(value));

        /// <summary>Maps a signed value onto an unsigned one that keeps small magnitudes small.</summary>
        public static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));

        /// <summary>The signed value <see cref="ZigZag"/> encoded.</summary>
        public static int UnZigZag(uint value) => (int)(value >> 1) ^ -(int)(value & 1);
    }
}

using System;
using System.IO;
using UnityEngine;

namespace WAR2D.Art
{
    /// <summary>
    /// A minimal, deterministic PNG writer: 8-bit RGBA, no filtering, zlib "stored" (uncompressed) blocks.
    /// Byte-identical output for identical pixels, with no dependence on Unity's encoder or its version, so
    /// committed art only changes when the art does. Files are larger than Unity's, which is fine for sprites.
    /// </summary>
    public static class Png
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static uint[] crcTable;

        /// <summary>Encodes <paramref name="pixels"/> (bottom-up rows, as Unity textures) as a PNG.</summary>
        public static byte[] Encode(int width, int height, Color32[] pixels)
        {
            if (width <= 0 || height <= 0 || pixels == null || pixels.Length != width * height)
                throw new ArgumentException("pixels must be width × height");

            // Raw scanlines, top row first, each prefixed by filter type 0.
            int stride = width * 4 + 1;
            var raw = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                int row = (height - 1 - y) * stride; // flip: PNG rows run top-down
                raw[row] = 0;
                for (int x = 0; x < width; x++)
                {
                    Color32 c = pixels[y * width + x];
                    int o = row + 1 + x * 4;
                    raw[o] = c.r; raw[o + 1] = c.g; raw[o + 2] = c.b; raw[o + 3] = c.a;
                }
            }

            using var png = new MemoryStream();
            png.Write(Signature, 0, Signature.Length);
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;  // bit depth
            header[9] = 6;  // colour type: RGBA
            header[10] = 0; // deflate
            header[11] = 0; // adaptive filtering (only type 0 is used)
            header[12] = 0; // no interlace
            WriteChunk(png, "IHDR", header);
            WriteChunk(png, "IDAT", Zlib(raw));
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        /// <summary>A zlib stream of stored deflate blocks (at most 65,535 bytes each) and its Adler-32.</summary>
        private static byte[] Zlib(byte[] data)
        {
            using var z = new MemoryStream();
            z.WriteByte(0x78); // deflate, 32 KB window
            z.WriteByte(0x01); // no preset dictionary, fastest; (0x7801 % 31 == 0)
            int offset = 0;
            do
            {
                int length = Math.Min(65535, data.Length - offset);
                bool last = offset + length >= data.Length;
                z.WriteByte((byte)(last ? 1 : 0));
                z.WriteByte((byte)(length & 0xFF));
                z.WriteByte((byte)(length >> 8));
                z.WriteByte((byte)(~length & 0xFF));
                z.WriteByte((byte)((~length >> 8) & 0xFF));
                z.Write(data, offset, length);
                offset += length;
            } while (offset < data.Length);

            uint a = 1, b = 0;
            foreach (byte d in data)
            {
                a = (a + d) % 65521;
                b = (b + a) % 65521;
            }
            var adler = new byte[4];
            WriteBigEndian(adler, 0, (b << 16) | a);
            z.Write(adler, 0, 4);
            return z.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, (uint)data.Length);
            s.Write(length, 0, 4);
            var typeBytes = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
            var crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, crc);
            s.Write(crcBytes, 0, 4);
        }

        private static uint Crc(uint crc, byte[] data)
        {
            if (crcTable == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                crcTable = table;
            }
            foreach (byte d in data) crc = crcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}

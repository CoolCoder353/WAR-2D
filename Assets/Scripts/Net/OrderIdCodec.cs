using System.Collections.Generic;
using System.IO;

/// <summary>
/// Unit id lists in order commands: ascending ids as delta varints (the first is the id itself).
/// Clients split a selection into chunks with <see cref="EncodeChunks"/>; the server rejects any chunk
/// that is malformed with <see cref="TryDecode"/>.
/// </summary>
public static class OrderIdCodec
{
    public const int MaxIdsPerChunk = 2048;
    public const int MaxChunkBytes = 8192;

    /// <summary>Encodes <paramref name="count"/> ascending ids starting at <paramref name="start"/>.</summary>
    public static byte[] Encode(IReadOnlyList<int> ascendingIds, int start, int count)
    {
        var bytes = new List<byte>(count * 2);
        int previous = 0;
        for (int i = start; i < start + count; i++)
        {
            WriteVarInt(bytes, (uint)(ascendingIds[i] - previous));
            previous = ascendingIds[i];
        }
        return bytes.ToArray();
    }

    /// <summary>Splits ascending ids into chunks of at most <see cref="MaxIdsPerChunk"/> ids and <see cref="MaxChunkBytes"/> bytes.</summary>
    public static List<byte[]> EncodeChunks(IReadOnlyList<int> ascendingIds)
    {
        var chunks = new List<byte[]>();
        int start = 0;
        while (start < ascendingIds.Count)
        {
            int count = 0, size = 0, previous = 0;
            while (start + count < ascendingIds.Count && count < MaxIdsPerChunk)
            {
                int id = ascendingIds[start + count];
                int cost = VarIntSize((uint)(id - previous));
                if (size + cost > MaxChunkBytes) break;
                size += cost;
                previous = id;
                count++;
            }
            chunks.Add(Encode(ascendingIds, start, count));
            start += count;
        }
        return chunks;
    }

    /// <summary>Decodes a chunk into <paramref name="into"/>; false (adding nothing) on any malformed input.</summary>
    public static bool TryDecode(byte[] bytes, int maxIds, List<int> into)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxChunkBytes) return false;
        int mark = into.Count;
        long previous = 0;
        int position = 0, count = 0;
        while (position < bytes.Length)
        {
            uint delta = 0;
            int shift = 0;
            while (true)
            {
                if (position >= bytes.Length || shift > 28) { into.RemoveRange(mark, into.Count - mark); return false; }
                byte b = bytes[position++];
                delta |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            long id = previous + delta;
            if (delta == 0 || id > int.MaxValue || ++count > maxIds) { into.RemoveRange(mark, into.Count - mark); return false; }
            into.Add((int)id);
            previous = id;
        }
        return true;
    }

    private static void WriteVarInt(List<byte> bytes, uint value)
    {
        while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; }
        bytes.Add((byte)value);
    }

    private static int VarIntSize(uint value)
    {
        int size = 1;
        while (value >= 0x80) { value >>= 7; size++; }
        return size;
    }
}

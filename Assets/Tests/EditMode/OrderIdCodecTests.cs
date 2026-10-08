using System.Collections.Generic;
using NUnit.Framework;

public class OrderIdCodecTests
{
    [Test]
    public void TenThousandIdsRoundTripAcrossChunks()
    {
        var ids = new List<int>();
        for (int i = 0; i < 10000; i++) ids.Add((1 << 20) + i * 8 + (i % 7));
        List<byte[]> chunks = OrderIdCodec.EncodeChunks(ids);
        Assert.That(chunks.Count, Is.EqualTo(5));
        var decoded = new List<int>();
        foreach (byte[] chunk in chunks)
        {
            Assert.LessOrEqual(chunk.Length, OrderIdCodec.MaxChunkBytes);
            Assert.IsTrue(OrderIdCodec.TryDecode(chunk, OrderIdCodec.MaxIdsPerChunk, decoded));
        }
        CollectionAssert.AreEqual(ids, decoded);
    }

    private static bool Decode(params byte[] bytes) => OrderIdCodec.TryDecode(bytes, OrderIdCodec.MaxIdsPerChunk, new List<int>());

    [Test] public void RejectsZeroDelta() => Assert.IsFalse(Decode(5, 0));
    [Test] public void RejectsZeroFirstId() => Assert.IsFalse(Decode(0));
    [Test] public void RejectsTruncatedVarint() => Assert.IsFalse(Decode(5, 0x80));
    [Test] public void RejectsVarintLongerThanFiveBytes() => Assert.IsFalse(Decode(0x81, 0x80, 0x80, 0x80, 0x80, 0x01));
    [Test] public void RejectsEmpty() => Assert.IsFalse(Decode());
    [Test] public void RejectsOverflowPastIntMax() => Assert.IsFalse(Decode(0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x02));

    [Test]
    public void RejectsMoreThanMaxIds()
    {
        var into = new List<int>();
        Assert.IsFalse(OrderIdCodec.TryDecode(new byte[] { 1, 1, 1 }, 2, into));
        Assert.IsEmpty(into, "a rejected chunk adds nothing");
    }

    [Test]
    public void RejectsChunkOverByteLimit() => Assert.IsFalse(OrderIdCodec.TryDecode(new byte[OrderIdCodec.MaxChunkBytes + 1], int.MaxValue, new List<int>()));
}

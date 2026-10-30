using System;
using System.IO;
using NUnit.Framework;
using WAR2D.Net.Replay;

public class ReplayFormatTests
{
    private static ReplayHeader Header() => new ReplayHeader { GameVersion = "0.10.0", MapSize = 256, Seed = 42, TickSeconds = 0.05f,
        Players = { new ReplayPlayerInfo { OwnerId = 5, Nickname = "A", Colour = 0, Team = 0 } } };

    [Test]
    public void RoundTripsHeaderAndRecords()
    {
        var ms = new MemoryStream();
        using (var w = new ReplayWriter(ms, Header(), leaveOpen: true))
        {
            w.Write(1, ReplayChannel.UnitsReliable, new byte[] { 1, 2, 3 });
            w.Write(1, ReplayChannel.Buildings, new byte[0]);
            w.Write(25, ReplayChannel.Explosions, new byte[] { 9 });
        }
        ms.Position = 0;
        Assert.IsTrue(ReplayReader.TryOpen(ms, out ReplayReader r, out string error), error);
        using (r)
        {
            Assert.AreEqual(42u, r.Header.Seed);
            Assert.AreEqual("A", r.Header.Players[0].Nickname);
            Assert.IsTrue(r.TryNext(out int t, out ReplayChannel c, out ArraySegment<byte> p));
            Assert.AreEqual((1, ReplayChannel.UnitsReliable, 3), (t, c, p.Count));
            Assert.IsTrue(r.TryNext(out t, out c, out p));
            Assert.AreEqual(0, p.Count);
            Assert.IsTrue(r.TryNext(out t, out c, out p));
            Assert.AreEqual((25, ReplayChannel.Explosions), (t, c));
            Assert.IsFalse(r.TryNext(out _, out _, out _));
            Assert.IsNull(r.Error);
        }
    }

    [Test]
    public void RejectsWrongMagic()
    {
        Assert.IsFalse(ReplayReader.TryOpen(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), out _, out string error));
        Assert.IsNotNull(error);
    }

    [Test]
    public void TruncatedOrFuzzedFilesNeverThrow([Values(1, 2, 3, 4, 5, 6, 7, 8)] int seed)
    {
        var ms = new MemoryStream();
        using (var w = new ReplayWriter(ms, Header(), leaveOpen: true))
            for (int i = 0; i < 200; i++) w.Write(i, (ReplayChannel)(i % 5), new byte[i % 37]);
        byte[] bytes = ms.ToArray();
        var rng = new Random(seed);
        byte[] cut = new byte[rng.Next(1, bytes.Length)];
        Array.Copy(bytes, cut, cut.Length);
        for (int i = 0; i < seed * 3; i++) cut[rng.Next(cut.Length)] ^= (byte)rng.Next(1, 256);
        Assert.DoesNotThrow(() =>
        {
            if (!ReplayReader.TryOpen(new MemoryStream(cut), out ReplayReader r, out _)) return;
            using (r) while (r.TryNext(out _, out _, out _)) { }
        });
    }

    [TestCase(1)]  // before v0.13's attack events with an attacker kind
    [TestCase(99)] // a newer build's
    public void RefusesOtherFormats(int format)
    {
        var raw = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(gzip))
        {
            byte[] json = System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(Header()));
            w.Write(ReplayFile.Magic);
            w.Write((ushort)format);
            w.Write((uint)json.Length);
            w.Write(json);
        }
        raw.Position = 0;
        Assert.AreEqual(2, ReplayFile.Format);
        Assert.IsFalse(ReplayReader.TryOpen(raw, out _, out string error));
        StringAssert.Contains($"replay format {format} is not supported", error);
    }
}

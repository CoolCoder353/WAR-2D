using System;
using System.IO;
using NUnit.Framework;
using WAR2D.Net.Replay;

public class ReplayTransferTests
{
    [Test]
    public void NothingIsSentBeforeGameOver()
    {
        foreach (GameState s in Enum.GetValues(typeof(GameState)))
            Assert.AreEqual(s == GameState.GameOver, ReplayRules.MaySend(s), s.ToString());
    }

    [Test]
    public void ChunksReassembleAndHashChecks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "w2d-xfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        byte[] data = new byte[100_000];
        new Random(3).NextBytes(data);
        string src = Path.Combine(dir, "src" + ReplayFile.Extension);
        File.WriteAllBytes(src, data);

        var offer = ReplaySender.MakeOffer(7, src, durationSeconds: 12f);
        var receiver = new ReplayReceiver(Path.Combine(dir, "in"));
        string saved = null;
        receiver.Saved += p => saved = p;
        receiver.OnOffer(offer, accept: true);
        foreach (ReplayChunk chunk in ReplaySender.Chunks(7, src, 16 * 1024)) receiver.OnChunk(chunk);
        Assert.IsNotNull(saved);
        CollectionAssert.AreEqual(data, File.ReadAllBytes(saved));

        var bad = new ReplayReceiver(Path.Combine(dir, "bad"));
        string badSaved = null;
        bad.Saved += p => badSaved = p;
        bad.OnOffer(offer, accept: true);
        foreach (ReplayChunk chunk in ReplaySender.Chunks(7, src, 16 * 1024))
        {
            if (chunk.Offset == 16 * 1024) chunk.Bytes.Array[chunk.Bytes.Offset] ^= 0xFF;
            bad.OnChunk(chunk);
        }
        Assert.IsNull(badSaved, "a corrupted transfer is not saved");
        Directory.Delete(dir, true);
    }

    [Test]
    public void ChunksForAnotherOfferOrOutOfOrderAreIgnored()
    {
        string dir = Path.Combine(Path.GetTempPath(), "w2d-xfer2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "s" + ReplayFile.Extension);
        File.WriteAllBytes(src, new byte[40_000]);
        var receiver = new ReplayReceiver(Path.Combine(dir, "in"));
        receiver.OnOffer(ReplaySender.MakeOffer(1, src, 1f), accept: true);
        var chunks = new System.Collections.Generic.List<ReplayChunk>(ReplaySender.Chunks(1, src, 16 * 1024));
        Assert.DoesNotThrow(() => receiver.OnChunk(new ReplayChunk { Id = 2, Offset = 0, Bytes = chunks[0].Bytes }));
        Assert.DoesNotThrow(() => receiver.OnChunk(chunks[1])); // offset 16 KB before 0: dropped, transfer cancelled
        Assert.IsFalse(receiver.Active);
        Directory.Delete(dir, true);
    }
}

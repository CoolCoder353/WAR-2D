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
        string src = Path.Combine(dir, "src" + ReplayFile.Extension);
        WriteReplay(src, 100_000, seed: 3);
        byte[] data = File.ReadAllBytes(src);

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
    public void AReceivedFileThatIsNotAReplayIsDiscarded()
    {
        string dir = Path.Combine(Path.GetTempPath(), "w2d-xfer3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "junk" + ReplayFile.Extension);
        var junk = new byte[40_000];
        new Random(5).NextBytes(junk);
        File.WriteAllBytes(src, junk);
        string inbox = Path.Combine(dir, "in");
        var receiver = new ReplayReceiver(inbox);
        string saved = null;
        receiver.Saved += p => saved = p;
        receiver.OnOffer(ReplaySender.MakeOffer(1, src, 1f), accept: true);
        foreach (ReplayChunk chunk in ReplaySender.Chunks(1, src, 16 * 1024)) receiver.OnChunk(chunk);
        Assert.IsNull(saved);
        Assert.IsEmpty(Directory.GetFiles(inbox));
        Directory.Delete(dir, true);
    }

    [Test]
    public void ManyReceivedReplaysNeverEvictTheLocalRecordings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "w2d-xfer4-" + Guid.NewGuid().ToString("N"));
        string inbox = Path.Combine(dir, "in");
        Directory.CreateDirectory(inbox);
        string own = Path.Combine(inbox, "20260101-000000-42" + ReplayFile.Extension);
        WriteReplay(own, 100, seed: 1);
        File.SetLastWriteTimeUtc(own, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // the oldest file
        string src = Path.Combine(dir, "src" + ReplayFile.Extension);
        WriteReplay(src, 1000, seed: 2);
        for (int i = 0; i < 5; i++)
        {
            var receiver = new ReplayReceiver(inbox, keepCount: 2); // a host sending offer after offer
            receiver.OnOffer(ReplaySender.MakeOffer(i, src, 1f), accept: true);
            foreach (ReplayChunk chunk in ReplaySender.Chunks(i, src, 16 * 1024)) receiver.OnChunk(chunk);
        }
        Assert.IsTrue(File.Exists(own), "the player's own recording stays");
        Assert.AreEqual(2, Directory.GetFiles(inbox, "*" + ReplayLibrary.ReceivedTag + "*" + ReplayFile.Extension).Length);

        // Hosting later prunes only this machine's own recordings: the newer received ones don't count against them.
        ReplayLibrary.Prune(inbox, 1, received: false);
        Assert.IsTrue(File.Exists(own), "one own recording is within a cap of one, however many were received");
        Assert.AreEqual(2, Directory.GetFiles(inbox, "*" + ReplayLibrary.ReceivedTag + "*" + ReplayFile.Extension).Length);
        Directory.Delete(dir, true);
    }

    /// <summary>A valid replay file with one record of <paramref name="payloadBytes"/> random bytes.</summary>
    private static void WriteReplay(string path, int payloadBytes, int seed)
    {
        var payload = new byte[payloadBytes];
        new Random(seed).NextBytes(payload);
        using FileStream f = File.Create(path);
        using var w = new ReplayWriter(f, new ReplayHeader { MapSize = 64, Seed = 1, TickSeconds = 0.05f, GameVersion = "t" });
        w.Write(1, ReplayChannel.Timeline, payload);
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

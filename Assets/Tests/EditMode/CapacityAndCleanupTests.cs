using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using WAR2D.Net.Replay;
using WAR2D.Net.Replication;

/// <summary>Limits a large match reaches: Health messages under the cap, and a crashed recording's leftovers.</summary>
public class CapacityAndCleanupTests
{
    [Test]
    public void HealthMessagesStayUnderTheChunkWhenManyUnitsChange()
    {
        using var units = new NativeArray<HealthUnit>(9000, Allocator.Temp);
        var filled = units;
        for (int i = 0; i < units.Length; i++) filled[i] = new HealthUnit { Index = (i + 1) * 20000, Health = 1 }; // the longest gaps
        int covered = 0;
        for (int start = 0; start < units.Length;)
        {
            int end = Messages.HealthChunkEnd(units, start);
            Assert.That(end, Is.GreaterThan(start));
            using var bytes = new NativeList<byte>(Allocator.Temp);
            var sink = new NativeSink(bytes);
            Messages.EncodeHealth(ref sink, units.GetSubArray(start, end - start));
            Assert.That(sink.Position, Is.LessThanOrEqualTo(WireLimits.ReliableChunk));
            covered += end - start;
            start = end;
        }
        Assert.AreEqual(units.Length, covered);
    }

    [Test]
    public void PruneRemovesOnlyAStaleRecordingPart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "w2d-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string stale = Path.Combine(folder, "old" + ReplayFile.Extension + ".part");
            string writing = Path.Combine(folder, "new" + ReplayFile.Extension + ".part");
            File.WriteAllBytes(stale, new byte[1]);
            File.WriteAllBytes(writing, new byte[1]);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
            ReplayLibrary.Prune(folder, 10, received: false);
            Assert.IsFalse(File.Exists(stale));
            Assert.IsTrue(File.Exists(writing), "a recording still being written stays");
        }
        finally { Directory.Delete(folder, true); }
    }
}

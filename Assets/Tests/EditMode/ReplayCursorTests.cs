using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using WAR2D.Client.Replay;
using WAR2D.Net.Replay;

public class ReplayCursorTests
{
    [Test]
    public void AdvancesByTickTimeAndRestarts()
    {
        string path = Path.Combine(Path.GetTempPath(), "w2d-cursor-" + Guid.NewGuid().ToString("N") + ReplayFile.Extension);
        using (var f = File.Create(path))
        using (var w = new ReplayWriter(f, new ReplayHeader { GameVersion = "t", MapSize = 128, TickSeconds = 0.05f }))
        {
            w.Write(0, ReplayChannel.UnitsReliable, new byte[] { 0 });
            w.Write(20, ReplayChannel.UnitsReliable, new byte[] { 20 });
            w.Write(40, ReplayChannel.End, new byte[] { 40 });
        }
        var seen = new List<int>();
        using var cursor = new ReplayCursor(path);
        Assert.AreEqual(2.0f, cursor.DurationSeconds, 1e-4);
        cursor.AdvanceTo(1.0f, (t, c, p) => seen.Add(t));
        CollectionAssert.AreEqual(new[] { 0, 20 }, seen);
        cursor.AdvanceTo(0.5f, (t, c, p) => seen.Add(t));
        Assert.AreEqual(2, seen.Count);
        cursor.Restart(); seen.Clear();
        cursor.AdvanceTo(2.0f, (t, c, p) => seen.Add(t));
        CollectionAssert.AreEqual(new[] { 0, 20, 40 }, seen);
        File.Delete(path);
    }
}

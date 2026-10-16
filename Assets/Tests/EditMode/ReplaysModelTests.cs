using System;
using System.Collections.Generic;
using NUnit.Framework;
using WAR2D.Net.Replay;
using WAR2D.UI;

public class ReplaysModelTests
{
    private static ReplaySummary Summary(int day, float seconds, long bytes, params (string name, int colour)[] players)
    {
        var h = new ReplayHeader { MapSize = 256, TickSeconds = 0.05f, GameVersion = "t" };
        foreach (var p in players) h.Players.Add(new ReplayPlayerInfo { Nickname = p.name, Colour = p.colour });
        return new ReplaySummary { Path = "r" + day, Utc = new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc), Header = h, Bytes = bytes, DurationSeconds = seconds };
    }

    [Test]
    public void RowsAreNewestFirstAndFormatted()
    {
        var list = new List<ReplaySummary> { Summary(14, 535f, 10_276_045, ("Oscar", 0), ("Claude", 3)), Summary(16, 1456f, 40_055_603, ("Rook", 5)), Summary(15, -1f, 0) };
        var model = new ReplaysModel(() => list);
        int changed = 0;
        model.Changed += () => changed++;
        model.Refresh();
        Assert.AreEqual(1, changed);
        Assert.AreEqual(new[] { "r16", "r15", "r14" }, new[] { model.Rows[0].Path, model.Rows[1].Path, model.Rows[2].Path });
        Assert.AreEqual("24:16", model.Rows[0].DurationText);
        Assert.AreEqual("38.2 MB", model.Rows[0].SizeText);
        Assert.AreEqual("–:––", model.Rows[1].DurationText, "unknown length");
        Assert.AreEqual(3, model.Rows[2].Players[1].Colour);
        StringAssert.Contains("Map 256", model.Rows[2].SummaryText);
        StringAssert.Contains("Oscar, Claude", model.Rows[2].SummaryText);
        Assert.IsFalse(model.Empty);
    }

    [Test]
    public void NoReplaysIsEmpty()
    {
        var model = new ReplaysModel(() => Array.Empty<ReplaySummary>());
        model.Refresh();
        Assert.IsTrue(model.Empty);
    }

    [TestCase(0f, "0:00")]
    [TestCase(59.6f, "1:00")]
    [TestCase(3725f, "1:02:05")]
    public void DurationFormat(float seconds, string text) => Assert.AreEqual(text, ReplaysModel.Duration(seconds));

    [Test]
    public void EndScreenReplayLine()
    {
        Assert.IsNull(EndScreenController.ReplayState(false, null, null, saveReplays: true).text, "no offer yet");
        Assert.IsNull(EndScreenController.ReplayState(false, null, new ReplayReceiver("x"), saveReplays: false).text, "saving is off");
        Assert.IsNull(EndScreenController.ReplayState(true, null, null, saveReplays: true).text, "the host isn't recording");
    }
}

using System.IO;
using NUnit.Framework;
using WAR2D.Dev.Logging;

public class WarLogTests
{
    [SetUp] public void SetUp() { WarLog.ResetForTests(capacity: 4); WarLog.ForwardToUnity = false; }
    [TearDown] public void TearDown() { WarLog.ResetForTests(); WarLog.ForwardToUnity = true; }

    [Test]
    public void EntriesCarryCategoryLevelAndAscendingSequence()
    {
        WarLog.Info(LogCategory.Net, "a");
        WarLog.Warn(LogCategory.Sim, "b");
        var tail = WarLog.Tail(10);
        Assert.AreEqual(2, tail.Count);
        Assert.AreEqual(LogCategory.Net, tail[0].Category);
        Assert.AreEqual(LogLevel.Warn, tail[1].Level);
        Assert.Less(tail[0].Seq, tail[1].Seq);
    }

    [Test]
    public void RingKeepsTheNewest()
    {
        for (int i = 0; i < 6; i++) WarLog.Info(LogCategory.Dev, "m" + i);
        var tail = WarLog.Tail(10);
        Assert.AreEqual(4, tail.Count);
        Assert.AreEqual("m2", tail[0].Message);
        Assert.AreEqual("m5", tail[3].Message);
    }

    [Test]
    public void DebugIsDroppedUntilItsCategoryAllowsIt()
    {
        WarLog.Debug(LogCategory.Replication, "hidden");
        Assert.AreEqual(0, WarLog.Tail(10).Count);
        WarLog.SetLevel(LogCategory.Replication, LogLevel.Debug);
        WarLog.Debug(LogCategory.Replication, "shown");
        Assert.AreEqual("shown", WarLog.Tail(10)[0].Message);
    }

    [Test]
    public void TailFiltersByCategoryLevelAndSequence()
    {
        WarLog.Info(LogCategory.Net, "n1");
        WarLog.Error(LogCategory.Sim, "s1");
        long after = WarLog.LastSeq;
        WarLog.Info(LogCategory.Net, "n2");
        Assert.AreEqual("n1", WarLog.Tail(10, LogCategory.Net)[0].Message);
        Assert.AreEqual("s1", WarLog.Tail(10, min: LogLevel.Error)[0].Message);
        Assert.AreEqual("n2", WarLog.Tail(10, afterSeq: after)[0].Message);
    }

    [Test]
    public void TickSourceStampsEntries()
    {
        WarLog.TickSource = () => 42;
        WarLog.Info(LogCategory.Sim, "t");
        WarLog.TickSource = null;
        Assert.AreEqual(42, WarLog.Tail(1)[0].Tick);
    }

    [Test]
    public void LogFileWritesLinesAndKeepsTheLastTen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "war2d-logtest-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(dir, $"war2d-2026010{i % 10}-0000{i:00}-1.log"), "old");
        using (var file = LogFile.Open(dir, keep: 10))
            file.Write(new LogEntry { Seq = 1, Category = LogCategory.Net, Level = LogLevel.Info, Message = "hello", Tick = -1 });
        Assert.LessOrEqual(Directory.GetFiles(dir, "war2d-*.log").Length, 10);
        StringAssert.Contains("[Net] hello", File.ReadAllText(LogFile.LastPath));
        Directory.Delete(dir, true);
    }

    [Test]
    public void GameCodeLogsThroughWarLog()
    {
        string root = System.IO.Path.Combine(UnityEngine.Application.dataPath, "Scripts");
        var offenders = new System.Collections.Generic.List<string>();
        foreach (string path in System.IO.Directory.GetFiles(root, "*.cs", System.IO.SearchOption.AllDirectories))
        {
            if (path.Replace('\\', '/').Contains("/Scripts/Editor/") || path.EndsWith("WarLog.cs")) continue;
            string text = System.IO.File.ReadAllText(path);
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bDebug\.Log(Warning|Error)?\s*\(")) offenders.Add(path);
        }
        Assert.That(offenders, Is.Empty, "use WarLog instead of Debug.Log");
    }
}

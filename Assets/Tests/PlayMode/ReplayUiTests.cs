using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.Client.Replay;
using WAR2D.Net.Replay;
using WAR2D.UI;

/// <summary>The Replays screen and the replay viewer HUD, with one replay written by the test.</summary>
public class ReplayUiTests
{
    private string path;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        PlayModeMatch.Configure();
        Directory.CreateDirectory(ReplayLibrary.Folder);
        path = Path.Combine(ReplayLibrary.Folder, "ui-test-" + System.Guid.NewGuid().ToString("N") + ReplayFile.Extension);
        var header = new ReplayHeader { GameVersion = Application.version, MapSize = 128, Seed = 3, TickSeconds = 0.05f };
        header.Players.Add(new ReplayPlayerInfo { OwnerId = 5, Nickname = "Ann", Colour = 2 });
        using (FileStream f = File.Create(path))
        using (var w = new ReplayWriter(f, header))
        {
            w.Write(0, ReplayChannel.Timeline, System.Text.Encoding.UTF8.GetBytes("[{\"Item1\":5,\"Item2\":120.0,\"Item3\":7,\"Item4\":1}]"));
            w.Write(200, ReplayChannel.End, System.Text.Encoding.UTF8.GetBytes("{}"));
        }
        ReplayLibrary.WriteInfo(path, 10f);
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        ReplayPlayer.Instance?.Close();
        ReplayLibrary.Delete(path);
        yield return PlayModeMatch.TearDown();
    }

    private static VisualElement Root() => Object.FindAnyObjectByType<MenuController>().GetComponent<UIDocument>().rootVisualElement;

    [UnityTest, Timeout(60000)]
    public IEnumerator WatchShowsTheViewerAndLeaveReturnsToTheList()
    {
        VisualElement root = Root();
        HudTests.Click(root.Q<Button>("replays-button"));
        yield return null;
        Assert.IsTrue(root.Q("replays").ClassListContains("menu-screen--visible"), "the Replays screen shows");
        VisualElement row = null;
        foreach (VisualElement r in root.Query("replay-row").ToList()) if (r.Q<Label>("replay-length").text == "0:10 · 0.0 MB") row = r;
        Assert.IsNotNull(row, "the test's replay is listed");
        Assert.IsFalse(root.Q("replays-empty").ClassListContains("play-panel__notice--visible"));

        HudTests.Click(row.Q<Button>("replay-watch"));
        yield return null;
        Assert.IsNotNull(ReplayPlayer.Instance);
        Assert.IsTrue(root.Q("replay-viewer").ClassListContains("menu-screen--visible"), "the viewer HUD shows");
        float start = ReplayPlayer.Instance.Seconds;
        yield return new WaitForSeconds(0.5f);
        Assert.Greater(ReplayPlayer.Instance.Seconds, start, "time advances");
        Assert.IsNotNull(root.Q("replay-player-5"), "the timeline's player is listed");
        StringAssert.Contains("/ 0:10", root.Q<Label>("replay-time").text);

        HudTests.Click(root.Q<Button>("speed-4"));
        Assert.AreEqual(4f, ReplayPlayer.Instance.Speed);
        HudTests.Click(root.Q<Button>("replay-play"));
        Assert.IsFalse(ReplayPlayer.Instance.Playing, "paused");

        HudTests.Click(root.Q<Button>("replay-leave"));
        yield return null;
        yield return null;
        Assert.IsNull(ReplayPlayer.Instance);
        Assert.IsTrue(root.Q("replays").ClassListContains("menu-screen--visible"), "back on the list");
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator DeleteAsksFirst()
    {
        VisualElement root = Root();
        HudTests.Click(root.Q<Button>("replays-button"));
        yield return null;
        VisualElement row = null;
        foreach (VisualElement r in root.Query("replay-row").ToList()) if (r.Q<Label>("replay-length").text == "0:10 · 0.0 MB") row = r;
        Assert.IsNotNull(row);
        HudTests.Click(row.Q<Button>("replay-delete"));
        Assert.IsTrue(File.Exists(path), "the first click only asks");
        HudTests.Click(row.Q<Button>("replay-delete"));
        Assert.IsFalse(File.Exists(path), "the second click deletes");
    }
}

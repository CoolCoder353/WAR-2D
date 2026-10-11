using System.Collections;
using Config;
using NUnit.Framework;
using UnityEngine.TestTools;
using WAR2D.Audio;

/// <summary>The music follows the game: the menu track, then the calm match track once a match is hosted.</summary>
public class AudioPlayModeTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown() => PlayModeMatch.TearDown();

    [UnityTest, Timeout(90000)]
    public IEnumerator HostingAMatchSwitchesFromMenuToCalmMusic()
    {
        AudioService.Ui("ui_click"); // makes the service in batch mode, where it doesn't boot itself
        yield return null;
        Assert.AreEqual(MusicCue.Menu, AudioService.CurrentCue);
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.WaitUntil(() => AudioService.CurrentCue == MusicCue.MatchCalm, 5f);
        Assert.AreEqual(MusicCue.MatchCalm, AudioService.CurrentCue);
    }
}

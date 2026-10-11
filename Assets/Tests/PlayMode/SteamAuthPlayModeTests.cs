using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using WAR2D.Net.Steam;

/// <summary>The Steam authenticator must not get in the way of the Direct (editor) flow; the Steam branch is covered by SteamAuthTests and the manual relay test.</summary>
public class SteamAuthPlayModeTests
{
    [UnitySetUp]
    public IEnumerator SetUp()
    {
        PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown() => PlayModeMatch.TearDown();

    [UnityTest, Timeout(60000)]
    public IEnumerator HostingThroughTheAuthenticatorReachesTheLobby()
    {
        Assert.That(GameManager.Instance.authenticator, Is.TypeOf<SteamAuthenticator>(), "the menu scene wires the authenticator");
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance != null && Mirror.NetworkClient.localPlayer != null, 15f);
        Assert.That(GameCore.Instance.CurrentState, Is.EqualTo(GameState.Lobby));
    }
}

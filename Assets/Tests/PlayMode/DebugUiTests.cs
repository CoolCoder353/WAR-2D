using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>Debug UI never reaches release builds.</summary>
public class DebugUiTests
{
    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevUi.ForceReleaseForTests = false;
        yield return null;
    }

    [UnityTest]
    public IEnumerator NoDebugUiInReleaseBuilds()
    {
        yield return PlayModeMatch.LoadMenu();
        Assert.That(DevUi.Enabled, Is.True, "the editor counts as a development build");
        DevUi.ForceReleaseForTests = true;
        Assert.That(DevUi.Enabled, Is.False);
        DevUi.Apply();
        yield return null;
        Assert.That(DevUi.ConsoleExists, Is.False, "the console (and so its toggle key) is gone in release");
    }
}

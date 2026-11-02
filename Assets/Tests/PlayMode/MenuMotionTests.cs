using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.UI;

/// <summary>Menu motion (v0.16, Figma M1): a screen arriving slides in and settles; Reduce motion swaps at once.</summary>
public class MenuMotionTests
{
    [UnitySetUp]
    public IEnumerator SetUp()
    {
        PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        SettingsStore.Current.ReduceMotion = false;
        yield return PlayModeMatch.TearDown();
    }

    private static MenuController Menu() => Object.FindAnyObjectByType<MenuController>();
    private static VisualElement Root() => Menu().GetComponent<UIDocument>().rootVisualElement;

    [UnityTest]
    public IEnumerator AnArrivingScreenStartsOffsetThenSettles()
    {
        VisualElement root = Root();
        VisualElement play = root.Q("host-join");
        HudTests.Click(root.Q<Button>("play-button"));
        Assert.AreEqual("Play", Menu().LastScreenChange);
        Assert.IsTrue(play.ClassListContains("menu-screen--enter"), "starts offset and transparent");
        Assert.IsTrue(play.ClassListContains("menu-screen--visible"), "and shown at once: motion never delays a click");
        float until = Time.realtimeSinceStartup + 1f;
        while (play.ClassListContains("menu-screen--enter") && Time.realtimeSinceStartup < until) yield return null;
        Assert.IsFalse(play.ClassListContains("menu-screen--enter"), "the class comes off and USS animates it in");
    }

    [UnityTest]
    public IEnumerator ReduceMotionAddsTheClassThatZeroesTransitions()
    {
        SettingsStore.Current.ReduceMotion = true;
        yield return null;
        yield return null;
        Assert.IsTrue(Root().ClassListContains(MenuMotion.ReduceMotionClass));
        SettingsStore.Current.ReduceMotion = false;
        yield return null;
        yield return null;
        Assert.IsFalse(Root().ClassListContains(MenuMotion.ReduceMotionClass));
    }
}

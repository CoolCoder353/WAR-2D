using System.Collections;
using System.Linq;
using Config;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.UI;

/// <summary>v0.18: the Field manual opens from the main menu and in a match, and every page draws.</summary>
public class ManualPlayModeTests
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

    [UnityTest, Timeout(120000)]
    public IEnumerator EveryPageDrawsFromTheMainMenu()
    {
        MenuController menu = Object.FindAnyObjectByType<MenuController>();
        yield return null;
        VisualElement root = menu.GetComponent<UIDocument>().rootVisualElement;
        var button = root.Q<Button>("manual-button");
        Assert.That(button, Is.Not.Null);
        using (var click = NavigationSubmitEvent.GetPooled())
        {
            click.target = button;
            button.SendEvent(click);
        }
        yield return null;
        Assert.That(menu.Manual.IsOpen, Is.True, "the Manual button opens it");
        var model = new ManualModel(config, a => a);
        foreach (string id in model.Pages.Keys.ToList())
        {
            menu.Manual.Go(id);
            yield return null;
            Assert.That(menu.Manual.CurrentPage, Is.EqualTo(id));
        }
        menu.Manual.Close();
        Assert.That(ManualController.AnyOpen, Is.False);
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator InAMatchItOpensOnASelectedTypeAndPausesASoloHost()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        HudController hud = Object.FindAnyObjectByType<HudController>();
        Assert.That(hud, Is.Not.Null);
        hud.Manual.Open(ManualModel.PageOf(UnitType.Tank));
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.IsMenuPaused, 5f);
        Assert.That(hud.Manual.CurrentPage, Is.EqualTo("unit-Tank"));
        hud.Manual.Close();
        yield return PlayModeMatch.WaitUntil(() => !GameCore.Instance.IsMenuPaused, 5f);
    }
}

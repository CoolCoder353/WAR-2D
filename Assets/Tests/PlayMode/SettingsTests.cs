using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.UI;

/// <summary>The settings screen, palettes and saved bindings.</summary>
public class SettingsTests
{
    private Config.GameConfigData config;
    private GameSettings saved;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        saved = SettingsStore.Current;
        SettingsStore.PathForTests = Path.Combine(Application.temporaryCachePath, "settings-ui-test.json"); // never the player's real file
        SettingsStore.Current = new GameSettings { MenuBattle = false };
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Rebinding.ResetAll();
        Palettes.Current = Palettes.Standard;
        SettingsStore.Current = saved;
        if (File.Exists(SettingsStore.PathForTests)) File.Delete(SettingsStore.PathForTests);
        SettingsStore.PathForTests = null;
        yield return PlayModeMatch.TearDown();
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator PaletteSwitchRecoloursUnitsAndTheUi()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        int me = PlayModeMatch.LocalOwner;
        Color32 before = PlayerPalette.OfOwner(me);
        VisualElement root = HudTests.HudRoot();

        HudController hud = Object.FindAnyObjectByType<HudController>();
        hud.MatchMenu.Open();
        HudTests.Click(root.Q<Button>("match-settings-button"));
        yield return null;
        Assert.That(hud.Settings.IsOpen, Is.True, "Settings opens from the in-match menu");
        HudTests.Click(root.Q<Button>("palette-colourblind"));
        yield return null;

        Assert.That(Palettes.Current, Is.EqualTo(Palettes.Colourblind));
        Assert.That(PlayerPalette.OfOwner(me), Is.Not.EqualTo(before), "units and buildings draw in the new colours");
        Assert.That(PlayerPalette.OfOwner(me), Is.EqualTo(Palettes.Player(Palettes.Colourblind, PlayerPalette.ColourIndexOf(me))));
        Assert.That(root.ClassListContains("palette-colourblind"), Is.True, "the UI's tokens switch too");
        HudTests.Click(root.Q<Button>("settings-back"));
        yield return null;
        Assert.That(hud.Settings.IsOpen, Is.False);
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator RebindAttackMovePersistsAcrossRestart()
    {
        string path = Path.Combine(Application.temporaryCachePath, "settings-rebind-test.json");
        try
        {
            Rebinding.Override("AttackMove", "<Keyboard>/q");
            GameSettings s = SettingsStore.Current;
            s.Bindings = Rebinding.SaveGame();
            SettingsStore.Save(s, path);

            Rebinding.ResetAll(); // a fresh start
            Assert.That(Rebinding.PathOf("AttackMove"), Is.EqualTo("<Keyboard>/a"));
            SettingsStore.Apply(SettingsStore.Load(path));
            yield return null;
            Assert.That(Rebinding.PathOf("AttackMove"), Is.EqualTo("<Keyboard>/q"));

            MenuController menu = Object.FindAnyObjectByType<MenuController>();
            VisualElement root = menu.GetComponent<UIDocument>().rootVisualElement;
            HudTests.Click(root.Q<Button>("settings-button"));
            yield return null;
            HudTests.Click(root.Q<Button>("tab-controls"));
            yield return null;
            Assert.That(root.Q<Button>("binding-AttackMove").text, Is.EqualTo("Q"), "the Controls page shows the saved binding");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

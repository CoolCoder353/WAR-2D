using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client;
using WAR2D.UI;

/// <summary>v0.15 quality-of-life logic: the Gameplay settings, the net-income readout, next alert, and the army rules.</summary>
public class QolTests
{
    private GameSettings saved;

    [SetUp]
    public void SetUp() => saved = SettingsStore.Current;

    [TearDown]
    public void TearDown() => SettingsStore.Current = saved;

    // ---- settings

    [Test]
    public void GameplaySettingsDefaultAndClamp()
    {
        var s = new GameSettings { UiScale = 9f, PanSpeed = 0f, ZoomSpeed = float.NaN, ShiftMultiplier = 100f, HealthBars = 7, Tracers = -3, MinimapScale = 5, MinimapFilters = 255 };
        s.Clamp();
        Assert.AreEqual(1.5f, s.UiScale);
        Assert.AreEqual(0.5f, s.PanSpeed);
        Assert.AreEqual(1f, s.ZoomSpeed, "NaN falls back to the default");
        Assert.AreEqual(4f, s.ShiftMultiplier);
        Assert.AreEqual(2, s.HealthBars);
        Assert.AreEqual(0, s.Tracers);
        Assert.AreEqual(2, s.MinimapScale);
        Assert.AreEqual((int)MinimapLayers.All, s.MinimapFilters);
        Assert.AreEqual(GameSettings.AlertSlots, s.AlertShow.Length);
        Assert.IsTrue(s.ShowsAlert(AlertKind.EnemySpotted));
        Assert.IsTrue(s.SoundsAlert(AlertKind.EnemySpotted));
    }

    [Test]
    public void AnOldSettingsFileKeepsTheNewDefaults()
    {
        GameSettings s = UnityEngine.JsonUtility.FromJson<GameSettings>("{\"Width\":1280,\"Height\":720,\"Master\":0.5}");
        s.Clamp();
        Assert.AreEqual(1280, s.Width);
        Assert.AreEqual(1f, s.UiScale);
        Assert.AreEqual((int)HealthBarMode.DamagedOnly, s.HealthBars);
        Assert.IsTrue(s.ConfirmDemolish && s.ConfirmSurrender);
        Assert.AreEqual((int)MinimapSize.Medium, s.MinimapScale);
    }

    [Test]
    public void TheAlertsThatLoseGamesCannotBeSilenced()
    {
        var s = new GameSettings { AlertShow = new bool[GameSettings.AlertSlots], AlertSound = new bool[GameSettings.AlertSlots] };
        s.Clamp();
        foreach (AlertKind kind in GameSettings.MustHear)
            Assert.IsTrue(s.ShowsAlert(kind) || s.SoundsAlert(kind), $"{kind} keeps Show or Sound");
        Assert.IsFalse(s.ShowsAlert(AlertKind.GiftReceived) || s.SoundsAlert(AlertKind.GiftReceived), "the others may be off");
    }

    [Test]
    public void SettingsRoundTripThroughJson()
    {
        var s = new GameSettings { UiScale = 1.25f, MinimapFilters = (int)(MinimapLayers.Gems | MinimapLayers.Army) };
        s.Clamp();
        s.AlertSound[(int)AlertKind.GiftReceived] = false;
        GameSettings back = UnityEngine.JsonUtility.FromJson<GameSettings>(UnityEngine.JsonUtility.ToJson(s));
        back.Clamp();
        Assert.AreEqual(1.25f, back.UiScale);
        Assert.IsTrue(back.ShowsLayer(MinimapLayers.Gems));
        Assert.IsFalse(back.ShowsLayer(MinimapLayers.Ghosts));
        Assert.IsFalse(back.SoundsAlert(AlertKind.GiftReceived));
    }

    // ---- economy readout

    [Test]
    public void NetIncomeIsAveragedOverFiveSeconds()
    {
        var e = new EconomyReadout();
        for (int t = 0; t < 5; t++) e.Feed(1000f, 40f, 10f, t); // +30
        Assert.AreEqual(30f, e.NetPerSecond, 1e-3f);
        Assert.AreEqual(EconomyState.Healthy, e.State);
        for (int t = 5; t < 12; t++) e.Feed(1000f, 40f, 70f, t); // -30 once the old samples have left the window
        Assert.AreEqual(-30f, e.NetPerSecond, 1e-3f);
    }

    [Test]
    public void ANegativeNetIsLowOnlyWhenTheBalanceLastsUnderAMinute()
    {
        var e = new EconomyReadout();
        e.Feed(5000f, 10f, 22f, 0f); // -12/s, 416 s left
        Assert.AreEqual(EconomyState.Negative, e.State);
        e.Feed(580f, 10f, 22f, 100f); // 48 s left (the old sample is out of the window)
        Assert.AreEqual(EconomyState.RunningLow, e.State);
        Assert.AreEqual(580f / 12f, e.SecondsUntilEmpty, 1e-2f);
        Assert.AreEqual("−12/s · runs out in 0:49", e.Text());
    }

    [Test]
    public void AnEmptyBalanceWithNegativeNetIsDecaying()
    {
        var e = new EconomyReadout();
        e.Feed(0f, 10f, 22f, 0f);
        Assert.AreEqual(EconomyState.Decaying, e.State);
        Assert.AreEqual("Decaying", e.Text());
    }

    [Test]
    public void TheLowFundsWarningFiresOnceAndRearmsWhenNetRecovers()
    {
        var e = new EconomyReadout();
        int warnings = 0;
        e.LowFunds += () => warnings++;
        e.Feed(300f, 0f, 20f, 0f);
        e.Feed(280f, 0f, 20f, 10f);
        Assert.AreEqual(1, warnings, "once per drop");
        e.Feed(260f, 30f, 0f, 20f);
        e.Feed(260f, 30f, 0f, 30f);
        Assert.AreEqual(EconomyState.Healthy, e.State);
        e.Feed(260f, 0f, 20f, 40f);
        e.Feed(260f, 0f, 20f, 50f);
        Assert.AreEqual(2, warnings, "re-armed after a healthy spell");
    }

    // ---- next alert

    private static Alert At(AlertKind kind, int x) => new Alert { Kind = kind, Tile = new int2(x, 0) };

    [Test]
    public void NextAlertWalksNewestFirstAndSkipsVisitedAndPlacelessOnes()
    {
        SettingsStore.Current = new GameSettings();
        var feed = new AlertFeedModel(10f);
        feed.Add(At(AlertKind.UnderAttack, 1), "a", 0f);
        feed.Add(At(AlertKind.GiftReceived, 2), "gift", 1f); // no place
        feed.Add(At(AlertKind.BombTriggered, 3), "b", 2f);
        Assert.AreEqual(2, feed.UnvisitedCount);
        int2? went = null;
        feed.FocusRequested += tile => went = tile;
        Assert.IsTrue(feed.FocusNextUnvisited());
        Assert.AreEqual(3, went.Value.x, "the newest first");
        Assert.IsTrue(feed.FocusNextUnvisited());
        Assert.AreEqual(1, went.Value.x, "then the one before it");
        Assert.AreEqual(0, feed.UnvisitedCount);
        Assert.IsFalse(feed.FocusNextUnvisited());
    }

    [Test]
    public void ClickingAnAlertMarksItVisited()
    {
        SettingsStore.Current = new GameSettings();
        var feed = new AlertFeedModel(10f);
        feed.Add(At(AlertKind.EnemySpotted, 5), "spotted", 0f);
        feed.Focus(feed.Entries[0].Id);
        Assert.IsTrue(feed.Entries[0].Visited);
    }

    [Test]
    public void AnAlertKindSwitchedOffStaysOutOfTheFeedButStillPings()
    {
        var settings = new GameSettings();
        settings.Clamp();
        settings.AlertShow[(int)AlertKind.EnemySpotted] = false;
        SettingsStore.Current = settings;
        var feed = new AlertFeedModel(10f);
        int pings = 0;
        feed.PingRequested += (_, _) => pings++;
        feed.Add(At(AlertKind.EnemySpotted, 5), "spotted", 0f);
        Assert.AreEqual(0, feed.Entries.Count);
        Assert.AreEqual(1, pings);
    }

    // ---- army

    [Test]
    public void TheArmyIsEveryUnitThatAttacksExceptDiggersAndBombers()
    {
        var gun = new Config.UnitConfig { Damage = 10 };
        var builder = new Config.UnitConfig { Damage = 0 };
        Assert.IsTrue(Selection.IsArmy(UnitType.Tank, gun));
        Assert.IsFalse(Selection.IsArmy(UnitType.Builder, builder));
        Assert.IsFalse(Selection.IsArmy(UnitType.Digger, gun), "a Digger does 4 damage but belongs to the siege group");
        Assert.IsFalse(Selection.IsArmy(UnitType.SuicideBomber, gun));
        Assert.IsTrue(Selection.IsSiege(UnitType.Digger) && Selection.IsSiege(UnitType.SuicideBomber));
        Assert.IsFalse(Selection.IsArmy(UnitType.Tank, null));
    }
}

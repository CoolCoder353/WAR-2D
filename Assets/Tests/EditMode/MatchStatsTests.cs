using NUnit.Framework;

public class MatchStatsTests
{
    [Test]
    public void BuiltLostAndKilled()
    {
        var stats = new MatchStats();
        stats.UnitBuilt(1);
        stats.UnitBuilt(1);
        stats.UnitBuilt(2);
        stats.UnitDied(2, 1);
        stats.UnitDied(1, 0); // decay: nobody's kill
        stats.UnitDied(1, 1); // never a kill of your own
        stats.BuildingBuilt(2);
        stats.BuildingLost(2);
        PlayerStats a = stats.Of(1), b = stats.Of(2);
        Assert.AreEqual(2, a.UnitsBuilt);
        Assert.AreEqual(2, a.UnitsLost);
        Assert.AreEqual(1, a.UnitsKilled);
        Assert.AreEqual(1, b.UnitsLost);
        Assert.AreEqual(0, b.UnitsKilled);
        Assert.AreEqual(1, b.BuildingsBuilt);
        Assert.AreEqual(1, b.BuildingsLost);
    }

    [Test]
    public void PeakArmyKeepsTheLargest()
    {
        var stats = new MatchStats();
        stats.Army(1, 40);
        stats.Army(1, 90);
        stats.Army(1, 10);
        Assert.AreEqual(90, stats.Of(1).PeakArmy);
    }

    [Test]
    public void GiftsMinedAndSnapshotOrder()
    {
        var stats = new MatchStats();
        stats.Gift(1, 2, 500f);
        stats.Mined(2, 30f);
        stats.Mined(2, -5f); // ignored
        PlayerStats[] snap = stats.Snapshot(new[] { 2, 1, 3 });
        Assert.AreEqual(500f, snap[0].GiftedIn);
        Assert.AreEqual(30f, snap[0].Mined);
        Assert.AreEqual(500f, snap[1].GiftedOut);
        Assert.AreEqual(default(PlayerStats), snap[2], "a player with nothing counted");
        stats.Clear();
        Assert.AreEqual(default(PlayerStats), stats.Of(1));
    }
}

using Mirror;
using NUnit.Framework;

/// <summary>v0.18: the end screen's samples, merging, big fights and message size.</summary>
public class SummaryTests
{
    private static Config.SummaryConfig Config_() => new Config.SummaryConfig { SampleSeconds = 10f, MaxPoints = 360, FightCellTiles = 32, FightWindowSeconds = 20f, FightMinWorth = 600f, MaxFights = 30 };
    private static readonly int[] One = { 1 };

    [Test]
    public void ALongMatchKeepsItsWholeShape()
    {
        var s = new MatchSummary();
        s.Begin(Config_());
        // 90 minutes; the army peaks once, at minute 40.
        for (int t = 10; t <= 5400; t += 10)
        {
            if (!s.SampleDue(t)) continue;
            float army = t == 2400 ? 9000f : 100f;
            s.Sample(t, One, _ => (army, t, 0));
        }
        SummaryData d = s.Build(256, null, null, null);
        Assert.LessOrEqual(d.Times.Length, 360);
        Assert.That(d.Times.Length, Is.GreaterThan(180));
        Assert.AreEqual(5400f, d.Times[d.Times.Length - 1], 20f, "still reaches the end");
        Assert.AreEqual(d.Times.Length, d.Players[0].Army.Length);
        Assert.AreEqual(d.Times.Length, d.Players[0].Killed.Length);
    }

    [Test]
    public void IncomeIsPerMinuteOverTheMatchClock()
    {
        var s = new MatchSummary();
        s.Begin(Config_());
        s.Income(1, 50f); // 50 in 10 match seconds, however long a pause was: only match time passes here
        s.Sample(10, One, _ => (0f, 0, 0));
        Assert.AreEqual(300f, s.Build(256, null, null, null).Players[0].Income[0], 0.01f);
    }

    [Test]
    public void AFightOverTwoCellsIsOneFight()
    {
        var f = new FightDetector(32, 20f, 600f, 30);
        for (int i = 0; i < 8; i++) f.Death(i, 1, 2, 100f, 30f, 10f); // cell 0
        for (int i = 8; i < 16; i++) f.Death(i, 2, 1, 100f, 34f, 10f); // cell 1
        SummaryFight[] fights = f.Finish();
        Assert.AreEqual(1, fights.Length);
        Assert.AreEqual(0f, fights[0].Start);
        Assert.AreEqual(15f, fights[0].End);
    }

    [Test]
    public void AQuietWindowSplitsTwoFightsInACell()
    {
        var f = new FightDetector(32, 20f, 600f, 30);
        for (int i = 0; i < 7; i++) f.Death(i, 1, 2, 100f, 10f, 10f);
        for (int i = 100; i < 107; i++) f.Death(i, 1, 2, 100f, 10f, 10f);
        Assert.AreEqual(2, f.Finish().Length);
    }

    [Test]
    public void ASkirmishUnderTheThresholdIsNoFight()
    {
        var f = new FightDetector(32, 20f, 600f, 30);
        for (int i = 0; i < 5; i++) f.Death(i, 1, 2, 100f, 10f, 10f);
        for (int i = 50; i < 55; i++) f.Death(i, 1, 2, 100f, 10f, 10f); // outside the window of the first five
        Assert.AreEqual(0, f.Finish().Length);
    }

    [Test]
    public void TheCapKeepsTheLargest()
    {
        var f = new FightDetector(32, 20f, 100f, 2);
        f.Death(0f, 1, 2, 200f, 10f, 10f);
        f.Death(100f, 1, 2, 900f, 10f, 10f);
        f.Death(200f, 1, 2, 500f, 10f, 10f);
        SummaryFight[] fights = f.Finish();
        Assert.AreEqual(2, fights.Length);
        Assert.AreEqual(100f, fights[0].Start, "in time order");
        Assert.AreEqual(200f, fights[1].Start);
    }

    [Test]
    public void AFightRecordsWhatEachPlayerLostAndKilled()
    {
        var f = new FightDetector(32, 20f, 100f, 2);
        f.Death(0f, 1, 2, 200f, 10f, 10f);
        SummaryFight fight = f.Finish()[0];
        int one = System.Array.IndexOf(fight.Owners, 1), two = System.Array.IndexOf(fight.Owners, 2);
        Assert.AreEqual(200f, fight.WorthLost[one]);
        Assert.AreEqual(1, fight.UnitsLost[one]);
        Assert.AreEqual(200f, fight.WorthKilled[two]);
        Assert.AreEqual(10f, fight.CentreX, 0.01f);
    }

    /// <summary>8 players × 360 samples × 4 values, plus 30 fights and 8 eliminations, in one message.</summary>
    [Test]
    public void TheLargestSummaryFitsOneMessage()
    {
        var s = new MatchSummary();
        s.Begin(new Config.SummaryConfig { SampleSeconds = 10f, MaxPoints = 361, FightCellTiles = 32, FightWindowSeconds = 1f, FightMinWorth = 1f, MaxFights = 30 });
        int[] owners = { 1, 2, 3, 4, 5, 6, 7, 8 };
        for (int t = 10; t <= 3600; t += 10) s.Sample(t, owners, _ => (12345f, 1000, 1000));
        for (int i = 0; i < 40; i++)
            foreach (int o in owners) s.Death(i * 10, o, o % 8 + 1, 100f, 10f + i * 40, 10f);
        foreach (int o in owners) s.Eliminated(100, o, EliminationCause.HqDestroyed, 1);
        SummaryData d = s.Build(1024, owners, new float[8], new float[8]);
        using NetworkWriterPooled writer = NetworkWriterPool.Get();
        writer.Write(d);
        Assert.Less(writer.Position, 64 * 1024);
    }
}

/// <summary>v0.18: the words and numbers on the end screen's tabs.</summary>
public class SummaryModelTests
{
    private static string Name(int owner) => owner == 2 ? "IronFox" : "Vex";

    private static SummaryData Data() => new SummaryData
    {
        Times = new[] { 10f, 20f, 30f },
        Players = new[]
        {
            new SummaryPlayer { Owner = 1, Army = new[] { 100f, 900f, 2400f }, Income = new[] { 1f, 2f, 3f }, Killed = new[] { 1, 2, 3 }, Lost = new[] { 0, 0, 1 } },
            new SummaryPlayer { Owner = 2, Army = new[] { 300f, 500f, 0f }, Income = new[] { 1f, 2f, 3f }, Killed = new[] { 1, 2, 3 }, Lost = new[] { 0, 0, 1 } },
        },
        Eliminations = new[] { new SummaryElimination { Time = 25f, Owner = 2, Cause = EliminationCause.HqDestroyed, By = 1 } },
        Fights = new[] { new SummaryFight { Start = 5f, End = 105f, CentreX = 512f, CentreY = 512f, Owners = new[] { 1, 2 }, UnitsLost = new[] { 3, 9 }, WorthLost = new[] { 1240f, 2010f }, WorthKilled = new[] { 2010f, 1240f } } },
        MapSize = 1024,
        HqOwners = new[] { 2 },
        HqX = new[] { 100f },
        HqY = new[] { 900f },
    };

    [Test]
    public void TheTimelineIsInTimeOrderAndSaysYou()
    {
        var entries = WAR2D.UI.TimelineText.Entries(Data(), Name, 1);
        Assert.AreEqual("Big fight in the middle, 1:40: IronFox lost 2,010, you lost 1,240", entries[0].Text);
        Assert.IsTrue(entries[0].Fight);
        Assert.AreEqual("IronFox is out: HQ destroyed by you", entries[1].Text);
    }

    [TestCase(100f, 880f, "near IronFox's base")]
    [TestCase(100f, 500f, "in the west")]
    [TestCase(900f, 900f, "in the north-east")]
    [TestCase(500f, 50f, "in the south")]
    public void APlaceIsNeverCoordinates(float x, float y, string words) =>
        Assert.AreEqual(words, WAR2D.UI.TimelineText.Place(x, y, Data(), Name, 1));

    [Test]
    public void TheReadOutIsHighestFirstAndLeavesOffThoseOut()
    {
        var rows = WAR2D.UI.SummaryModel.ReadOut(Data(), WAR2D.UI.SummaryMetric.ArmyValue, 20f, null);
        Assert.AreEqual((1, 900f), rows[0]);
        Assert.AreEqual((2, 500f), rows[1]);
        Assert.AreEqual(1, WAR2D.UI.SummaryModel.ReadOut(Data(), WAR2D.UI.SummaryMetric.ArmyValue, 30f, null).Count, "player 2 went out at 25 s");
        Assert.AreEqual(1, WAR2D.UI.SummaryModel.ReadOut(Data(), WAR2D.UI.SummaryMetric.ArmyValue, 20f, new[] { 2 }).Count, "toggled off");
    }

    [Test]
    public void TheAxisTopIsARoundNumber()
    {
        Assert.AreEqual(2500f, WAR2D.UI.SummaryModel.NiceMax(Data(), WAR2D.UI.SummaryMetric.ArmyValue));
        Assert.AreEqual("2.5k", WAR2D.UI.SummaryModel.Short(2500f));
    }
}

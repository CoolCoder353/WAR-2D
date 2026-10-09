using NUnit.Framework;

public class ServerPlayerTests
{
    [Test]
    public void TrySpend_NeverGoesNegative()
    {
        var p = new ServerPlayer(null, 10f);
        Assert.That(p.TrySpend(11f), Is.False);
        Assert.That(p.Resources, Is.EqualTo(10f));
        Assert.That(p.TrySpend(10f), Is.True);
        Assert.That(p.Resources, Is.EqualTo(0f));
    }

    [Test]
    public void Changes_MarkDirty_UntilSynced()
    {
        var p = new ServerPlayer(null, 10f);
        p.MarkSynced();
        Assert.That(p.ResourcesDirty, Is.False);
        p.Add(1f);
        Assert.That(p.ResourcesDirty, Is.True);
        p.MarkSynced();
        p.TrySpend(1f);
        Assert.That(p.ResourcesDirty, Is.True);
    }

    [Test]
    public void Add_IgnoresNegativeAndNaN()
    {
        var p = new ServerPlayer(null, 10f);
        p.Add(-5f);
        p.Add(float.NaN);
        Assert.That(p.Resources, Is.EqualTo(10f));
    }

    [Test]
    public void IncomeAndUpkeepRollEachSecond()
    {
        var p = new ServerPlayer(null, 100f);
        p.AddIncome(5f);
        p.AddIncome(10f);
        p.AddUpkeep(4f);
        Assert.That(p.Resources, Is.EqualTo(115f), "income is added to the balance");
        Assert.That(p.IncomeLastSecond, Is.EqualTo(0f), "nothing is reported until the second rolls");
        p.MarkSynced();
        p.RollSecond();
        Assert.That(p.IncomeLastSecond, Is.EqualTo(15f));
        Assert.That(p.UpkeepLastSecond, Is.EqualTo(4f));
        Assert.That(p.ResourcesDirty, Is.True, "new rates need a sync");
        p.MarkSynced();
        p.RollSecond();
        Assert.That(p.IncomeLastSecond, Is.EqualTo(0f));
        Assert.That(p.UpkeepLastSecond, Is.EqualTo(0f));
        p.MarkSynced();
        p.RollSecond();
        Assert.That(p.ResourcesDirty, Is.False, "unchanged rates do not resync");
    }

    [Test]
    public void IncomeAndUpkeepIgnoreBadAmounts()
    {
        var p = new ServerPlayer(null, 10f);
        p.AddIncome(float.NaN);
        p.AddIncome(-3f);
        p.AddUpkeep(float.NaN);
        p.AddUpkeep(-1f);
        p.RollSecond();
        Assert.That(p.IncomeLastSecond, Is.EqualTo(0f));
        Assert.That(p.UpkeepLastSecond, Is.EqualTo(0f));
        Assert.That(p.Resources, Is.EqualTo(10f));
    }
}

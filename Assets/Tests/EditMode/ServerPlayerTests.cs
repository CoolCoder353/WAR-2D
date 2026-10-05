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
}

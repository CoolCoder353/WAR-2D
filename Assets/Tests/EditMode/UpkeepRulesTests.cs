using NUnit.Framework;

public class UpkeepRulesTests
{
    [Test]
    public void TryCharge_Affordable_Deducts()
    {
        float balance = 10f;
        Assert.That(UpkeepRules.TryCharge(ref balance, 4f), Is.True);
        Assert.That(balance, Is.EqualTo(6f));
    }

    [Test]
    public void TryCharge_Unaffordable_LeavesBalanceAndFails()
    {
        float balance = 3f;
        Assert.That(UpkeepRules.TryCharge(ref balance, 4f), Is.False);
        Assert.That(balance, Is.EqualTo(3f));
    }

    [Test]
    public void TryCharge_FreeOrNegativeCost_AlwaysSucceedsWithoutChange()
    {
        float balance = 0f;
        Assert.That(UpkeepRules.TryCharge(ref balance, 0f), Is.True);
        Assert.That(UpkeepRules.TryCharge(ref balance, -5f), Is.True);
        Assert.That(balance, Is.EqualTo(0f));
    }

    [Test]
    public void DecayDamage_IsPercentOfMaxPerSecond()
    {
        Assert.That(UpkeepRules.DecayDamage(200f, 5f, 2f), Is.EqualTo(20f).Within(1e-4));
    }
}

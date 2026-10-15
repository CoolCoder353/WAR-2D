using NUnit.Framework;

/// <summary>Radar pings: once per contact, re-armed only after a clear spell and a cooldown.</summary>
public class RadarRulesTests
{
    private const float Clear = 5f, Cooldown = 30f;

    private static bool Step(ref RadarWatch w, bool enemy, double t) => RadarRules.Step(ref w, enemy, t, Clear, Cooldown);

    [Test]
    public void FirstContactPingsAndHoldingContactDoesnt()
    {
        var w = new RadarWatch();
        Assert.IsFalse(Step(ref w, false, 0));
        Assert.IsTrue(Step(ref w, true, 1));
        for (double t = 1.2; t < 100; t += 0.2) Assert.IsFalse(Step(ref w, true, t), $"t = {t}");
    }

    [Test]
    public void FlickeringInAndOutDoesntPingAgain()
    {
        var w = new RadarWatch();
        Assert.IsTrue(Step(ref w, true, 0));
        for (int i = 0; i < 50; i++)
        {
            Assert.IsFalse(Step(ref w, false, 100 + i * 2.0));       // gone for 1 s ...
            Assert.IsFalse(Step(ref w, true, 100 + i * 2.0 + 1.0)); // ... back before the clear time
        }
    }

    [Test]
    public void ReArmsAfterTheClearTimeAndTheCooldown()
    {
        var w = new RadarWatch();
        Assert.IsTrue(Step(ref w, true, 0));
        Assert.IsFalse(Step(ref w, false, 1));
        Assert.IsFalse(Step(ref w, false, 7));  // clear for 6 s ...
        Assert.IsFalse(Step(ref w, true, 8), "... but within the 30 s cooldown");
        Assert.IsFalse(Step(ref w, false, 9));
        Assert.IsFalse(Step(ref w, false, 40)); // clear 31 s, cooldown passed
        Assert.IsTrue(Step(ref w, true, 41));
    }
}

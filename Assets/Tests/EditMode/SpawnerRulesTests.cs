using NUnit.Framework;

public class SpawnerRulesTests
{
    [TestCase(0.99f, 1f, false)]
    [TestCase(1.0f, 1f, true)]
    [TestCase(0.5f, 2f, true)]
    [TestCase(10f, 0f, false)]
    public void CanSpawn_RespectsRate(float elapsed, float rate, bool expected)
    {
        Assert.That(SpawnerRules.CanSpawn(elapsed, rate), Is.EqualTo(expected));
    }
}

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

    [Test]
    public void DequeueStopsAtZero()
    {
        int count = 2;
        Assert.That(SpawnerRules.TryDequeue(ref count), Is.True);
        Assert.That(SpawnerRules.TryDequeue(ref count), Is.True);
        Assert.That(count, Is.EqualTo(0));
        Assert.That(SpawnerRules.TryDequeue(ref count), Is.False, "an empty queue stays empty");
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void EnqueueStopsAtMax()
    {
        int count = SpawnerRules.MaxQueue - 1;
        Assert.That(SpawnerRules.TryEnqueue(ref count), Is.True);
        Assert.That(SpawnerRules.TryEnqueue(ref count), Is.False);
        Assert.That(count, Is.EqualTo(SpawnerRules.MaxQueue));
    }

    [Test]
    public void OnlySpawnersProduceUnits()
    {
        Assert.That(SpawnerRules.UnitFor(BuildingType.SmallUnitSpawner), Is.EqualTo(UnitType.Tank));
        Assert.That(SpawnerRules.UnitFor(BuildingType.Miner), Is.EqualTo(UnitType.None));
        Assert.That(SpawnerRules.UnitFor(BuildingType.Base), Is.EqualTo(UnitType.None));
    }
}

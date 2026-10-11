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

    [Test]
    public void QueueStepsClampToTheQueue()
    {
        int count = 95;
        Assert.AreEqual(5, SpawnerRules.Apply(ref count, 50), "+50 stops at the max");
        Assert.AreEqual(SpawnerRules.MaxQueue, count);
        count = 0;
        Assert.AreEqual(0, SpawnerRules.Apply(ref count, -1));
        Assert.AreEqual(10, SpawnerRules.Apply(ref count, 10));
        Assert.IsTrue(SpawnerRules.IsQueueStep(50) && SpawnerRules.IsQueueStep(-1));
        Assert.IsFalse(SpawnerRules.IsQueueStep(7) || SpawnerRules.IsQueueStep(-50) || SpawnerRules.IsQueueStep(1000));
        count = -3;
        Assert.AreEqual(0, SpawnerRules.Apply(ref count, 10), "an invalid count is left alone");
    }
}

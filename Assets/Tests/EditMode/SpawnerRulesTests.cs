using NUnit.Framework;

public class SpawnerRulesTests
{
    [TestCase(0.99f, 1f, false)]
    [TestCase(1.0f, 1f, true)]
    [TestCase(2.0f, 0.7f, true)]
    [TestCase(10f, 0f, false)]
    public void CanSpawn_RespectsBuildSeconds(float progress, float buildSeconds, bool expected)
    {
        Assert.That(SpawnerRules.CanSpawn(progress, buildSeconds), Is.EqualTo(expected));
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
        count = -4;
        Assert.AreEqual(0, SpawnerRules.Apply(ref count, 10), "an invalid count is left alone");
    }

    [Test]
    public void ABigQueueCanBeCutByTenOrCleared()
    {
        // Playtest 2026-11-02: Shift + − removes ten, Ctrl + − clears the queue.
        int count = 73;
        Assert.IsTrue(SpawnerRules.IsQueueStep(-10) && SpawnerRules.IsQueueStep(-SpawnerRules.MaxQueue));
        SpawnerRules.Apply(ref count, -10);
        Assert.AreEqual(63, count);
        SpawnerRules.Apply(ref count, -SpawnerRules.MaxQueue);
        Assert.AreEqual(0, count);
    }

    [Test]
    public void ABigSelectionGoesAsSeveralCommands()
    {
        int[] ids = new int[SpawnerRules.MaxSpawnersPerCommand * 2 + 3];
        for (int i = 0; i < ids.Length; i++) ids[i] = i + 1;
        var sent = new System.Collections.Generic.List<int>();
        int batches = 0;
        foreach (int[] batch in SpawnerRules.Batches(ids))
        {
            Assert.LessOrEqual(batch.Length, SpawnerRules.MaxSpawnersPerCommand);
            sent.AddRange(batch);
            batches++;
        }
        Assert.AreEqual(3, batches);
        CollectionAssert.AreEqual(ids, sent, "every id once, in order");
        Assert.IsEmpty(SpawnerRules.Batches(new int[0]), "nothing selected: nothing sent");
    }

    [Test]
    public void EachProducerMakesItsRolesUnits()
    {
        CollectionAssert.AreEqual(new[] { UnitType.Builder }, SpawnerRules.Produces(BuildingType.Base));
        CollectionAssert.AreEqual(new[] { UnitType.Scout, UnitType.Tank, UnitType.Flamethrower }, SpawnerRules.Produces(BuildingType.Barracks));
        CollectionAssert.AreEqual(new[] { UnitType.Heavy, UnitType.Artillery, UnitType.Sniper }, SpawnerRules.Produces(BuildingType.HeavyFactory));
        CollectionAssert.AreEqual(new[] { UnitType.Digger, UnitType.Bomber }, SpawnerRules.Produces(BuildingType.SiegeWorkshop));
        Assert.IsEmpty(SpawnerRules.Produces(BuildingType.Miner));
        Assert.IsFalse(SpawnerRules.MayProduce(BuildingType.Base, UnitType.Tank));
        Assert.IsFalse(SpawnerRules.MayProduce(BuildingType.Barracks, UnitType.Digger), "Diggers moved to the Siege Workshop");
    }

    [Test]
    public void ANewProducerStartsOnItsFirstType()
    {
        Assert.AreEqual(UnitType.Builder, SpawnerRules.DefaultUnit(BuildingType.Base));
        Assert.AreEqual(UnitType.Scout, SpawnerRules.DefaultUnit(BuildingType.Barracks));
        Assert.AreEqual(UnitType.None, SpawnerRules.DefaultUnit(BuildingType.Radar));
    }
}

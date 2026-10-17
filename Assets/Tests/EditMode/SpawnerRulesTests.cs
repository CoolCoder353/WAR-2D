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
    public void EachProducerMakesItsRolesUnits()
    {
        CollectionAssert.AreEqual(new[] { UnitType.Builder }, SpawnerRules.Produces(BuildingType.Base));
        CollectionAssert.AreEqual(new[] { UnitType.Scout, UnitType.Tank, UnitType.Flamethrower }, SpawnerRules.Produces(BuildingType.Barracks));
        CollectionAssert.AreEqual(new[] { UnitType.Heavy, UnitType.Artillery, UnitType.Sniper }, SpawnerRules.Produces(BuildingType.HeavyFactory));
        CollectionAssert.AreEqual(new[] { UnitType.Digger, UnitType.SuicideBomber }, SpawnerRules.Produces(BuildingType.SiegeWorkshop));
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

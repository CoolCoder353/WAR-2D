using Config;
using NUnit.Framework;
using Unity.Collections;
using WAR2D.Rules;

public class DamageTableTests
{
    private static NativeArray<float> Table(out int types)
    {
        var errors = new System.Collections.Generic.List<string>();
        GameConfigData c = ConfigParser.Parse(ConfigParserTests.ValidXml, errors);
        Assert.That(errors, Is.Empty);
        types = System.Enum.GetValues(typeof(UnitType)).Length;
        return DamageTable.Build(c.Damage, types, Allocator.Temp);
    }

    [Test]
    public void UnknownPairReturnsOne()
    {
        var table = Table(out _);
        Assert.AreEqual(1f, DamageTable.Get(table, (int)UnitType.None, TargetClass.Wall));
        table.Dispose();
    }

    [Test]
    public void TankVersusWallFromConfig()
    {
        var table = Table(out _);
        Assert.AreEqual(0.5f, DamageTable.Get(table, (int)UnitType.Tank, TargetClass.Wall), 1e-6f);
        Assert.AreEqual(1f, DamageTable.Get(table, (int)UnitType.Tank, TargetClass.LightUnit), 1e-6f);
        table.Dispose();
    }

    [Test]
    public void OutOfRangeAttackerReturnsOne()
    {
        var table = Table(out int types);
        Assert.AreEqual(1f, DamageTable.Get(table, DamageTable.Rows + 5, TargetClass.LightUnit));
        Assert.AreEqual(1f, DamageTable.Get(table, -1, TargetClass.LightUnit));
        table.Dispose();
    }

    [Test]
    public void TheRowCountsMatchTheEnums()
    {
        Assert.AreEqual(System.Enum.GetValues(typeof(UnitType)).Length, DamageTable.UnitTypeCount);
        Assert.AreEqual(System.Enum.GetValues(typeof(BuildingType)).Length, DamageTable.BuildingTypeCount);
    }

    [Test]
    public void BuildingRowsComeAfterTheUnitRows()
    {
        var table = Table(out int types);
        Assert.AreEqual(types, DamageTable.BuildingAttacker(BuildingType.None));
        Assert.AreEqual((int)UnitType.Scout, DamageTable.UnitAttacker(UnitType.Scout));
        Assert.AreEqual(1.5f, DamageTable.Get(table, DamageTable.BuildingAttacker(BuildingType.GunTurret), TargetClass.LightUnit), 1e-6f);
        Assert.AreEqual(0.4f, DamageTable.Get(table, DamageTable.BuildingAttacker(BuildingType.GunTurret), TargetClass.HeavyUnit), 1e-6f);
        Assert.AreEqual(0.5f, DamageTable.Get(table, DamageTable.BuildingAttacker(BuildingType.GunTurret), TargetClass.Wall), 1e-6f);
        Assert.AreEqual(1f, DamageTable.Get(table, DamageTable.BuildingAttacker(BuildingType.Radar), TargetClass.LightUnit), "a building with no row reads 1");
        Assert.AreEqual(0.5f, DamageTable.Get(table, DamageTable.UnitAttacker(UnitType.Tank), TargetClass.Wall), 1e-6f, "unit rows are unchanged");
        table.Dispose();
    }
}

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
        Assert.AreEqual(1f, DamageTable.Get(table, (int)UnitType.Tank, TargetClass.Unit), 1e-6f);
        table.Dispose();
    }

    [Test]
    public void OutOfRangeAttackerReturnsOne()
    {
        var table = Table(out int types);
        Assert.AreEqual(1f, DamageTable.Get(table, types + 5, TargetClass.Unit));
        Assert.AreEqual(1f, DamageTable.Get(table, -1, TargetClass.Unit));
        table.Dispose();
    }
}

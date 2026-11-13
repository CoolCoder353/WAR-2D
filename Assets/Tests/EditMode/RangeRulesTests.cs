using NUnit.Framework;
using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.Client.Overlays;

/// <summary>v0.18 held-key rings: which rings, merged and capped; placement reach; all from the type alone.</summary>
public class RangeRulesTests
{
    private static readonly Config.GameConfigData Config_ = LoadConfig();
    private static Config.GameConfigData LoadConfig() { Config.ConfigLoader.ResetForTests(); return Config.ConfigLoader.LoadConfig(); }

    [Test]
    public void AUnitsRingsComeFromItsType()
    {
        RingSet arty = RangeRules.OfUnit(Config_.GetUnit(UnitType.Artillery), new float2(1, 1), 7);
        Config.UnitConfig artillery = Config_.GetUnit(UnitType.Artillery);
        Assert.AreEqual((artillery.Range, artillery.MinRange, artillery.Sight), (arty.Range, arty.Min, arty.Sight));
        Assert.Greater(arty.Range, arty.Sight, "v0.20: it fires past its sight");
        RingSet builder = RangeRules.OfUnit(Config_.GetUnit(UnitType.Builder), float2.zero, 7);
        Assert.AreEqual(0f, builder.Range, "a Builder can't attack");
        RingSet bomber = RangeRules.OfUnit(Config_.GetUnit(UnitType.Bomber), float2.zero, 7);
        Assert.AreEqual(Config_.GetUnit(UnitType.Bomber).BlastRadius, bomber.Range, "a bomber's reach is its blast");
    }

    /// <summary>Each defence's ring is its own reach in the config: the turret's range, the Radar's sight, the Jammer's jam radius.</summary>
    [TestCase(BuildingType.GunTurret)]
    [TestCase(BuildingType.Radar)]
    [TestCase(BuildingType.Jammer)]
    [TestCase(BuildingType.Barracks)]
    public void PlacementShowsTheBuildingsReach(BuildingType type)
    {
        float reach = type switch
        {
            BuildingType.GunTurret => Config_.GetBuilding(type).Range,
            BuildingType.Radar => Config_.GetBuilding(type).Sight,
            BuildingType.Jammer => Config_.Siege.JamRadius,
            _ => 0f,
        };
        if (type != BuildingType.Barracks) Assert.Greater(reach, 0f, "a defence has a reach");
        Assert.AreEqual(reach, WAR2D.Client.DefenceView.RingRadius(type));
    }

    private static RingSet Tank(float x, float y = 0f, int owner = 1) => new RingSet { At = new float2(x, y), Range = 5f, Sight = 8f, Group = 3, Owner = owner };

    [Test]
    public void CloseUnitsOfATypeShareARing()
    {
        var sets = new List<RingSet>();
        for (int i = 0; i < 200; i++) sets.Add(Tank(i * 0.05f, i % 7 * 0.05f));
        var kept = new List<RingSet>();
        RangeRules.Choose(sets, float2.zero, 64, 0.8f, kept);
        Assert.That(kept.Count, Is.LessThan(10));
    }

    [Test]
    public void DifferentOwnersDontShare()
    {
        var kept = new List<RingSet>();
        RangeRules.Choose(new List<RingSet> { Tank(0f, 0f, 1), Tank(0f, 0f, 2) }, float2.zero, 64, 0.8f, kept);
        Assert.AreEqual(2, kept.Count);
    }

    [Test]
    public void TheCapKeepsThoseNearestTheCentre()
    {
        var sets = new List<RingSet>();
        for (int i = 0; i < 100; i++) sets.Add(Tank(i * 50f));
        var kept = new List<RingSet>();
        RangeRules.Choose(sets, float2.zero, 64, 0.8f, kept);
        Assert.AreEqual(64, kept.Count);
        Assert.AreEqual(0f, kept[0].At.x);
        foreach (RingSet set in kept) Assert.Less(set.At.x, 64 * 50f);
    }
}

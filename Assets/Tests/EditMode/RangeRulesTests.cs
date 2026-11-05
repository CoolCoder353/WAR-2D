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
        Assert.AreEqual((18f, 4f, 18f), (arty.Range, arty.Min, arty.Sight));
        RingSet builder = RangeRules.OfUnit(Config_.GetUnit(UnitType.Builder), float2.zero, 7);
        Assert.AreEqual(0f, builder.Range, "a Builder can't attack");
        RingSet bomber = RangeRules.OfUnit(Config_.GetUnit(UnitType.SuicideBomber), float2.zero, 7);
        Assert.AreEqual(Config_.GetUnit(UnitType.SuicideBomber).BlastRadius, bomber.Range, "a bomber's reach is its blast");
    }

    [TestCase(BuildingType.GunTurret, 9f)]
    [TestCase(BuildingType.Radar, 20f)]
    [TestCase(BuildingType.Jammer, 12f)]
    [TestCase(BuildingType.LargeRadar, 30f)]
    [TestCase(BuildingType.LargeJammer, 18f)]
    [TestCase(BuildingType.Barracks, 0f)]
    public void PlacementShowsTheBuildingsReach(BuildingType type, float reach) =>
        Assert.AreEqual(reach, WAR2D.Client.DefenceView.RingRadius(type));

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

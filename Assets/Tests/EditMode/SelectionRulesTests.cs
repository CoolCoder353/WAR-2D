using NUnit.Framework;
using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>v0.18: the drag box prefers the army over Builders. 2026-11-07: a building box takes one type.</summary>
public class SelectionRulesTests
{
    private static readonly Dictionary<int, UnitType> Types = new Dictionary<int, UnitType>
    {
        [1] = UnitType.Builder, [2] = UnitType.Builder, [3] = UnitType.Tank, [4] = UnitType.Digger, [5] = UnitType.Bomber,
    };

    private static List<int> Box(params int[] ids)
    {
        var caught = new List<int>(ids);
        SelectionRules.PreferArmy(caught, id => Types[id]);
        return caught;
    }

    [Test] public void BuildersAndTheArmyKeepTheArmy() => CollectionAssert.AreEquivalent(new[] { 3 }, Box(1, 2, 3));
    [Test] public void OnlyBuildersKeepThem() => CollectionAssert.AreEquivalent(new[] { 1, 2 }, Box(1, 2));
    [Test] public void DiggersCountAsArmy() => CollectionAssert.AreEquivalent(new[] { 4 }, Box(1, 4));
    [Test] public void BombersCountAsArmy() => CollectionAssert.AreEquivalent(new[] { 5 }, Box(2, 5));
    [Test] public void AnEmptyBoxStaysEmpty() => CollectionAssert.IsEmpty(Box());

    // Two Heavy Factories side by side, then a Barracks to their right (footprint corners).
    private static List<int> Buildings(float2 dragStart, BuildingType keep = BuildingType.None) => SelectionRules.OneBuildingType(
        new List<(int, BuildingType, float2, float2)>
        {
            (1, BuildingType.HeavyFactory, new float2(10, 10), new float2(13, 13)),
            (2, BuildingType.HeavyFactory, new float2(14, 10), new float2(17, 13)),
            (3, BuildingType.Barracks, new float2(18, 10), new float2(20, 12)),
        }, dragStart, keep);

    [Test] public void ABuildingBoxTakesTheTypeItReachesFirst() => CollectionAssert.AreEquivalent(new[] { 1, 2 }, Buildings(new float2(8, 8)));
    [Test] public void BegunOnTheOtherSideItTakesTheOtherType() => CollectionAssert.AreEquivalent(new[] { 3 }, Buildings(new float2(22, 14)));
    [Test] public void BegunOnABuildingItTakesThatType() => CollectionAssert.AreEquivalent(new[] { 3 }, Buildings(new float2(19, 11)));
    [Test] public void AddingKeepsTheTypeAlreadySelected() => CollectionAssert.AreEquivalent(new[] { 3 }, Buildings(new float2(8, 8), BuildingType.Barracks));
    [Test] public void AnEmptyBuildingBoxStaysEmpty() => CollectionAssert.IsEmpty(SelectionRules.OneBuildingType(new List<(int, BuildingType, float2, float2)>(), 0f));
}

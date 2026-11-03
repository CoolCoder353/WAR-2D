using NUnit.Framework;
using System.Collections.Generic;

/// <summary>v0.18: the drag box prefers the army over Builders.</summary>
public class SelectionRulesTests
{
    private static readonly Dictionary<int, UnitType> Types = new Dictionary<int, UnitType>
    {
        [1] = UnitType.Builder, [2] = UnitType.Builder, [3] = UnitType.Tank, [4] = UnitType.Digger, [5] = UnitType.SuicideBomber,
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
}

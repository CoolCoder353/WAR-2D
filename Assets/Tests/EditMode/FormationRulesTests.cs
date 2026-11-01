using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Formation slots (v0.16): a block at the goal, front row towards the order's direction, heavy units in front.</summary>
public class FormationRulesTests
{
    [Test]
    public void HeavyUnitsTakeTheFrontRowAndRangedTheBack()
    {
        var types = new List<UnitType> { UnitType.Artillery, UnitType.Heavy, UnitType.Scout, UnitType.Tank, UnitType.Sniper, UnitType.Scout };
        var positions = new List<float2>();
        for (int i = 0; i < types.Count; i++) positions.Add(new float2(i, 0));
        var offsets = new List<float2>();
        FormationRules.Layout(types, positions, goal: new float2(2.5f, 30), spacing: 1.2f, offsets); // moving north (+y)
        float Front(int i) => offsets[i].y;
        Assert.Greater(Front(1), Front(2), "Heavy ahead of Scout");
        Assert.Greater(Front(3), Front(2), "Tank ahead of Scout");
        Assert.Greater(Front(2), Front(0), "Scout ahead of Artillery");
        Assert.Greater(Front(2), Front(4), "Scout ahead of Sniper");
    }

    [Test]
    public void SlotsAreSpacedAndCentredOnTheGoal()
    {
        var types = new List<UnitType>(); var positions = new List<float2>();
        for (int i = 0; i < 9; i++) { types.Add(UnitType.Tank); positions.Add(new float2(i % 3, -(i / 3))); }
        var offsets = new List<float2>();
        FormationRules.Layout(types, positions, new float2(1, 40), 1.2f, offsets);
        float2 sum = 0; foreach (float2 o in offsets) sum += o;
        Assert.Less(math.length(sum / offsets.Count), 0.7f, "centred");
        for (int a = 0; a < offsets.Count; a++)
        for (int b = a + 1; b < offsets.Count; b++)
            Assert.GreaterOrEqual(math.distance(offsets[a], offsets[b]), 1.19f);
    }

    [Test]
    public void TheBlockIsWiderThanDeep()
    {
        var types = new List<UnitType>(); var positions = new List<float2>();
        for (int i = 0; i < 12; i++) { types.Add(UnitType.Tank); positions.Add(new float2(i, 0)); }
        var offsets = new List<float2>();
        FormationRules.Layout(types, positions, new float2(5, 50), 1.2f, offsets);
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (float2 o in offsets) { minX = math.min(minX, o.x); maxX = math.max(maxX, o.x); minY = math.min(minY, o.y); maxY = math.max(maxY, o.y); }
        Assert.Greater(maxX - minX, maxY - minY);
    }
}

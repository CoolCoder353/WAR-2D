using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client.Overlays;

/// <summary>Target lines (v0.16, Figma T): which lines draw, how long they hold, the cap and their colour.</summary>
public class TargetLineRulesTests
{
    [Test]
    public void ALineHoldsAfterTheLastShotThenGoes()
    {
        var rules = new TargetLineRules(holdSeconds: 1.5f, max: 200);
        rules.Shot(attackerId: 7, targetId: 42, now: 10f);
        Assert.IsTrue(rules.TryTarget(7, 11f, out int target));
        Assert.AreEqual(42, target);
        Assert.IsFalse(rules.TryTarget(7, 11.6f, out _), "1.5 s after the last shot");
    }

    [Test]
    public void ANewShotMovesTheLine()
    {
        var rules = new TargetLineRules(1.5f, 200);
        rules.Shot(7, 42, 10f);
        rules.Shot(7, 43, 11f);
        Assert.IsTrue(rules.TryTarget(7, 12f, out int target));
        Assert.AreEqual(43, target);
    }

    [Test]
    public void AnUnnamedShotDrawsNoLine()
    {
        var rules = new TargetLineRules(1.5f, 200);
        rules.Shot(7, 0, 10f);
        Assert.IsFalse(rules.TryTarget(7, 10f, out _));
    }

    [Test]
    public void ForgetDropsOnlyExpiredLines()
    {
        var rules = new TargetLineRules(1.5f, 200);
        rules.Shot(1, 42, 10f);
        rules.Shot(2, 43, 11f);
        rules.Forget(11.8f);
        Assert.IsFalse(rules.TryTarget(1, 11.8f, out _));
        Assert.IsTrue(rules.TryTarget(2, 11.8f, out _));
    }

    [Test]
    public void WallTargetsAreOrangeAndOthersRed()
    {
        Assert.AreEqual(TargetLineKind.Wall, TargetLineRules.KindOf(-5));
        Assert.AreEqual(TargetLineKind.Attack, TargetLineRules.KindOf(1048577));
    }

    [Test]
    public void TheCapKeepsTheLinesNearestTheViewCentre()
    {
        var rules = new TargetLineRules(1.5f, 2);
        var from = new List<(int id, float2 at)> { (1, new float2(100, 100)), (2, new float2(1, 1)), (3, new float2(50, 50)) };
        List<int> kept = rules.Nearest(from, new float2(0, 0), rules.Max);
        CollectionAssert.AreEqual(new[] { 2, 3 }, kept.ConvertAll(i => from[i].id));
    }
}

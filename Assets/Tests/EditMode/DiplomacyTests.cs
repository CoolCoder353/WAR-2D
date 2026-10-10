using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>The diplomacy masks: starting state from teams, one-way changes, and their effect on combat.</summary>
public class DiplomacyTests
{
    private const int A = 0, B = 1, C = 2;

    private static Diplomacy FromTeams()
    {
        var d = new Diplomacy();
        d.Join(A, 0, new List<(int, int)>());
        d.Join(B, 0, new List<(int, int)> { (A, 0) });
        d.Join(C, 1, new List<(int, int)> { (A, 0), (B, 0) });
        return d;
    }

    [Test]
    public void JoinFromTeams()
    {
        Diplomacy d = FromTeams();
        Assert.IsFalse(d.Attacks(A, B));
        Assert.IsFalse(d.Attacks(B, A));
        Assert.IsTrue(d.SharesVisionWith(A, B));
        Assert.IsTrue(d.SharesVisionWith(B, A));
        Assert.IsTrue(d.Attacks(A, C));
        Assert.IsTrue(d.Attacks(C, A));
        Assert.IsFalse(d.SharesVisionWith(A, C));
        Assert.IsFalse(d.SharesVisionWith(C, A));
        Assert.IsFalse(d.Attacks(A, A), "nobody attacks themselves");
        Assert.IsTrue(d.Dirty);
    }

    [Test]
    public void SetAttackIsOneWay()
    {
        Diplomacy d = FromTeams();
        d.SetAttack(A, C, false);
        Assert.IsFalse(d.Attacks(A, C));
        Assert.IsTrue(d.Attacks(C, A));
    }

    [Test]
    public void SharedWithListsSharers()
    {
        Diplomacy d = FromTeams();
        d.SetShareVision(C, A, true);
        Assert.AreEqual((ushort)((1 << B) | (1 << C)), d.SharedWith(A));
    }

    [Test]
    public void LeaveClearsRowAndColumn()
    {
        Diplomacy d = FromTeams();
        d.Leave(C);
        Assert.AreEqual(0, d.AttackMask(C));
        Assert.AreEqual(0, d.ShareVisionMask(C));
        Assert.IsFalse(d.Attacks(A, C));
        Assert.IsFalse(d.Attacks(B, C));
        Assert.IsFalse(d.Attacks(A, B), "other rows are untouched");
        Assert.IsTrue(d.SharesVisionWith(A, B));
    }

    [Test]
    public void OutOfRangeSlotsAreIgnored()
    {
        var d = new Diplomacy();
        Assert.DoesNotThrow(() => d.SetAttack(-1, 99, true));
        Assert.DoesNotThrow(() => d.Leave(255));
        Assert.IsFalse(d.Attacks(-1, 99));
    }

    private const int OwnerC = 303;

    private static void SetAttack(SimHarness sim, int from, int to, bool on) =>
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetAttack, OwnerId = from, TargetOwnerId = to, Flag = on });

    [Test]
    public void OneWayPeace()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);
        int[] p1 = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        int[] p2 = sim.Spawn(SimHarness.OwnerB, new float2(12, 10));
        sim.Spawn(OwnerC, new float2(40, 40));
        SetAttack(sim, SimHarness.OwnerA, SimHarness.OwnerB, false); // P1 attacks only P3; P2 still attacks P1
        bool p2Targeted = false;
        for (int t = 0; t < 20; t++)
        {
            sim.Tick();
            Assert.AreNotEqual(p2[0], sim.UnitById(p1[0]).TargetId, $"P1 targeted P2 on tick {t}");
            p2Targeted |= sim.UnitById(p2[0]).TargetId == p1[0];
        }
        Assert.IsTrue(p2Targeted, "P2 attacks P1");
    }

    [Test]
    public void StopAttackingDropsTarget()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        int[] b = sim.Spawn(SimHarness.OwnerB, new float2(12, 10));
        for (int t = 0; t < 3; t++) sim.Tick();
        Assert.AreEqual(b[0], sim.UnitById(a[0]).TargetId, "fighting first");
        SetAttack(sim, SimHarness.OwnerA, SimHarness.OwnerB, false);
        sim.Tick();
        Assert.AreEqual(-1, sim.UnitById(a[0]).TargetId);
        Assert.AreEqual(a[0], sim.UnitById(b[0]).TargetId, "B still fights A");
    }

    [TestCase(true, GameState.Playing, PlayerState.Playing, PlayerState.Playing, 1, 2, 10.0, 0.0, true)]
    [TestCase(false, GameState.Playing, PlayerState.Playing, PlayerState.Playing, 1, 2, 10.0, 0.0, false)]
    [TestCase(true, GameState.Lobby, PlayerState.Playing, PlayerState.Playing, 1, 2, 10.0, 0.0, false)]
    [TestCase(true, GameState.Playing, PlayerState.Eliminated, PlayerState.Playing, 1, 2, 10.0, 0.0, false)]
    [TestCase(true, GameState.Playing, PlayerState.Playing, PlayerState.Eliminated, 1, 2, 10.0, 0.0, false)]
    [TestCase(true, GameState.Playing, PlayerState.Playing, PlayerState.Playing, 1, 1, 10.0, 0.0, false)]
    [TestCase(true, GameState.Playing, PlayerState.Playing, PlayerState.Playing, 1, 2, 10.0, 9.0, false)]
    public void CanChangeRules(bool enabled, GameState state, PlayerState sender, PlayerState target, int from, int to, double now, double last, bool expected)
    {
        Assert.AreEqual(expected, DiplomacyRules.CanChange(enabled, state, sender, target, from, to, now, last, 2f));
    }

    [Test]
    public void CanChangeRejectsUnknownPlayers() =>
        Assert.IsFalse(DiplomacyRules.CanChange(true, GameState.Playing, PlayerState.Playing, null, 1, 2, 10, double.NegativeInfinity, 2f));
}

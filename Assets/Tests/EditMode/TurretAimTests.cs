using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Client.Render;

/// <summary>A turret turns at a limited rate to what it fires at, then back to the way it faces, and its flash stands at the barrel's tip.</summary>
public class TurretAimTests
{
    private const float Tolerance = 1e-3f;

    [Test]
    public void ATurretTurnsNoFasterThanItsRateAndStopsOnItsGoal()
    {
        Assert.AreEqual(0.5f, TurretAim.Step(0f, 2f, 0.5f), Tolerance);
        Assert.AreEqual(-0.5f, TurretAim.Step(0f, -2f, 0.5f), Tolerance);
        Assert.AreEqual(2f, TurretAim.Step(1.9f, 2f, 0.5f), Tolerance, "it doesn't overshoot");
        Assert.AreEqual(2f, TurretAim.Step(2f, 2f, 0.5f), Tolerance);
    }

    [Test]
    public void ATurretTurnsTheShortWayRound()
    {
        float nearlyEast = 350f * Mathf.Deg2Rad, justPastEast = 10f * Mathf.Deg2Rad;
        Assert.AreEqual(350f * Mathf.Deg2Rad + 5f * Mathf.Deg2Rad, TurretAim.Step(nearlyEast, justPastEast, 5f * Mathf.Deg2Rad), Tolerance, "up through east, not back round");
        Assert.AreEqual(0f, Mathf.DeltaAngle(10f, TurretAim.Step(nearlyEast, justPastEast, 1f) * Mathf.Rad2Deg), 0.01f, "it stops on the goal, whichever turn it is on");
    }

    [Test]
    public void ATurretAimsWhileItHoldsItsAimElseFacesAsTheHullDoes()
    {
        const float hull = 1f, aim = 2f;
        Assert.AreEqual(aim, TurretAim.Goal(hull, aim, true));
        Assert.AreEqual(hull, TurretAim.Goal(hull, aim, false));
    }

    [TestCase(UnitType.Tank, true), TestCase(UnitType.Scout, true), TestCase(UnitType.Sniper, true), TestCase(UnitType.Heavy, true), TestCase(UnitType.Artillery, true)]
    [TestCase(UnitType.Flamethrower, false), TestCase(UnitType.Builder, false), TestCase(UnitType.Digger, false), TestCase(UnitType.Bomber, false)]
    public void OnlyTheGunsTurn(UnitType type, bool turns)
    {
        Assert.AreEqual(turns, TurretAim.Has(type));
        Assert.AreEqual(turns, TurretAim.TurnRate(type) > 0f);
        Assert.AreEqual(turns, new UnitFrames.Table(ArtAtlas.Load(ArtAtlas.Units)).TurretFirst[(int)type] >= 0, "its turret is in the atlas");
    }

    [Test]
    public void TheFacingIsTheNearestDrawnOne()
    {
        Assert.AreEqual(0, TurretAim.Facing(0f));
        Assert.AreEqual(1, TurretAim.Facing(25f * Mathf.Deg2Rad));
        Assert.AreEqual(8, TurretAim.Facing(Mathf.PI));
        Assert.AreEqual(15, TurretAim.Facing(-20f * Mathf.Deg2Rad));
        Assert.AreEqual(22.5f * Mathf.Deg2Rad, TurretAim.FacingAngle(1), Tolerance);
    }

    /// <summary>The owner's tuned distances, in art pixels from the unit's centre, with the turret straight ahead: forward and to the left.</summary>
    [TestCase(UnitType.Tank, 0, 34f, 0f)]
    [TestCase(UnitType.Scout, 0, 17f, -3f)]
    [TestCase(UnitType.Sniper, 0, 56f, 0f)] // v0.20: the 2×2 Sniper's long barrel (pivot -10 + reach 66)
    [TestCase(UnitType.Heavy, 0, 60f, 6f)]
    [TestCase(UnitType.Heavy, 1, 60f, -6f)]
    public void AFlashStandsWhereTheOwnerTunedIt(UnitType type, int barrel, float forward, float left)
    {
        foreach (float degrees in new[] { 0f, 45f, 90f, 225f })
        {
            float a = degrees * Mathf.Deg2Rad;
            Vector2 along = new Vector2(Mathf.Cos(a), Mathf.Sin(a)), side = new Vector2(-along.y, along.x);
            Vector2 expected = along * forward + side * left, muzzle = TurretAim.Muzzle(type, a, a, barrel);
            Assert.AreEqual(expected.x, muzzle.x, Tolerance, $"{type} at {degrees}");
            Assert.AreEqual(expected.y, muzzle.y, Tolerance, $"{type} at {degrees}");
        }
    }

    [Test]
    public void TheHeavyFiresTwoBarrelsAndTheOthersOne()
    {
        Assert.AreEqual(2, TurretAim.Barrels(UnitType.Heavy));
        Assert.AreEqual(1, TurretAim.Barrels(UnitType.Tank));
    }

    [Test]
    public void ATurnedTurretsFlashSwingsAboutItsRealPivotNotTheUnitsCentre()
    {
        // The Heavy's pivot is 6 px behind its centre: a turret turned right round has its muzzle 66 px behind the pivot.
        Vector2 muzzle = TurretAim.Muzzle(UnitType.Heavy, 0f, Mathf.PI, 0);
        Assert.AreEqual(-72f, muzzle.x, Tolerance);
    }

    [Test]
    public void TheRaisedBarrelsBoreAndFlashStandWhereTheOwnerTunedThem()
    {
        Vector2 bore = TurretAim.ArtilleryBore(0f, 0f), flash = TurretAim.ArtilleryFlash(0f, 0f), axis = TurretAim.ArtilleryBarrelAxis(0f);
        Assert.AreEqual(40f, bore.x, Tolerance);
        Assert.AreEqual(17f, bore.y, Tolerance);
        Assert.AreEqual(5f, (flash - bore).magnitude, Tolerance);
        Assert.AreEqual(26.57f, Mathf.Atan2(axis.y, axis.x) * Mathf.Rad2Deg, 0.01f, "the barrel rises 16 px over its 32");
    }

    [Test]
    public void TheArtilleryBarrelIsRaisedOnlyWhileItAims()
    {
        Assert.AreEqual(0, TurretAim.TurretFrame(3, false, false), "level");
        Assert.AreEqual(1, TurretAim.TurretFrame(3, true, false), "raised");
        Assert.AreEqual(2, TurretAim.TurretFrame(3, true, true), "raised, in recoil");
        Assert.AreEqual(1, TurretAim.TurretFrame(2, false, true), "another gun only recoils");
    }
}

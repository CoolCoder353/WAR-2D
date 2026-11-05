using NUnit.Framework;
using UnityEngine;
using WAR2D.Art.Render;

/// <summary>Shots stay between muzzle and target, a shell flies an arc over its ground line, a flame stops at its target, and craters reuse their pool.</summary>
public class CombatEffectsTests
{
    [SetUp]
    public void SetUp()
    {
        Effects.Clear();
        Decals.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        Effects.Clear();
        Decals.Clear();
    }

    private const float Tolerance = 1e-3f;

    [Test]
    public void AShotGrowsFromTheMuzzleAndItsHeadEndsOnTheTarget()
    {
        Vector3 from = new Vector3(1f, 0f, 0f), to = new Vector3(11f, 0f, 0f);
        Vector3 centre = Effects.ShotCentre(from, to, 0.1f, 4f, out float drawn);
        Assert.AreEqual(1f, drawn, Tolerance, "only as long as it has flown");
        Assert.AreEqual(1.5f, centre.x, Tolerance);
        centre = Effects.ShotCentre(from, to, 1f, 4f, out drawn);
        Assert.AreEqual(4f, drawn, Tolerance);
        Assert.AreEqual(to.x, centre.x + drawn * 0.5f, Tolerance, "its head is on the target, never past it");
    }

    [Test]
    public void AShellFliesAnArcFromTheGunToItsLandingPoint()
    {
        Vector3 from = new Vector3(2f, 3f, 0f), to = new Vector3(14f, 3f, 0f);
        Assert.AreEqual(from, Effects.ShellPoint(from, to, 0f));
        Assert.AreEqual(to, Effects.ShellPoint(from, to, 1f));
        Assert.AreEqual(3f + 12f * Effects.ShellArcShare, Effects.ShellPoint(from, to, 0.5f).y, Tolerance);
        Assert.AreEqual(Effects.ShellMaxHeight, Effects.ShellPoint(Vector3.zero, new Vector3(100f, 0f, 0f), 0.5f).y, Tolerance, "the arc is capped");
    }

    [Test]
    public void AFlameStopsAtItsTargetAndNeverPastItsRange()
    {
        Vector2 unit = new Vector2(5f, 5f);
        Assert.AreEqual(new Vector2(6f, 5f), Effects.FlameEnd(unit, new Vector2(6f, 5f), 2.5f), "a target in range: exactly on it");
        Vector2 far = Effects.FlameEnd(unit, new Vector2(5f, 15f), 2.5f);
        Assert.AreEqual(5f, far.x, Tolerance);
        Assert.AreEqual(7.5f, far.y, Tolerance, "a building's far centre: only as far as its range reaches");
        Assert.AreEqual(unit, Effects.FlameEnd(unit, unit, 2.5f));
    }

    [Test]
    public void TheFlameArtEndsAtItsRightEdgeAndItsFireStaysInsideTheSplash()
    {
        EffectFrame stream = EffectGen.FlameStream(0);
        for (int y = 0; y < stream.Height; y++)
            Assert.AreEqual(0, stream.Pixels[y * stream.Width + stream.Width - 1].a, "nothing reaches the target's far side");
        EffectFrame splash = EffectGen.FlameSplash(0);
        float radius = EffectGen.FlameSplashTiles * RenderGen.TileSize, c = splash.Width / 2f;
        for (int y = 0; y < splash.Height; y++)
        for (int x = 0; x < splash.Width; x++)
        {
            float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
            if (d > radius) Assert.AreEqual(0, splash.Pixels[y * splash.Width + x].a, $"{x},{y} is outside the splash");
        }
    }

    [Test]
    public void AFlameIsWeakerAtTheEdgeOfItsSplash()
    {
        EffectFrame splash = EffectGen.FlameSplash(0);
        float c = splash.Width / 2f, inner = 0f, outer = 0f;
        float radius = EffectGen.FlameSplashTiles * RenderGen.TileSize;
        int innerCount = 0, outerCount = 0;
        for (int y = 0; y < splash.Height; y++)
        for (int x = 0; x < splash.Width; x++)
        {
            float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
            if (d < radius * 0.5f) { inner += splash.Pixels[y * splash.Width + x].a; innerCount++; }
            else if (d < radius) { outer += splash.Pixels[y * splash.Width + x].a; outerCount++; }
        }
        Assert.Greater(inner / innerCount, outer / outerCount);
    }

    [Test]
    public void AUnitThatKeepsFiringKeepsOneFlame()
    {
        for (int i = 0; i < 5; i++) Effects.Flame(7, 9, Vector3.zero, new Vector3(2f, 0f, 0f), 1.5f, 1f);
        Assert.AreEqual(1, Effects.ActiveFlames);
        Effects.Flame(8, 9, Vector3.zero, new Vector3(2f, 0f, 0f), 1.5f, 1f);
        Assert.AreEqual(2, Effects.ActiveFlames);
    }

    [Test]
    public void AFlameGoesOutWhenItsUnitOrTargetHasGone()
    {
        Effects.Flame(7, 9, Vector3.zero, new Vector3(2f, 0f, 0f), 1.5f, 1f);
        Effects.FollowFlames((int attacker, int target, out Vector3 nozzle, out Vector3 end) => { nozzle = end = default; return false; });
        Assert.AreEqual(0, Effects.ActiveFlames);
    }

    [Test]
    public void GunsFireAFlashAndTracerEachAndTheArtilleryOnlyItsFlash()
    {
        Effects.GunShot(UnitType.Tank, Vector2.zero, 0f, 0f, new Vector3(5f, 0f, 0f), 0.35f, true, Vector3.zero);
        Assert.AreEqual(1, Effects.ActiveTracers);
        Assert.AreEqual(1, Effects.ActiveTankBursts);
        Effects.GunShot(UnitType.Heavy, Vector2.zero, 0f, 0f, new Vector3(5f, 0f, 0f), 0.35f, true, Vector3.zero);
        Assert.AreEqual(3, Effects.ActiveTracers, "the Heavy fires both barrels");
        Effects.GunShot(UnitType.Tank, Vector2.zero, 0f, 0f, new Vector3(5f, 0f, 0f), 0.35f, false, Vector3.zero);
        Assert.AreEqual(3, Effects.ActiveTracers, "the Tracers setting hides the line");
        Assert.AreEqual(2, Effects.ActiveTankBursts, "and leaves the flash");
        Effects.GunShot(UnitType.Artillery, Vector2.zero, 0f, 0f, new Vector3(9f, 0f, 0f), 0f, false, Vector3.zero);
        Assert.AreEqual(3, Effects.ActiveTankBursts, "the Artillery flashes");
        Assert.AreEqual(0, Effects.ActiveShells, "its shell comes with its own message, to where the server says");
    }

    [Test]
    public void AShellFlightDrawsTheShellAndOnlyItsLandingLeavesACrater()
    {
        Effects.ShellFlight(Vector2.zero, new Vector2(9f, 0f), 0.6f);
        Assert.AreEqual(1, Effects.ActiveShells);
        Assert.AreEqual(0, Decals.ActiveCraters, "nothing on the ground while it flies");
        Effects.ShellLanded(new Vector2(9f, 0f));
        Assert.AreEqual(1, Decals.ActiveCraters);
    }

    [Test]
    public void CratersReuseTheOldestOnceThePoolIsFullAndFadeInAboutHalfAMinute()
    {
        for (int i = 0; i < Decals.CraterPool + 20; i++) Decals.Crater(new Vector2(i, 0f));
        Assert.AreEqual(Decals.CraterPool, Decals.ActiveCraters);
        Assert.AreEqual(Decals.CraterPool, Decals.Created, "no allocation after warm-up");
        Assert.AreEqual(0, Decals.FrameAt(0f, Decals.CraterHold, Decals.CraterFade));
        Assert.AreEqual(-1, Decals.FrameAt(30f, Decals.CraterHold, Decals.CraterFade));
    }

    [Test]
    public void ACraterIsAboutATileWideAndDithersAway()
    {
        int Opaque(EffectFrame f)
        {
            int n = 0;
            foreach (Color32 p in f.Pixels) if (p.a > 0) n++;
            return n;
        }
        for (int v = 0; v < EffectGen.CraterVariants; v++)
            Assert.Greater(Opaque(EffectGen.Crater(v, 0)), 2 * Opaque(EffectGen.Crater(v, EffectGen.DecalFrames - 1)), $"crater {v}");
    }
}

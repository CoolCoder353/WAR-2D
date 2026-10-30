using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art.Render;
using WAR2D.Audio;
using WAR2D.Client;
using WAR2D.Client.Render;
using WAR2D.Net.Replication;

/// <summary>The polish effects' rules (docs/polish.md, 2026-10-30): hit feedback, panning, fog easing, ground marks, building deaths.</summary>
public class PolishEffectsTests
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

    [Test]
    public void AHitFlashesBriefly()
    {
        Assert.IsTrue(HitFeedback.Flashing(10f, 10f));
        Assert.IsTrue(HitFeedback.Flashing(10f, 10.1f));
        Assert.IsFalse(HitFeedback.Flashing(10f, 10f + HitFeedback.FlashSeconds + 0.01f));
        Assert.IsFalse(HitFeedback.Flashing(float.NegativeInfinity, 10f), "never hit");
    }

    [Test]
    public void TheTrailHoldsThenDrainsToTheHealth()
    {
        Assert.AreEqual(0.8f, HitFeedback.Trail(0.8f, 0.5f, 0.1f, 0.016f), "holds just after the hit");
        float trail = HitFeedback.Trail(0.8f, 0.5f, HitFeedback.HoldSeconds, 0.1f);
        Assert.AreEqual(0.8f - HitFeedback.DrainPerSecond * 0.1f, trail, 1e-5f, "then drains");
        Assert.AreEqual(0.5f, HitFeedback.Trail(0.52f, 0.5f, 1f, 0.1f), "never below the health");
        Assert.AreEqual(0.9f, HitFeedback.Trail(0.5f, 0.9f, 0f, 0.016f), "follows healing at once");
    }

    [Test]
    public void SoundsPanWithTheirScreenPosition()
    {
        var view = new Rect(0, 0, 40, 20);
        Assert.AreEqual(0f, SfxVoicePool.Pan(new Vector2(20, 3), view), 1e-5f, "centre");
        Assert.AreEqual(-SfxVoicePool.MaxPan, SfxVoicePool.Pan(new Vector2(0, 10), view), 1e-5f, "left edge");
        Assert.AreEqual(SfxVoicePool.MaxPan / 2f, SfxVoicePool.Pan(new Vector2(30, 10), view), 1e-5f, "halfway right");
        Assert.AreEqual(SfxVoicePool.MaxPan, SfxVoicePool.Pan(new Vector2(90, 10), view), 1e-5f, "off screen stays at the edge's pan");
    }

    [Test]
    public void FogCellsEaseToTheirLevel()
    {
        byte a = 255;
        int frames = 0;
        while (a != 0 && frames < 1000) { a = FogView.Toward(a, 0, 1f / 60f); frames++; }
        Assert.AreEqual(0, a);
        Assert.That(frames, Is.InRange(14, 16), "a full reveal takes about FadeSeconds at 60 fps");
        Assert.AreEqual(140, FogView.Toward(0, 140, 10f), "a long frame lands on the level, never past it");
        Assert.AreEqual(1, FogView.Toward(0, 140, 0f), "every frame moves at least one step");
    }

    [Test]
    public void GroundMarksHoldThenFadeThroughTheirFrames()
    {
        Assert.AreEqual(0, Decals.FrameAt(0f, 10f, 6f));
        Assert.AreEqual(0, Decals.FrameAt(9.9f, 10f, 6f));
        Assert.AreEqual(1, Decals.FrameAt(10f, 10f, 6f));
        Assert.AreEqual(2, Decals.FrameAt(12.5f, 10f, 6f));
        Assert.AreEqual(EffectGen.DecalFrames - 1, Decals.FrameAt(15.9f, 10f, 6f));
        Assert.AreEqual(-1, Decals.FrameAt(16f, 10f, 6f), "gone");
    }

    [Test]
    public void TrackMarksPickTheNearestOfFourDirections()
    {
        Assert.AreEqual(0, Decals.TrackDirection(0f));
        Assert.AreEqual(0, Decals.TrackDirection(math.PI), "driving west lays the same mark as east");
        Assert.AreEqual(1, Decals.TrackDirection(math.PI / 4f));
        Assert.AreEqual(2, Decals.TrackDirection(-math.PI / 2f));
        Assert.AreEqual(3, Decals.TrackDirection(-math.PI / 4f));
        Assert.AreEqual(1, Decals.TrackDirection(math.radians(50f)));
    }

    [Test]
    public void ThousandsOfMarksNeverExceedThePools()
    {
        for (int i = 0; i < 2000; i++)
        {
            Decals.Scorch(new Vector2(i, 0), i % 3 == 0);
            Decals.Track(new Vector2(i, 1), i * 0.1f);
            Decals.Rubble(new Vector2(i, 2), 1 + i % 5);
        }
        Assert.AreEqual(Decals.ScorchPool + Decals.LargeScorchPool, Decals.ActiveScorches);
        Assert.AreEqual(Decals.TrackPool, Decals.ActiveTracks);
        Assert.AreEqual(Decals.ScorchPool + Decals.LargeScorchPool + Decals.TrackPool + 3 * Decals.RubblePool + Decals.LargeRubblePool, Decals.Created);
    }

    [Test]
    public void ABuildingCollapseRipplesAcrossItsFootprint()
    {
        Effects.BuildingCollapse(new Vector2(10, 10), 1);
        Assert.AreEqual(1, Effects.ActiveExplosions, "a 1-tile building dies like a unit");
        Assert.AreEqual(0, Effects.PendingBlasts);
        Effects.BuildingCollapse(new Vector2(30, 30), 5);
        Assert.AreEqual(12, Effects.PendingBlasts, "an HQ's ripple: 25 / 2 follow-up blasts");
        for (int i = 0; i < 1000; i++) Effects.BuildingCollapse(new Vector2(i, 0), 5);
        Assert.AreEqual(Effects.MaxDelayed, Effects.PendingBlasts, "capped");
    }

    [Test]
    public void DamagedBuildingEffectsUseTheirOwnPools()
    {
        for (int i = 0; i < 500; i++)
        {
            Effects.Smoke(new Vector2(i, 0));
            Effects.Fire(new Vector2(i, 1));
            Effects.GemSparkle(new Vector2(i, 2));
        }
        Assert.AreEqual(Effects.SmokePool, Effects.ActiveSmoke);
        Assert.AreEqual(Effects.FirePool, Effects.ActiveFires);
        Assert.AreEqual(Effects.SparklePool, Effects.ActiveSparkles);
    }

    private static void Enter(ClientBuildings b, int id, BuildingType type, int2 anchor, byte progress = 100) =>
        b.Enter(new BuildingData { id = id, buildingType = type, position = anchor, ownerId = 1 },
            new HealthComponent { currentHealth = 100f, maxHealth = 100f }, progress);

    [Test]
    public void ADeathOnABuildingsAnchorIsThatBuildings()
    {
        var b = new ClientBuildings();
        Enter(b, 7, BuildingType.Barracks, new int2(10, 10));
        Assert.IsTrue(b.TryTakeDying(new int2(10, 10), 0f, out ClientBuildings.Entry e));
        Assert.AreEqual(7, e.Data.id);
        Assert.IsFalse(b.TryTakeDying(new int2(11, 10), 0f, out _), "another tile of the footprint is a unit's death");

        b.Hide(7);
        Assert.IsFalse(b.TryTakeDying(new int2(10, 10), 0f, out _), "a ghost's death isn't seen");
    }

    [Test]
    public void ABuildingRemovedJustBeforeItsExplosionIsStillFoundOnce()
    {
        var b = new ClientBuildings();
        Enter(b, 9, BuildingType.HeavyFactory, new int2(4, 4));
        float now = Time.time;
        b.Remove(9);
        Assert.IsTrue(b.TryTakeDying(new int2(4, 4), now + 0.5f, out ClientBuildings.Entry e));
        Assert.AreEqual(BuildingType.HeavyFactory, e.Data.buildingType);
        Assert.IsFalse(b.TryTakeDying(new int2(4, 4), now + 0.5f, out _), "taken");

        Enter(b, 10, BuildingType.Barracks, new int2(20, 20));
        b.Remove(10);
        Assert.IsFalse(b.TryTakeDying(new int2(20, 20), now + ClientBuildings.GoneMemorySeconds + 1f, out _), "forgotten after a while");
    }

    [Test]
    public void OnlyVehiclesLeaveTracks()
    {
        Assert.IsTrue(WorldEffects.LeavesTracks((byte)UnitType.Tank));
        Assert.IsTrue(WorldEffects.LeavesTracks((byte)UnitType.Heavy));
        Assert.IsTrue(WorldEffects.LeavesTracks((byte)UnitType.Artillery));
        Assert.IsFalse(WorldEffects.LeavesTracks((byte)UnitType.Scout));
        Assert.IsFalse(WorldEffects.LeavesTracks((byte)UnitType.Builder));
    }

    [Test]
    public void AVehicleLaysAMarkEveryHalfTile()
    {
        var fx = new WorldEffects(16);
        var view = new Rect(-50, -50, 100, 100);
        fx.Moved(3, 77, (byte)UnitType.Tank, new float2(0, 0), view); // first sight: no mark
        fx.Moved(3, 77, (byte)UnitType.Tank, new float2(0.3f, 0), view);
        Assert.AreEqual(0, Decals.ActiveTracks);
        fx.Moved(3, 77, (byte)UnitType.Tank, new float2(0.6f, 0), view);
        Assert.AreEqual(1, Decals.ActiveTracks);
        fx.Moved(3, 77, (byte)UnitType.Tank, new float2(9f, 0), view);
        Assert.AreEqual(1, Decals.ActiveTracks, "a correction's jump lays nothing");
        fx.Moved(3, 78, (byte)UnitType.Tank, new float2(9.6f, 0), view);
        Assert.AreEqual(1, Decals.ActiveTracks, "a new unit on the index starts afresh");
        fx.Moved(3, 78, (byte)UnitType.Tank, new float2(80f, 0), view);
        fx.Moved(3, 78, (byte)UnitType.Tank, new float2(80.6f, 0), view);
        Assert.AreEqual(1, Decals.ActiveTracks, "off screen lays nothing");
    }

    [Test]
    public void SpotHashesAreFixedAndInRange()
    {
        for (int id = 1; id < 500; id++)
        {
            float h = WorldEffects.Hash01(id, 3);
            Assert.That(h, Is.InRange(0f, 0.99999f));
            Assert.AreEqual(h, WorldEffects.Hash01(id, 3));
        }
        Assert.AreNotEqual(WorldEffects.Hash01(5, 1), WorldEffects.Hash01(5, 2));
    }
}

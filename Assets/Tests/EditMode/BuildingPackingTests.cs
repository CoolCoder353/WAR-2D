using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Art;
using WAR2D.Client.Render;
using WAR2D.Net.Replication;

/// <summary>How the instanced building renderer turns the client's buildings into draw instances.</summary>
public class BuildingPackingTests
{
    private static int2 Size(BuildingType t) => t == BuildingType.Barracks ? new int2(2, 2) : new int2(1, 1);

    /// <summary>An atlas with a 2-frame Spawner, a 4-facing × 2-frame Miner and a 4-frame construction overlay.</summary>
    private static BuildingFrames Frames()
    {
        var buildings = new AtlasData();
        buildings.sprites.Add(new AtlasSprite { id = "Barracks", first = 0, frames = 2, directions = 1 });
        buildings.sprites.Add(new AtlasSprite { id = "Miner", first = 2, frames = 2, directions = 4 });
        for (int i = 0; i < 10; i++) buildings.frames.Add(new AtlasFrame { w = 64, h = 64 });
        var effects = new AtlasData();
        effects.sprites.Add(new AtlasSprite { id = "ConstructionOverlay", first = 3, frames = 4, directions = 1 });
        for (int i = 0; i < 7; i++) effects.frames.Add(new AtlasFrame { w = 64, h = 64 });
        return new BuildingFrames(ArtAtlas.From(buildings), ArtAtlas.From(effects));
    }

    private static ClientBuildings.Entry Entry(int id, BuildingType type, int2 anchor, float health = 10, byte progress = 100, bool ghost = false, float rotation = 0f) =>
        new ClientBuildings.Entry
        {
            Data = new BuildingData { id = id, buildingType = type, position = anchor, ownerId = 1, rotation = rotation },
            Health = new HealthComponent { entityId = id, currentHealth = health, maxHealth = 10 },
            Progress = progress,
            Ghost = ghost,
        };

    private static BuildingPacking.Lists Pack(float time, params ClientBuildings.Entry[] entries)
    {
        var lists = new BuildingPacking.Lists();
        BuildingPacking.Pack(entries, Frames(), _ => 0xFF0000FFu, Size, time, _ => false, lists);
        return lists;
    }

    [Test]
    public void FartherBuildingsDrawFirst()
    {
        BuildingPacking.Lists lists = Pack(0f, Entry(1, BuildingType.Miner, new int2(0, 2)), Entry(2, BuildingType.Miner, new int2(0, 9)), Entry(3, BuildingType.Miner, new int2(0, 5)));
        Assert.That(lists.Buildings.Count, Is.EqualTo(3));
        Assert.That(lists.Buildings[0].position.y, Is.GreaterThan(lists.Buildings[1].position.y));
        Assert.That(lists.Buildings[1].position.y, Is.GreaterThan(lists.Buildings[2].position.y));
    }

    [Test]
    public void BuildingsSitOnTheirFootprintCentre()
    {
        BuildingPacking.Lists lists = Pack(0f, Entry(1, BuildingType.Barracks, new int2(4, 4)));
        float2 centre = Footprint.VisualCenter(new int2(4, 4), new int2(2, 2));
        Assert.That(lists.Buildings[0].position, Is.EqualTo(centre));
    }

    [Test]
    public void FacingPicksTheRotatedFramesAndTimeAnimatesThem()
    {
        BuildingPacking.Lists east = Pack(0f, Entry(1, BuildingType.Miner, new int2(0, 0)));
        BuildingPacking.Lists north = Pack(0f, Entry(1, BuildingType.Miner, new int2(0, 0), rotation: 90f));
        Assert.That(north.Buildings[0].frame - east.Buildings[0].frame, Is.EqualTo(2u), "one facing is two frames on");
        var seen = new HashSet<uint>();
        for (int t = 0; t < 20; t++) seen.Add(Pack(t * 0.05f, Entry(1, BuildingType.Miner, new int2(0, 0))).Buildings[0].frame);
        Assert.That(seen.Count, Is.EqualTo(2), "the idle frames loop");
    }

    [Test]
    public void GhostsHoldTheirFirstFrameAndAreFlagged()
    {
        for (int t = 0; t < 20; t++)
        {
            UnitInstance ghost = Pack(t * 0.05f, Entry(1, BuildingType.Barracks, new int2(0, 0), ghost: true)).Buildings[0];
            Assert.That(ghost.frame, Is.EqualTo(0u));
            Assert.That(((int)ghost.pad & BuildingPacking.GhostFlag) != 0, Is.True);
        }
    }

    [Test]
    public void BlueprintsFadeInUnderTheOverlayWithAProgressBar()
    {
        BuildingPacking.Lists blueprint = Pack(0f, Entry(1, BuildingType.Barracks, new int2(0, 0), progress: 0));
        BuildingPacking.Lists half = Pack(0f, Entry(1, BuildingType.Barracks, new int2(0, 0), progress: 50));
        BuildingPacking.Lists done = Pack(0f, Entry(1, BuildingType.Barracks, new int2(0, 0)));
        uint Alpha(UnitInstance u) => u.color >> 24;
        Assert.That(Alpha(blueprint.Buildings[0]), Is.LessThan(Alpha(half.Buildings[0])));
        Assert.That(Alpha(half.Buildings[0]), Is.LessThan(Alpha(done.Buildings[0])));
        Assert.That(Alpha(done.Buildings[0]), Is.EqualTo(255u));
        Assert.That(blueprint.Overlays.Count, Is.EqualTo(1));
        Assert.That(blueprint.Overlays[0].frame, Is.InRange(3u, 6u), "a ConstructionOverlay cell");
        Assert.That(blueprint.Overlays[0].scale, Is.EqualTo(2f), "the overlay covers the footprint");
        Assert.That(blueprint.Bars.Count, Is.EqualTo(1), "the progress bar");
        Assert.That(blueprint.Bars[0].color, Is.EqualTo(BuildingPacking.ProgressColor));
        Assert.That(done.Overlays.Count + done.Bars.Count, Is.EqualTo(0));
    }

    [Test]
    public void OnlyDamagedFinishedBuildingsShowAHealthBar()
    {
        Assert.That(Pack(0f, Entry(1, BuildingType.Miner, new int2(0, 0), health: 10)).Bars.Count, Is.EqualTo(0));
        BuildingPacking.Lists hurt = Pack(0f, Entry(1, BuildingType.Miner, new int2(0, 0), health: 4));
        Assert.That(hurt.Bars.Count, Is.EqualTo(1));
        Assert.That(hurt.Bars[0].color, Is.EqualTo(InstancedUnitRenderer.BarColor(0.4f)));
        Assert.That(Pack(0f, Entry(1, BuildingType.Miner, new int2(0, 0), health: 4, ghost: true)).Bars.Count, Is.EqualTo(0), "a ghost shows no live health");
    }

    /// <summary>An atlas with a 2-frame Gun turret base (cells 0–1) and its head: 8 facings × 2 frames (cells 2–17).</summary>
    private static BuildingFrames TurretFrames()
    {
        var buildings = new AtlasData();
        buildings.sprites.Add(new AtlasSprite { id = "GunTurret", first = 0, frames = 2, directions = 1 });
        buildings.sprites.Add(new AtlasSprite { id = ArtManifest.TurretHead, first = 2, frames = 2, directions = 8 });
        for (int i = 0; i < 18; i++) buildings.frames.Add(new AtlasFrame { w = 64, h = 64 });
        return new BuildingFrames(ArtAtlas.From(buildings), ArtAtlas.From(new AtlasData()));
    }

    private static BuildingPacking.Lists PackTurret(float time, ClientBuildings.TurretHead head, params ClientBuildings.Entry[] entries)
    {
        var lists = new BuildingPacking.Lists();
        BuildingPacking.Pack(entries, TurretFrames(), _ => 0xFF0000FFu, Size, time, _ => false, lists, _ => head);
        return lists;
    }

    [Test]
    public void ATurretsHeadDrawsOverItsBaseInItsFacing()
    {
        var head = new ClientBuildings.TurretHead { Facing = 3, FiredAt = float.NegativeInfinity };
        // Two turrets side by side (equal y): each head is drawn right after its own base.
        BuildingPacking.Lists lists = PackTurret(5f, head, Entry(1, BuildingType.GunTurret, new int2(0, 0)), Entry(2, BuildingType.GunTurret, new int2(3, 0)));
        Assert.That(lists.Buildings.Count, Is.EqualTo(4));
        Assert.That(lists.Buildings[0].frame, Is.EqualTo(0u), "the base, whole");
        Assert.That(lists.Buildings[1].frame, Is.EqualTo((uint)(2 + 3 * 2)), "the head in facing 3, at rest");
        Assert.That(lists.Buildings[1].position, Is.EqualTo(lists.Buildings[0].position));
        Assert.That(lists.Buildings[3].frame, Is.EqualTo((uint)(2 + 3 * 2)));
    }

    [Test]
    public void ATurretsBaseShowsDamageByHealthAndItsHeadRecoilsAfterAShot()
    {
        var head = new ClientBuildings.TurretHead { Facing = 0, FiredAt = 5f };
        for (int t = 0; t < 20; t++)
            Assert.That(PackTurret(10f + t * 0.05f, head, Entry(1, BuildingType.GunTurret, new int2(0, 0), health: 10)).Buildings[0].frame, Is.EqualTo(0u), "not looped");
        Assert.That(PackTurret(10f, head, Entry(1, BuildingType.GunTurret, new int2(0, 0), health: 4)).Buildings[0].frame, Is.EqualTo(1u), "damaged below half");
        Assert.That(PackTurret(5.05f, head, Entry(1, BuildingType.GunTurret, new int2(0, 0))).Buildings[1].frame, Is.EqualTo(3u), "recoil just after the shot");
        Assert.That(PackTurret(6f, head, Entry(1, BuildingType.GunTurret, new int2(0, 0))).Buildings[1].frame, Is.EqualTo(2u), "back at rest");
    }

    [Test]
    public void HeadFacingsAreTheNearestOfEight()
    {
        Assert.That(ClientBuildings.FacingOf(1, 0), Is.EqualTo(0));
        Assert.That(ClientBuildings.FacingOf(1, 1), Is.EqualTo(1));
        Assert.That(ClientBuildings.FacingOf(0, 1), Is.EqualTo(2));
        Assert.That(ClientBuildings.FacingOf(-1, 0), Is.EqualTo(4));
        Assert.That(ClientBuildings.FacingOf(0, -1), Is.EqualTo(6));
        Assert.That(ClientBuildings.FacingOf(1, -0.2f), Is.EqualTo(0));
    }
}

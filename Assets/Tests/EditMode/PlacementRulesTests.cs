using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.World;

public class PlacementRulesTests
{
    // A 21x21 ground map covering world tiles -10..10; grid tile = world tile + Offset.
    private static readonly int2 Offset = new int2(10, 10);
    private MapStore map;
    private HashSet<int2> units;

    [SetUp]
    public void SetUp()
    {
        var rows = new string[21];
        for (int i = 0; i < rows.Length; i++) rows[i] = new string('.', 21);
        map = MapStore.FromAscii(rows);
        units = new HashSet<int2>();
    }

    [TearDown]
    public void TearDown() => map.Dispose();

    private void SetTile(int2 world, TileType type)
    {
        var tiles = map.Grid.Tiles;
        tiles[map.Grid.Index(world + Offset)] = (byte)type;
    }

    private PlacementResult Check(BuildingType type, int2 anchor, float rot, int size, GameState state, bool hasHQ) =>
        PlacementRules.Check(type, anchor, rot, new int2(size, size), state, hasHQ,
            p => map.Grid.TileAt(p + Offset), p => map.Grid.IsUsed(p + Offset), p => units.Contains(p));

    [Test]
    public void HQ_DuringPlacingHQ_IsOk() =>
        Assert.That(Check(BuildingType.Base, int2.zero, 0, 3, GameState.PlacingHQ, false), Is.EqualTo(PlacementResult.Ok));

    [Test]
    public void SecondHQ_IsRejected() =>
        Assert.That(Check(BuildingType.Base, int2.zero, 0, 3, GameState.PlacingHQ, true), Is.EqualTo(PlacementResult.HQAlreadyPlaced));

    [Test]
    public void HQ_DuringPlaying_IsWrongState() =>
        Assert.That(Check(BuildingType.Base, int2.zero, 0, 3, GameState.Playing, false), Is.EqualTo(PlacementResult.WrongGameState));

    [Test]
    public void OtherBuilding_DuringPlacingHQ_IsWrongState() =>
        Assert.That(Check(BuildingType.SmallUnitSpawner, int2.zero, 0, 2, GameState.PlacingHQ, true), Is.EqualTo(PlacementResult.WrongGameState));

    [Test]
    public void OtherBuilding_WithoutHQ_IsRejected() =>
        Assert.That(Check(BuildingType.SmallUnitSpawner, int2.zero, 0, 2, GameState.Playing, false), Is.EqualTo(PlacementResult.HQNotPlacedYet));

    [Test]
    public void Building_OnWall_IsBlocked()
    {
        SetTile(int2.zero, TileType.Wall);
        Assert.That(Check(BuildingType.SmallUnitSpawner, int2.zero, 0, 2, GameState.Playing, true), Is.EqualTo(PlacementResult.TileBlocked));
    }

    [Test]
    public void Building_OnUsedTile_IsBlocked()
    {
        map.SetUsed(new int2(-1, -1) + Offset, true);
        Assert.That(Check(BuildingType.SmallUnitSpawner, int2.zero, 0, 2, GameState.Playing, true), Is.EqualTo(PlacementResult.TileBlocked));
    }

    [Test]
    public void Building_OnUnit_IsBlocked()
    {
        units.Add(new int2(0, -1));
        Assert.That(Check(BuildingType.SmallUnitSpawner, int2.zero, 0, 2, GameState.Playing, true), Is.EqualTo(PlacementResult.TileBlocked));
    }

    [Test]
    public void OffMap_IsBlocked() =>
        Assert.That(Check(BuildingType.SmallUnitSpawner, new int2(50, 50), 0, 2, GameState.Playing, true), Is.EqualTo(PlacementResult.TileBlocked));

    [Test]
    public void Miner_FacingGem_IsOk()
    {
        SetTile(new int2(0, 1), TileType.Gem);
        Assert.That(Check(BuildingType.Miner, int2.zero, 90, 1, GameState.Playing, true), Is.EqualTo(PlacementResult.Ok));
    }

    [Test]
    public void Miner_NotFacingGem_IsRejected()
    {
        SetTile(new int2(0, 1), TileType.Gem);
        Assert.That(Check(BuildingType.Miner, int2.zero, 0, 1, GameState.Playing, true), Is.EqualTo(PlacementResult.MinerMustFaceGem));
    }

    [TestCase(45f)]
    [TestCase(360f)]
    [TestCase(-90f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void NonRightAngleRotation_IsRejected(float rot) =>
        Assert.That(Check(BuildingType.SmallUnitSpawner, int2.zero, rot, 2, GameState.Playing, true), Is.EqualTo(PlacementResult.InvalidRotation));

    [Test]
    public void UndefinedType_IsRejected()
    {
        Assert.That(Check(BuildingType.None, int2.zero, 0, 1, GameState.Playing, true), Is.EqualTo(PlacementResult.InvalidType));
        Assert.That(Check((BuildingType)99, int2.zero, 0, 1, GameState.Playing, true), Is.EqualTo(PlacementResult.InvalidType));
    }

    [TestCase(GameState.Lobby)]
    [TestCase(GameState.Countdown)]
    [TestCase(GameState.GameOver)]
    public void NothingCanBeBuilt_OutsidePlacingOrPlaying(GameState state)
    {
        Assert.That(Check(BuildingType.Base, int2.zero, 0, 3, state, false), Is.EqualTo(PlacementResult.WrongGameState));
        Assert.That(Check(BuildingType.SmallUnitSpawner, int2.zero, 0, 2, state, true), Is.EqualTo(PlacementResult.WrongGameState));
    }

    [Test]
    public void HQ_OutsideOwnClearing_IsRejected()
    {
        int2 site = new int2(-6, -6);
        System.Func<int2, bool> inClearing = t => PlacementRules.InClearing(t, site, 4f);
        PlacementResult Place(int2 anchor) => PlacementRules.Check(BuildingType.Base, anchor, 0, new int2(3, 3), GameState.PlacingHQ, false,
            p => map.Grid.TileAt(p + Offset), p => map.Grid.IsUsed(p + Offset), p => false, inClearing);
        Assert.That(Place(site), Is.EqualTo(PlacementResult.Ok));
        Assert.That(Place(new int2(6, 6)), Is.EqualTo(PlacementResult.OutsideHqClearing));
    }

    [Test]
    public void Spawner_IgnoresTheClearing()
    {
        Assert.That(PlacementRules.Check(BuildingType.SmallUnitSpawner, new int2(6, 6), 0, new int2(2, 2), GameState.Playing, true,
            p => map.Grid.TileAt(p + Offset), p => map.Grid.IsUsed(p + Offset), p => false, t => false), Is.EqualTo(PlacementResult.Ok));
    }
}

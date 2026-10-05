using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

public class PlacementRulesTests
{
    private Dictionary<int2, TileNode> tiles;
    private HashSet<int2> units;

    [SetUp]
    public void SetUp()
    {
        tiles = new Dictionary<int2, TileNode>();
        for (int x = -10; x <= 10; x++)
            for (int y = -10; y <= 10; y++)
                tiles[new int2(x, y)] = new TileNode { position = new int2(x, y), weight = 1, tileType = TileType.Ground };
        units = new HashSet<int2>();
    }

    private TileNode Get(int2 p) => tiles.TryGetValue(p, out var t) ? t : new TileNode { position = p, weight = 0, tileType = TileType.Wall };

    private PlacementResult Check(BuildingType type, int2 anchor, float rot, int size, GameState state, bool hasHQ) =>
        PlacementRules.Check(type, anchor, rot, new int2(size, size), state, hasHQ, Get, p => units.Contains(p));

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
        tiles[new int2(0, 0)] = new TileNode { position = int2.zero, weight = 0, tileType = TileType.Wall };
        Assert.That(Check(BuildingType.SmallUnitSpawner, int2.zero, 0, 2, GameState.Playing, true), Is.EqualTo(PlacementResult.TileBlocked));
    }

    [Test]
    public void Building_OnUsedTile_IsBlocked()
    {
        tiles[new int2(-1, -1)] = new TileNode { position = new int2(-1, -1), weight = 1, used = 1 };
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
        tiles[new int2(0, 1)] = new TileNode { position = new int2(0, 1), weight = 0, tileType = TileType.Gem };
        Assert.That(Check(BuildingType.Miner, int2.zero, 90, 1, GameState.Playing, true), Is.EqualTo(PlacementResult.Ok));
    }

    [Test]
    public void Miner_NotFacingGem_IsRejected()
    {
        tiles[new int2(0, 1)] = new TileNode { position = new int2(0, 1), weight = 0, tileType = TileType.Gem };
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
}

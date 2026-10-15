using NUnit.Framework;

/// <summary>What each tile kind does.</summary>
public class TileRulesTests
{
    [Test]
    public void FloorIsGroundAndWallBlueprint()
    {
        Assert.IsTrue(TileRules.IsFloor(TileType.Ground));
        Assert.IsTrue(TileRules.IsFloor(TileType.WallBlueprint), "a blueprint is walkable until construction starts");
        Assert.IsFalse(TileRules.IsFloor(TileType.Wall));
        Assert.IsFalse(TileRules.IsFloor(TileType.Gem));
        Assert.IsFalse(TileRules.IsFloor(TileType.Border));
        Assert.IsFalse(TileRules.IsFloor(TileType.PlayerWall));
    }

    [Test]
    public void RockPlayerWallAndBorderBlockSight()
    {
        Assert.IsTrue(TileRules.BlocksSight(TileType.Wall));
        Assert.IsTrue(TileRules.BlocksSight(TileType.PlayerWall));
        Assert.IsTrue(TileRules.BlocksSight(TileType.Border));
        Assert.IsFalse(TileRules.BlocksSight(TileType.Gem), "gems face floor; they never blocked sight on their own");
        Assert.IsFalse(TileRules.BlocksSight(TileType.WallBlueprint));
        Assert.IsFalse(TileRules.BlocksSight(TileType.Ground));
    }

    [Test]
    public void OnlyRockGemsAndPlayerWallsAreDiggable()
    {
        Assert.IsTrue(TileRules.IsDiggable(TileType.Wall));
        Assert.IsTrue(TileRules.IsDiggable(TileType.Gem));
        Assert.IsTrue(TileRules.IsDiggable(TileType.PlayerWall));
        Assert.IsFalse(TileRules.IsDiggable(TileType.Border), "the map border can never be dug");
        Assert.IsFalse(TileRules.IsDiggable(TileType.Ground));
    }

    [Test]
    public void BorderNeverChangesAndNothingBecomesBorder()
    {
        Assert.IsFalse(TileRules.MayChange(TileType.Border, TileType.Ground));
        Assert.IsFalse(TileRules.MayChange(TileType.Ground, TileType.Border));
        Assert.IsTrue(TileRules.MayChange(TileType.Wall, TileType.Ground));
        Assert.IsFalse(TileRules.MayChange((TileType)99, TileType.Ground), "unknown kinds are refused");
    }
}

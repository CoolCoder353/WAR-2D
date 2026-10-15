using NUnit.Framework;

public class ClickOrdersTests
{
    private const int Me = 1, Ally = 2, Enemy = 3;
    private static bool IsEnemy(int owner) => owner == Enemy;

    private static (OrderKind builders, OrderKind diggers, OrderKind others) On(ClickTarget target) => ClickOrders.For(target, Me, IsEnemy);

    [Test]
    public void GroundMovesEveryone()
    {
        Assert.That(On(new ClickTarget { Tile = TileType.Ground }), Is.EqualTo((OrderKind.Move, OrderKind.Move, OrderKind.Move)));
    }

    [Test]
    public void BuildersBuildOwnBlueprintsAndRepairFriendlyDamage()
    {
        Assert.That(On(new ClickTarget { HasBuilding = true, BuildingOwner = Me, Blueprint = true }).builders, Is.EqualTo(OrderKind.Build));
        Assert.That(On(new ClickTarget { HasBuilding = true, BuildingOwner = Me, Damaged = true }).builders, Is.EqualTo(OrderKind.Repair));
        Assert.That(On(new ClickTarget { HasBuilding = true, BuildingOwner = Ally, Damaged = true }).builders, Is.EqualTo(OrderKind.Repair));
        Assert.That(On(new ClickTarget { HasBuilding = true, BuildingOwner = Ally, Blueprint = true }).builders, Is.EqualTo(OrderKind.Move), "never an ally's blueprint");
        Assert.That(On(new ClickTarget { HasBuilding = true, BuildingOwner = Enemy, Damaged = true }).builders, Is.EqualTo(OrderKind.Move));
        Assert.That(On(new ClickTarget { HasBuilding = true, BuildingOwner = Me }).builders, Is.EqualTo(OrderKind.Move), "nothing to repair");
    }

    [Test]
    public void BuildersWorkOnFriendlyWalls()
    {
        Assert.That(On(new ClickTarget { Tile = TileType.WallBlueprint, WallOwner = Me }).builders, Is.EqualTo(OrderKind.Build));
        Assert.That(On(new ClickTarget { Tile = TileType.PlayerWall, WallOwner = Me, WallHealth = 40 }).builders, Is.EqualTo(OrderKind.Repair));
        Assert.That(On(new ClickTarget { Tile = TileType.PlayerWall, WallOwner = Ally, WallHealth = 40 }).builders, Is.EqualTo(OrderKind.Repair));
        Assert.That(On(new ClickTarget { Tile = TileType.PlayerWall, WallOwner = Me, WallHealth = 100 }).builders, Is.EqualTo(OrderKind.Move));
    }

    [Test]
    public void DiggersDigRockGemsAndEnemyWalls()
    {
        Assert.That(On(new ClickTarget { Tile = TileType.Wall }).diggers, Is.EqualTo(OrderKind.Dig));
        Assert.That(On(new ClickTarget { Tile = TileType.Gem }).diggers, Is.EqualTo(OrderKind.Dig));
        Assert.That(On(new ClickTarget { Tile = TileType.PlayerWall, WallOwner = Enemy, WallHealth = 100 }).diggers, Is.EqualTo(OrderKind.Dig));
        Assert.That(On(new ClickTarget { Tile = TileType.PlayerWall, WallOwner = Ally, WallHealth = 100 }).diggers, Is.EqualTo(OrderKind.Move), "allied walls are safe");
        Assert.That(On(new ClickTarget { Tile = TileType.Border }).diggers, Is.EqualTo(OrderKind.Move));
    }

    [Test]
    public void CombatUnitsAttackMoveOntoEnemyWalls()
    {
        var wall = new ClickTarget { Tile = TileType.PlayerWall, WallOwner = Enemy, WallHealth = 100 };
        Assert.That(On(wall).others, Is.EqualTo(OrderKind.AttackMove));
        Assert.That(On(wall).builders, Is.EqualTo(OrderKind.AttackMove), "a Builder with nothing to do follows the rest");
        Assert.That(On(new ClickTarget { Tile = TileType.PlayerWall, WallOwner = Ally, WallHealth = 100 }).others, Is.EqualTo(OrderKind.Move));
    }
}

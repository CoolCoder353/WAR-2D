/// <summary>What a client knows about the tile under a right-click (its own terrain and buildings only).</summary>
public struct ClickTarget
{
    public TileType Tile;
    /// <summary>The wall tile's owner (0 when none or unknown).</summary>
    public int WallOwner;
    /// <summary>The wall tile's health in percent.</summary>
    public int WallHealth;
    /// <summary>True when a seen (not ghost) building stands on the tile.</summary>
    public bool HasBuilding;
    public int BuildingOwner;
    /// <summary>The building is still a blueprint (progress under 100 %).</summary>
    public bool Blueprint;
    /// <summary>The building is below full health.</summary>
    public bool Damaged;
    /// <summary>v0.18: a seen enemy unit under the pointer (0 when none).</summary>
    public int EnemyUnit;
    /// <summary>v0.18: the seen (not ghost) building's id, for an Attack order.</summary>
    public int BuildingId;
}

/// <summary>
/// The v0.7 right-click (Figma <c>Command card – v0.7</c>): each part of the selection takes the order it can.
/// v0.18 (Figma O1): everything but workers attacks a seen enemy unit or building under the pointer.
/// Builders build an own blueprint and repair an own or friendly damaged building or wall; Diggers dig rock, a
/// gem or an enemy wall; everything else (and workers with nothing to do there) attack-moves onto an enemy wall
/// and moves otherwise. The server checks every order again. Pure.
/// </summary>
public static class ClickOrders
{
    /// <summary>The orders for the Builders, the Diggers and the rest of the selection.</summary>
    /// <param name="isEnemy">True for an owner the local player attacks.</param>
    public static (OrderKind builders, OrderKind diggers, OrderKind others) For(in ClickTarget target, int me, System.Func<int, bool> isEnemy)
    {
        bool enemyWall = target.Tile == TileType.PlayerWall && target.WallOwner != 0 && target.WallOwner != me && isEnemy(target.WallOwner);
        OrderKind others = enemyWall ? OrderKind.AttackMove : OrderKind.Move;
        OrderKind workers = others;
        if (AttackTarget(target, isEnemy) != 0) others = OrderKind.Attack;
        return (BuilderOrder(target, me, isEnemy) ?? OrderKind.Move, DiggerOrder(target, enemyWall) ?? workers, others);
    }

    /// <summary>The seen enemy unit under the pointer, else the seen enemy building, else 0. A ghost is never a target.</summary>
    public static int AttackTarget(in ClickTarget target, System.Func<int, bool> isEnemy)
    {
        if (target.EnemyUnit != 0) return target.EnemyUnit;
        return target.HasBuilding && target.BuildingId != 0 && isEnemy(target.BuildingOwner) ? target.BuildingId : 0;
    }

    private static OrderKind? BuilderOrder(in ClickTarget target, int me, System.Func<int, bool> isEnemy)
    {
        if (target.HasBuilding)
        {
            bool own = target.BuildingOwner == me;
            if (own && target.Blueprint) return OrderKind.Build;
            if ((own || !isEnemy(target.BuildingOwner)) && !target.Blueprint && target.Damaged) return OrderKind.Repair;
            return null;
        }
        if (target.Tile == TileType.WallBlueprint && target.WallOwner == me) return OrderKind.Build;
        bool friendlyWall = target.WallOwner == me || (target.WallOwner != 0 && !isEnemy(target.WallOwner));
        if (target.Tile == TileType.PlayerWall && friendlyWall && target.WallHealth < 100) return OrderKind.Repair;
        return null;
    }

    private static OrderKind? DiggerOrder(in ClickTarget target, bool enemyWall) =>
        target.Tile == TileType.Wall || TileRules.IsGem(target.Tile) || enemyWall ? OrderKind.Dig : (OrderKind?)null;
}

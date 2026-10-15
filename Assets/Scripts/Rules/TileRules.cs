/// <summary>What each <see cref="TileType"/> does. Pure and Burst-safe.</summary>
public static class TileRules
{
    /// <summary>True for tiles units may stand on (ignoring building footprints).</summary>
    public static bool IsFloor(TileType type) => type == TileType.Ground || type == TileType.WallBlueprint;

    /// <summary>True for tiles units may stand on, from the grid's byte.</summary>
    public static bool IsFloor(byte type) => IsFloor((TileType)type);

    /// <summary>True for tiles line of sight stops at (the tile itself is still seen).</summary>
    public static bool BlocksSight(TileType type) => type == TileType.Wall || type == TileType.PlayerWall || type == TileType.Border;

    /// <summary>True for tiles a Digger can remove.</summary>
    public static bool IsDiggable(TileType type) => type == TileType.Wall || type == TileType.Gem || type == TileType.PlayerWall;

    /// <summary>True when a tile of kind <paramref name="from"/> may become <paramref name="to"/>: never to or from the border, never an unknown kind.</summary>
    public static bool MayChange(TileType from, TileType to) =>
        from != to && from <= TileType.WallBlueprint && to <= TileType.WallBlueprint && from != TileType.Border && to != TileType.Border;
}

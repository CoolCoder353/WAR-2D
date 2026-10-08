using System;
using Unity.Mathematics;

public enum PlacementResult
{
    Ok,
    InvalidType,
    InvalidRotation,
    WrongGameState,
    HQAlreadyPlaced,
    HQNotPlacedYet,
    TileBlocked,
    MinerMustFaceGem
}

/// <summary>Server-authoritative building placement rules. Pure: all world access is passed in.</summary>
public static class PlacementRules
{
    public static PlacementResult Check(
        BuildingType type, int2 anchor, float rotation, int2 size, GameState state, bool hasPlacedHQ,
        Func<int2, TileType> tileAt, Func<int2, bool> isUsed, Func<int2, bool> isOccupiedByUnit)
    {
        if (type == BuildingType.None || !Enum.IsDefined(typeof(BuildingType), type)) return PlacementResult.InvalidType;
        if (!IsRightAngle(rotation)) return PlacementResult.InvalidRotation;

        if (type == BuildingType.Base)
        {
            if (state != GameState.PlacingHQ) return PlacementResult.WrongGameState;
            if (hasPlacedHQ) return PlacementResult.HQAlreadyPlaced;
        }
        else
        {
            if (state != GameState.Playing) return PlacementResult.WrongGameState;
            if (!hasPlacedHQ) return PlacementResult.HQNotPlacedYet;
        }

        foreach (int2 tile in Footprint.Tiles(anchor, size))
        {
            if (tileAt(tile) != TileType.Ground || isUsed(tile) || isOccupiedByUnit(tile)) return PlacementResult.TileBlocked;
        }

        if (type == BuildingType.Miner && tileAt(anchor + MinerRules.FacingOffset(rotation)) != TileType.Gem)
        {
            return PlacementResult.MinerMustFaceGem;
        }

        return PlacementResult.Ok;
    }

    /// <summary>True for exactly 0, 90, 180 or 270.</summary>
    public static bool IsRightAngle(float rotation)
    {
        return rotation == 0f || rotation == 90f || rotation == 180f || rotation == 270f;
    }
}

using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>Tracks which unit has claimed which tile, so units never share a tile (server only).</summary>
public sealed class TileOccupancy
{
    private readonly Dictionary<int2, int> owners = new Dictionary<int2, int>();
    private readonly Dictionary<int, HashSet<int2>> claims = new Dictionary<int, HashSet<int2>>();

    /// <summary>True if the tile is free or already claimed by <paramref name="unitId"/>. Use -1 for "anyone".</summary>
    public bool IsAvailable(int2 tile, int unitId)
    {
        return !owners.TryGetValue(tile, out int owner) || owner == unitId;
    }

    public bool TryClaim(int2 tile, int unitId)
    {
        if (!IsAvailable(tile, unitId)) return false;
        owners[tile] = unitId;
        if (!claims.TryGetValue(unitId, out HashSet<int2> set))
        {
            set = new HashSet<int2>();
            claims[unitId] = set;
        }
        set.Add(tile);
        return true;
    }

    public void Release(int2 tile, int unitId)
    {
        if (owners.TryGetValue(tile, out int owner) && owner == unitId)
        {
            owners.Remove(tile);
            claims[unitId].Remove(tile);
        }
    }

    public void ReleaseAll(int unitId)
    {
        if (!claims.TryGetValue(unitId, out HashSet<int2> set)) return;
        foreach (int2 tile in set) owners.Remove(tile);
        claims.Remove(unitId);
    }

    public int ClaimCount(int unitId) => claims.TryGetValue(unitId, out HashSet<int2> set) ? set.Count : 0;

    public void Clear()
    {
        owners.Clear();
        claims.Clear();
    }
}

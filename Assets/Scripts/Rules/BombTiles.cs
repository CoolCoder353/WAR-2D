using System;
using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>
/// Where each hidden bomb is (server): bombs do not mark their tile used (units walk over them), so placement looks here.
/// A tile can hold more than one: a bomb laid over another owner's bomb the placer doesn't know (and doesn't see) goes in
/// beside it, so neither is forgotten. Pure.
/// </summary>
public sealed class BombTiles
{
    private readonly Dictionary<int2, List<(int id, int owner)>> byTile = new Dictionary<int2, List<(int id, int owner)>>();
    private static readonly List<(int id, int owner)> None = new List<(int id, int owner)>();

    /// <summary>Records a bomb on a tile.</summary>
    public void Add(int2 tile, int id, int owner)
    {
        if (!byTile.TryGetValue(tile, out var bombs)) byTile[tile] = bombs = new List<(int id, int owner)>();
        bombs.Add((id, owner));
    }

    /// <summary>Forgets one bomb (by id) on a tile, leaving any other bomb there.</summary>
    public void Remove(int2 tile, int id)
    {
        if (!byTile.TryGetValue(tile, out var bombs)) return;
        bombs.RemoveAll(bomb => bomb.id == id);
        if (bombs.Count == 0) byTile.Remove(tile);
    }

    /// <summary>True when a bomb the viewer may know of (<see cref="BombRules.MayKnow"/>) is on the tile.</summary>
    public bool AnyKnown(int2 tile, int viewer, Func<int, int, bool> sharesVisionWith)
    {
        foreach ((int _, int owner) in At(tile)) if (BombRules.MayKnow(viewer, owner, sharesVisionWith)) return true;
        return false;
    }

    /// <summary>Adds the ids of the bombs on the tile the viewer may not know of to <paramref name="into"/>.</summary>
    public void Unknown(int2 tile, int viewer, Func<int, int, bool> sharesVisionWith, List<int> into)
    {
        foreach ((int id, int owner) in At(tile)) if (!BombRules.MayKnow(viewer, owner, sharesVisionWith)) into.Add(id);
    }

    /// <summary>How many bombs are on the tile.</summary>
    public int CountAt(int2 tile) => At(tile).Count;

    private List<(int id, int owner)> At(int2 tile) => byTile.TryGetValue(tile, out var bombs) ? bombs : None;
}

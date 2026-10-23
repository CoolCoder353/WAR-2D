using Config;
using Mirror;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Net.Replication;
using WAR2D.Sim;

/// <summary>v0.7 commands: cancel/demolish, wall drag, production changes. Every one is gated and validated.</summary>
public partial class WorldStateManager
{
    /// <summary>
    /// Cancels (blueprint: full refund) or demolishes (finished: <c>Siege.DemolishRefund</c> of its cost back)
    /// each named building the sender owns, except the HQ (plan P1), with the normal explosion. Lists longer than
    /// <see cref="SpawnerRules.MaxSpawnersPerCommand"/> are refused.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdCancelBuildings(int[] buildingIds, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdCancelBuildings)) || !TryOwnerOf(sender, out int owner)) return;
        PlayerActions.CancelBuildings(owner, buildingIds);
    }

    /// <summary>Blueprints their owner cancelled this frame, so their destruction does not count as a building lost.</summary>
    internal readonly HashSet<int> cancelledBlueprints = new HashSet<int>();

    internal readonly List<int2> wallScratch = new List<int2>();

    /// <summary>
    /// Lays a wall line from <paramref name="from"/> to <paramref name="to"/> (4-connected, at most
    /// <c>Siege.MaxWallLine</c> segments): at the next boundary, each seen, free ground tile becomes a blueprint
    /// the sender pays <c>Siege.WallCost</c> for; tiles it can't see, place or afford are skipped.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdPlaceWall(int2 from, int2 to, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdPlaceWall)) || !TryOwnerOf(sender, out int owner)) return;
        PlayerActions.PlaceWall(owner, from, to);
    }

    /// <summary>True when owner <paramref name="from"/> shares vision with owner <paramref name="to"/> (main thread, settled).</summary>
    private bool SharesVision(int from, int to) =>
        Sim != null && Sim.TrySlotOf(from, out int a) && Sim.TrySlotOf(to, out int b) && Sim.Diplomacy.SharesVisionWith(a, b);

    /// <summary>True when a bomb the viewer may know of (its own, or shared with it) sits on the tile: placement sees the tile as taken.</summary>
    internal bool KnownBombAt(int2 tile, int viewer) =>
        bombTiles.TryGetValue(tile, out var bomb) && BombRules.MayKnow(viewer, bomb.owner, SharesVision);

    /// <summary>Sets off the bomb on a tile when <paramref name="placer"/> may not know of it (plan P5); the sim checks the placer's side sees the tile.</summary>
    internal void DetonateUnknownBomb(int2 tile, int placer)
    {
        if (!bombTiles.TryGetValue(tile, out var bomb) || BombRules.MayKnow(placer, bomb.owner, SharesVision)) return;
        Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.DetonateBomb, OwnerId = placer, TargetId = bomb.id, Tile = tile });
    }

    /// <summary>What a wall tile is as a client may know it: kind, and for player walls their owner and health percent.</summary>
    private TerrainCell TerrainCellAt(int index)
    {
        SimData data = Sim.Data;
        byte type = data.Tiles[index];
        var cell = new TerrainCell { Index = index, Type = type };
        if (type != (byte)TileType.PlayerWall && type != (byte)TileType.WallBlueprint) return cell;
        byte slot = data.WallOwnerSlot[index];
        cell.OwnerId = slot < data.OwnerIdBySlot.Length ? data.OwnerIdBySlot[slot] : 0;
        cell.Health = type == (byte)TileType.WallBlueprint ? (byte)0
            : (byte)math.clamp((int)math.ceil(data.WallHealth[index] / data.WallMaxHealth * 100f), 1, 100);
        return cell;
    }
}

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
        if (!CommandGate.Allow(sender, nameof(CmdCancelBuildings))) return;
        if (buildingIds == null || buildingIds.Length > SpawnerRules.MaxSpawnersPerCommand) return;
        if (!TryActingOwner(sender, out int owner)) return;
        ServerPlayer player = GameCore.Instance.GetServerPlayerById(owner);
        if (player == null) return;
        GameConfigData config = ConfigLoader.LoadConfig();
        foreach (int id in buildingIds)
        {
            if (!Buildings.TryGetValue(id, out Entity entity) || !EntityManager.Exists(entity)) continue;
            BuildingData building = EntityManager.GetComponentData<BuildingData>(entity);
            if (building.ownerId != owner || building.buildingType == BuildingType.Base) continue;
            bool blueprint = EntityManager.HasComponent<Construction>(entity);
            HealthComponent health = EntityManager.GetComponentData<HealthComponent>(entity);
            if (health.currentHealth <= 0f) continue; // already removed this frame (or dying): never refund twice
            float refund = ConstructionRules.Refund(blueprint, config.GetBuilding(building.buildingType).UpfrontCost, config.Siege.DemolishRefund);
            if (refund > 0f) player.Refund(refund);
            health.currentHealth = 0f; // DestructionSystem removes it, frees the footprint and records the explosion
            EntityManager.SetComponentData(entity, health);
        }
    }

    private readonly List<int2> wallScratch = new List<int2>();

    /// <summary>
    /// Lays a wall line from <paramref name="from"/> to <paramref name="to"/> (4-connected, at most
    /// <c>Siege.MaxWallLine</c> segments): each free ground tile becomes a blueprint the sender pays
    /// <c>Siege.WallCost</c> for; tiles it can't place or afford are skipped.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdPlaceWall(int2 from, int2 to, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdPlaceWall))) return;
        if (!TryActingOwner(sender, out int owner) || Map == null) return;
        SiegeConfig siege = ConfigLoader.LoadConfig().Siege;
        long spanX = System.Math.Abs((long)to.x - from.x), spanY = System.Math.Abs((long)to.y - from.y);
        if (!Map.Grid.Contains(from) || !Map.Grid.Contains(to) || spanX + spanY + 1 > siege.MaxWallLine) return;
        ServerPlayer player = GameCore.Instance.GetServerPlayerById(owner);
        if (player == null) return;
        wallScratch.Clear();
        WallRules.Line(from, to, wallScratch);
        foreach (int2 tile in wallScratch)
        {
            if (!WallRules.CanPlace(tile, Map.Grid.TileAt, Map.IsUsed) || KnownBombAt(tile, owner)) continue;
            if (!player.TrySpend(siege.WallCost)) break;
            float cost = siege.WallCost;
            int2 at = tile;
            Sim.Commands.Enqueue(new SimCommand
            {
                Kind = SimCommandKind.PlaceWall, OwnerId = owner, Tile = tile,
                OnApplied = ok =>
                {
                    if (!ok) player.Refund(cost);
                    else DetonateUnknownBomb(at, owner); // a wall raised on a bomb the owner may not know sets it off
                },
            });
        }
    }

    /// <summary>True when owner <paramref name="from"/> shares vision with owner <paramref name="to"/> (main thread, settled).</summary>
    private bool SharesVision(int from, int to) =>
        Sim != null && Sim.TrySlotOf(from, out int a) && Sim.TrySlotOf(to, out int b) && Sim.Diplomacy.SharesVisionWith(a, b);

    /// <summary>True when a bomb the viewer may know of (its own, or shared with it) sits on the tile: placement sees the tile as taken.</summary>
    private bool KnownBombAt(int2 tile, int viewer) =>
        bombTiles.TryGetValue(tile, out var bomb) && BombRules.MayKnow(viewer, bomb.owner, SharesVision);

    /// <summary>Sets off the bomb on a tile when <paramref name="placer"/> may not know of it (plan P5); the sim checks the placer's side sees the tile.</summary>
    private void DetonateUnknownBomb(int2 tile, int placer)
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

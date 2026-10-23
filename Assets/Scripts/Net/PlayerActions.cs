using System.Collections.Generic;
using Config;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>
/// Everything a player does to the match, as server-side actions taking an owner id (v0.14). A client's
/// <c>[Command]</c> is gate (<see cref="CommandGate.Allow"/>) → owner (its own player object, never an argument) →
/// action; an AI calls the same action with its own owner id. Each action validates everything itself (the owner
/// may act now, ownership of what it names, value ranges), so a client and the AI pass the same checks. Each returns
/// whether it was accepted, for the AI's bookkeeping and tests; nothing here tells a client why.
/// Server only: call on the main thread (they queue <see cref="SimCommand"/>s or touch buildings, never units).
/// </summary>
public static class PlayerActions
{
    private static WorldStateManager World => WorldStateManager.Instance;

    /// <summary>True when the owner may act on the world now: Playing, and still in the match.</summary>
    public static bool MayAct(int owner) => World != null && World.MayAct(owner);

    // ---------- Buildings ----------

    /// <summary>Places the owner's HQ (PlacingHQ, inside one of its dealt start options when it has some).</summary>
    public static bool PlaceHq(int owner, int2 anchor) => PlaceBuilding(owner, anchor, BuildingType.Base, 0f);

    /// <summary>Checks, charges and queues a building (a blueprint, or the HQ) for the owner (<see cref="WorldStateManager.CheckPlacement(BuildingType, int2, float, int)"/>).</summary>
    public static bool PlaceBuilding(int owner, int2 anchor, BuildingType type, float rotation)
    {
        if (World == null || World.Map == null || World.Sim == null) return false;
        return World.PlaceBuilding(owner, anchor, type, rotation);
    }

    /// <summary>
    /// Cancels (blueprint: full refund) or demolishes (finished: <c>Siege.DemolishRefund</c> of its cost back) each named
    /// building the owner has, except the HQ, with the normal explosion. Lists longer than
    /// <see cref="SpawnerRules.MaxSpawnersPerCommand"/> are refused. True when any building was removed.
    /// </summary>
    public static bool CancelBuildings(int owner, IReadOnlyList<int> buildingIds)
    {
        if (buildingIds == null || buildingIds.Count > SpawnerRules.MaxSpawnersPerCommand || !MayAct(owner)) return false;
        WorldStateManager world = World;
        ServerPlayer player = GameCore.Instance.GetServerPlayerById(owner);
        if (player == null) return false;
        GameConfigData config = ConfigLoader.LoadConfig();
        EntityManager em = world.EntityManager;
        bool any = false;
        foreach (int id in buildingIds)
        {
            if (!world.TryGetBuilding(id, out Entity entity)) continue;
            BuildingData building = em.GetComponentData<BuildingData>(entity);
            if (building.ownerId != owner || building.buildingType == BuildingType.Base) continue;
            bool blueprint = em.HasComponent<Construction>(entity);
            HealthComponent health = em.GetComponentData<HealthComponent>(entity);
            if (health.currentHealth <= 0f) continue; // already removed this frame (or dying): never refund twice
            float refund = ConstructionRules.Refund(blueprint, config.GetBuilding(building.buildingType).UpfrontCost, config.Siege.DemolishRefund);
            if (refund > 0f) player.Refund(refund);
            if (blueprint) world.cancelledBlueprints.Add(id); // not a building lost (OnEntityDestroyed)
            health.currentHealth = 0f; // DestructionSystem removes it, frees the footprint and records the explosion
            em.SetComponentData(entity, health);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Lays a wall line from <paramref name="from"/> to <paramref name="to"/> (4-connected, at most <c>Siege.MaxWallLine</c>
    /// segments): at the next boundary each seen, free ground tile becomes a blueprint the owner pays <c>Siege.WallCost</c>
    /// for; tiles it can't see, place or afford are skipped. True when the line was queued.
    /// </summary>
    public static bool PlaceWall(int owner, int2 from, int2 to)
    {
        WorldStateManager world = World;
        if (!MayAct(owner) || world.Map == null) return false;
        SiegeConfig siege = ConfigLoader.LoadConfig().Siege;
        if (!WallRules.ValidDrag(from, to, new int2(world.Map.Grid.Width, world.Map.Grid.Height), siege.MaxWallLine)) return false;
        ServerPlayer player = GameCore.Instance.GetServerPlayerById(owner);
        if (player == null) return false;
        List<int2> line = world.wallScratch;
        line.Clear();
        WallRules.Line(from, to, line);
        float cost = siege.WallCost;
        foreach (int2 tile in line)
        {
            // Only the owner's own knowledge filters here. Whether the tile is free is the sim's call, after its
            // sight check: testing the real map first would let charges reveal what stands in the fog.
            if (world.KnownBombAt(tile, owner)) continue;
            int2 at = tile;
            bool paid = false;
            world.Sim.Commands.Enqueue(new SimCommand
            {
                Kind = SimCommandKind.PlaceWall, OwnerId = owner, Tile = tile,
                TryPay = () => paid = player.TrySpend(cost),
                OnApplied = ok =>
                {
                    if (!ok) { if (paid) player.Refund(cost); }
                    else world.DetonateUnknownBomb(at, owner); // a wall raised on a bomb the owner may not know sets it off
                },
            });
        }
        return true;
    }

    // ---------- Production ----------

    /// <summary>
    /// Sets the unit type each named, finished producer the owner has makes, and clears its queue and the progress
    /// towards its next unit. Producers that can't make the type, or already make it, are skipped. Lists longer than
    /// <see cref="SpawnerRules.MaxSpawnersPerCommand"/> and unknown types are refused. True when any producer changed.
    /// </summary>
    public static bool SetProduction(int owner, IReadOnlyList<int> buildingIds, byte unitType)
    {
        if (buildingIds == null || buildingIds.Count > SpawnerRules.MaxSpawnersPerCommand) return false;
        if (unitType == 0 || !System.Enum.IsDefined(typeof(UnitType), (UnitType)unitType)) return false;
        if (!MayAct(owner)) return false;
        WorldStateManager world = World;
        EntityManager em = world.EntityManager;
        bool any = false;
        foreach (int id in buildingIds)
        {
            if (!world.TryOwnSpawner(id, owner, out Entity building) || em.HasComponent<Construction>(building)) continue;
            BuildingType buildingType = em.GetComponentData<BuildingData>(building).buildingType;
            if (!SpawnerRules.MayProduce(buildingType, (UnitType)unitType)) continue;
            SpawnerData spawner = em.GetComponentData<SpawnerData>(building);
            if (spawner.unitType == unitType) continue; // already making it: keep the queue
            spawner.unitType = unitType;
            spawner.count = 0;
            spawner.progress = 0f;
            em.SetComponentData(building, spawner);
            world.SendSpawnerQueue(owner, id, unitType, 0);
            any = true;
        }
        return any;
    }

    /// <summary>Typed form of <see cref="SetProduction(int, IReadOnlyList{int}, byte)"/>.</summary>
    public static bool SetProduction(int owner, IReadOnlyList<int> buildingIds, UnitType type) => SetProduction(owner, buildingIds, (byte)type);

    /// <summary>
    /// Adds or removes <paramref name="delta"/> units of the selected type on each named, finished producer the owner has
    /// (only the queue buttons' steps, <see cref="SpawnerRules.QueueSteps"/>). True when any queue changed.
    /// </summary>
    public static bool ChangeQueues(int owner, IReadOnlyList<int> buildingIds, int delta)
    {
        if (buildingIds == null || buildingIds.Count > SpawnerRules.MaxSpawnersPerCommand || !SpawnerRules.IsQueueStep(delta)) return false;
        if (!MayAct(owner)) return false;
        WorldStateManager world = World;
        EntityManager em = world.EntityManager;
        bool any = false;
        foreach (int id in buildingIds)
        {
            if (!world.TryOwnSpawner(id, owner, out Entity building) || em.HasComponent<Construction>(building)) continue;
            SpawnerData spawner = em.GetComponentData<SpawnerData>(building);
            if (SpawnerRules.Apply(ref spawner.count, delta) == 0) continue;
            em.SetComponentData(building, spawner);
            world.SendSpawnerQueue(owner, id, spawner.unitType, spawner.count);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Sets (or, with <paramref name="clear"/>, removes) the rally tile of each named spawner the owner has: new units
    /// from it get a move order there. The tile must be on the map. True when any rally changed.
    /// </summary>
    public static bool SetRally(int owner, IReadOnlyList<int> buildingIds, int2 tile, bool clear)
    {
        WorldStateManager world = World;
        if (buildingIds == null || buildingIds.Count > SpawnerRules.MaxSpawnersPerCommand || world == null || world.Map == null || !world.Map.Grid.Contains(tile)) return false;
        if (!MayAct(owner)) return false;
        EntityManager em = world.EntityManager;
        bool any = false;
        foreach (int id in buildingIds)
        {
            if (!world.TryOwnSpawner(id, owner, out Entity building)) continue;
            SpawnerData spawner = em.GetComponentData<SpawnerData>(building);
            spawner.rally = tile;
            spawner.hasRally = !clear;
            em.SetComponentData(building, spawner);
            world.SendSpawnerRally(owner, id, tile, !clear);
            any = true;
        }
        return any;
    }

    // ---------- Units ----------

    /// <summary>
    /// Orders units (any <see cref="OrderKind"/>; the goal is used, and must be on the map, for orders that name a
    /// tile; <paramref name="queue"/> queues it after each unit's current orders). Ids the owner doesn't own are
    /// ignored by the simulation at the boundary. More ids than <c>Simulation/MaxUnitsPerPlayer</c> are refused.
    /// True when the order was queued.
    /// </summary>
    public static bool Order(int owner, IReadOnlyList<int> unitIds, OrderKind kind, bool queue, int2 goal)
    {
        WorldStateManager world = World;
        if (unitIds == null || unitIds.Count == 0 || unitIds.Count > ConfigLoader.LoadConfig().Simulation.MaxUnitsPerPlayer) return false;
        if (!MayAct(owner) || !world.IsOrderValid((byte)kind, goal)) return false;
        world.Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = kind, Queue = queue, OwnerId = owner, Tile = goal, Ids = ToArray(unitIds) });
        return true;
    }

    /// <summary>Every listed Suicide bomber of the owner's blows up where it stands (an <see cref="OrderKind.Detonate"/> order).</summary>
    public static bool Detonate(int owner, IReadOnlyList<int> unitIds) => Order(owner, unitIds, OrderKind.Detonate, false, int2.zero);

    /// <summary>
    /// Gives the listed Diggers a dig area over the box (clamped to the map's inside and to <c>Orders/MaxDigArea</c>
    /// tiles a side). True when it was queued.
    /// </summary>
    public static bool DigArea(int owner, IReadOnlyList<int> unitIds, bool queue, int2 from, int2 to)
    {
        WorldStateManager world = World;
        if (unitIds == null || unitIds.Count == 0 || unitIds.Count > ConfigLoader.LoadConfig().Simulation.MaxUnitsPerPlayer) return false;
        if (!MayAct(owner) || world.Map == null) return false;
        (int2 min, int2 max) = world.MapBounds;
        if (!CommandValidator.TryDigArea(from, to, min, max, ConfigLoader.LoadConfig().Orders.MaxDigArea, out int2 lo, out int2 hi)) return false;
        world.Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.DigArea, Queue = queue, OwnerId = owner, BoxMin = lo, BoxMax = hi, Ids = ToArray(unitIds) });
        return true;
    }

    /// <summary>Replaces one of the owner's squads (0–9) with the listed units (an empty list empties it).</summary>
    public static bool AssignSquad(int owner, int squad, IReadOnlyList<int> unitIds)
    {
        if (!CommandValidator.IsSquadIndexValid(squad) || unitIds == null || unitIds.Count > ConfigLoader.LoadConfig().Simulation.MaxUnitsPerPlayer || !MayAct(owner)) return false;
        World.Squads.Assign(owner, squad, unitIds);
        World.MarkSquadCountsDirty();
        return true;
    }

    /// <summary>Orders one of the owner's squads' living members, as <see cref="Order"/>. False for an empty squad.</summary>
    public static bool OrderSquad(int owner, int squad, OrderKind kind, bool queue, int2 goal)
    {
        WorldStateManager world = World;
        if (!CommandValidator.IsSquadIndexValid(squad) || !MayAct(owner) || !world.IsOrderValid((byte)kind, goal)) return false;
        if (world.Squads.Prune(owner, squad, world.Ids) > 0) world.MarkSquadCountsDirty(); // the HUD's count went stale
        IReadOnlyList<int> members = world.Squads.Members(owner, squad);
        if (members.Count == 0) return false;
        world.Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = kind, Queue = queue, OwnerId = owner, Tile = goal, Ids = ToArray(members) });
        return true;
    }

    // ---------- Diplomacy and the match ----------

    /// <summary>Starts or stops attacking another player (match Diplomacy on, Playing, both live, the cooldown).</summary>
    public static bool SetAttack(int owner, int target, bool on) =>
        GameCore.Instance != null && GameCore.Instance.TryChangeDiplomacy(owner, target, on, SimCommandKind.SetAttack);

    /// <summary>Starts or stops sharing vision with another player (as <see cref="SetAttack"/>).</summary>
    public static bool SetShareVision(int owner, int target, bool on) =>
        GameCore.Instance != null && GameCore.Instance.TryChangeDiplomacy(owner, target, on, SimCommandKind.SetShareVision);

    /// <summary>Gives resources to any other live player (Playing, at most what the owner has, one gift per cooldown).</summary>
    public static bool Gift(int owner, int target, float amount) => GameCore.Instance != null && GameCore.Instance.TryGift(owner, target, amount);

    /// <summary>Leaves the match as if the owner's HQ fell: everything it owns is destroyed and it is out.</summary>
    public static bool Surrender(int owner) => GameCore.Instance != null && GameCore.Instance.TrySurrender(owner);

    private static int[] ToArray(IReadOnlyList<int> ids)
    {
        var array = new int[ids.Count];
        for (int i = 0; i < array.Length; i++) array[i] = ids[i];
        return array;
    }
}

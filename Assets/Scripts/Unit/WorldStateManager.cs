using System.Collections.Generic;
using System.Linq;
using Mirror;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Tilemaps;
using Config;
using WAR2D.World;

/// <summary>
/// Manages the state of the game world, including the tilemap, units, and buildings.
/// Handles server-side logic for movement, building placement, and visibility.
/// </summary>
[BurstCompile]
public class WorldStateManager : NetworkBehaviour
{
    /// <summary>
    /// Singleton instance of the WorldStateManager.
    /// </summary>
    public static WorldStateManager Instance { get; private set; }

    /// <summary>The match's tile grid. Built on the server at start, and regenerated on clients for generated maps.</summary>
    public MapStore Map { get; private set; }

    /// <summary>Tiles per side of a generated map; 0 when the scene's tilemaps are the map.</summary>
    [SyncVar] public int MapSize;

    /// <summary>Seed of the generated map.</summary>
    [SyncVar] public uint MapSeed;

    /// <summary>The server's <see cref="MapStore.Hash"/>, which clients verify their regenerated copy against.</summary>
    [SyncVar] public ulong MapHash;

    /// <summary>Inclusive tile bounds of the map, used for every box clamp. Grid tiles are world tiles.</summary>
    public (int2 min, int2 max) MapBounds => Map == null
        ? (int2.zero, int2.zero)
        : (int2.zero, new int2(Map.Grid.Width - 1, Map.Grid.Height - 1));

    private EntityManager EntityManager => World.DefaultGameObjectInjectionWorld.EntityManager;

    [Header("Tilemaps")]
    /// <summary>The tilemap defining walkable areas.</summary>
    public Tilemap WalkableTilemap;
    /// <summary>The tilemap defining unwalkable areas (walls, gems).</summary>
    public Tilemap UnwalkableTilemap;

    /// <summary>
    /// Tracks the view area for each client player.
    /// Key: ClientPlayer, Value: (StartCorner, EndCorner) of the view box.
    /// </summary>
    private Dictionary<ClientPlayer, (int2, int2)> playerView = new Dictionary<ClientPlayer, (int2, int2)>();

    /// <summary>Server-side unit tile claims.</summary>
    public TileOccupancy Occupancy { get; } = new TileOccupancy();

    /// <summary>Server-side id source for units and buildings in this match.</summary>
    public NetIdAllocator Ids { get; } = new NetIdAllocator();

    /// <summary>
    /// Dictionary of all units in the game. Key: Unit ID, Value: Entity.
    /// </summary>
    private Dictionary<int, Entity> Units = new Dictionary<int, Entity>();

    /// <summary>
    /// Dictionary of all buildings in the game. Key: Building ID, Value: Entity.
    /// </summary>
    private Dictionary<int, Entity> Buildings = new Dictionary<int, Entity>();

    /// <summary>Footprint tiles each building marked used, so they can be freed when it dies.</summary>
    private readonly Dictionary<int, List<int2>> buildingFootprints = new Dictionary<int, List<int2>>();

    private EntityManager entityManager;

    #region Lifecycle Methods

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(this);
        }
    }

    [Server]
    public override void OnStartServer()
    {
        entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

        MapConfig m = ConfigLoader.LoadConfig().Match.Map;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (m.Size > 0)
        {
            uint seed = m.Seed != 0 ? m.Seed : (uint)UnityEngine.Random.Range(1, int.MaxValue);
            Map = MapStore.Generate(m.Size, seed, m.GemChance);
            MapSize = m.Size;
            MapSeed = seed;
            MapHash = Map.Hash();
        }
        else
        {
            Map = MapStore.FromTilemaps(WalkableTilemap, UnwalkableTilemap);
        }
        Debug.Log($"[Map] {Map.Grid.Width}x{Map.Grid.Height} map ready in {timer.ElapsedMilliseconds} ms (seed {MapSeed})");
    }

    /// <summary>
    /// Regenerates a generated map from its seed, checks it against the server's hash, and draws it.
    /// A mismatching map would desync every placement check, so the client disconnects.
    /// </summary>
    [Client]
    public override void OnStartClient()
    {
        if (MapSize <= 0) return;
        if (!isServer)
        {
            Map = MapStore.Generate(MapSize, MapSeed, ConfigLoader.LoadConfig().Match.Map.GemChance);
            if (Map.Hash() != MapHash)
            {
                Debug.LogError($"[Map] hash mismatch: local {Map.Hash():X16}, server {MapHash:X16}. Disconnecting.");
                NetworkClient.Disconnect();
                return;
            }
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (WalkableTilemap != null) WalkableTilemap.GetComponent<TilemapRenderer>().enabled = false;
        if (UnwalkableTilemap != null) UnwalkableTilemap.GetComponent<TilemapRenderer>().enabled = false;
        MapView.Show(Map.Grid);
        Debug.Log($"[Map] baked the {MapSize}² map texture in {timer.ElapsedMilliseconds} ms");
    }

    private void OnDestroy()
    {
        Map?.Dispose();
        Map = null;
        if (Instance == this) Instance = null;
    }

    [ServerCallback]
    public void FixedUpdate()
    {
        UpdatePlayerViews();
        FlushDeathEvents();
    }


    private readonly List<float2> pendingDeathPositions = new List<float2>();

    private const int MaxExplosionsPerMessage = 256;

    /// <summary>
    /// Sends the deaths recorded since the last tick to each client whose view contains them.
    /// </summary>
    [Server]
    private void FlushDeathEvents()
    {
        List<float2> deaths = TakeDeathPositions();
        if (deaths.Count == 0) return;

        foreach (KeyValuePair<ClientPlayer, (int2, int2)> view in playerView)
        {
            if (view.Key == null || view.Key.connectionToClient == null) continue;
            List<Vector2> visible = VisibilityRules.Filter(deaths, view.Value.Item1, view.Value.Item2);
            for (int i = 0; i < visible.Count; i += MaxExplosionsPerMessage)
            {
                int n = Mathf.Min(MaxExplosionsPerMessage, visible.Count - i);
                view.Key.TargetPlayExplosions(view.Key.connectionToClient, visible.GetRange(i, n).ToArray());
            }
        }
    }

    /// <summary>Called by DestructionSystem just before an entity is destroyed.</summary>
    [Server]
    public void OnEntityDestroyed(Entity entity)
    {
        if (!EntityManager.Exists(entity)) return;
        if (EntityManager.HasComponent<LocalTransform>(entity))
        {
            pendingDeathPositions.Add(EntityManager.GetComponentData<LocalTransform>(entity).Position.xy);
        }

        if (EntityManager.HasComponent<ClientUnit>(entity))
        {
            int id = EntityManager.GetComponentData<ClientUnit>(entity).id;
            Units.Remove(id);
            Occupancy.ReleaseAll(id);
        }
        else if (EntityManager.HasComponent<BuildingData>(entity))
        {
            int id = EntityManager.GetComponentData<BuildingData>(entity).id;
            Buildings.Remove(id);
            if (buildingFootprints.Remove(id, out List<int2> footprint))
            {
                foreach (int2 tile in footprint) Map.SetUsed(tile, false);
            }
        }
    }

    /// <summary>Sets every entity owned by the player to 0 health; DestructionSystem removes them (with explosions).</summary>
    [Server]
    public void KillAllEntitiesOwnedBy(int ownerId)
    {
        foreach (Entity e in Units.Values) Kill(e, ownerId);
        foreach (Entity e in Buildings.Values) Kill(e, ownerId);
    }

    private void Kill(Entity entity, int ownerId)
    {
        if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<HealthComponent>(entity)) return;
        int owner = EntityManager.HasComponent<ClientUnit>(entity)
            ? EntityManager.GetComponentData<ClientUnit>(entity).ownerId
            : EntityManager.GetComponentData<BuildingData>(entity).ownerId;
        if (owner != ownerId) return;
        HealthComponent hp = EntityManager.GetComponentData<HealthComponent>(entity);
        hp.currentHealth = 0f;
        EntityManager.SetComponentData(entity, hp);
    }

    /// <summary>Returns and clears the death positions recorded since the last call.</summary>
    [Server]
    public List<float2> TakeDeathPositions()
    {
        var copy = new List<float2>(pendingDeathPositions);
        pendingDeathPositions.Clear();
        return copy;
    }


    [Server]
    public void DestroyAllEntities()
    {
        // Each entity records its position as it goes, so the match-end wipe still reaches
        // clients as explosions instead of dying silently. pendingDeathPositions is deliberately
        // NOT cleared here: FlushDeathEvents drains it via TakeDeathPositions next FixedUpdate.
        foreach (Entity e in Buildings.Values) DestroyWithDeathPosition(e);
        foreach (Entity e in Units.Values) DestroyWithDeathPosition(e);
        foreach (List<int2> footprint in buildingFootprints.Values)
        {
            foreach (int2 tile in footprint) Map?.SetUsed(tile, false);
        }
        Units.Clear();
        Buildings.Clear();
        buildingFootprints.Clear();
        Occupancy.Clear();
    }

    /// <summary>Records the entity's death position (when it has one), then destroys it.</summary>
    private void DestroyWithDeathPosition(Entity entity)
    {
        if (!EntityManager.Exists(entity)) return;
        if (EntityManager.HasComponent<LocalTransform>(entity))
        {
            pendingDeathPositions.Add(EntityManager.GetComponentData<LocalTransform>(entity).Position.xy);
        }
        EntityManager.DestroyEntity(entity);
    }
    #endregion

    #region Unit Management

    /// <summary>
    /// Updates the view area for a client.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void UpdateClientView(int2 startcorner, int2 endcorner, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(UpdateClientView)) || !CommandValidator.IsBoxValid(startcorner, endcorner)) return;

        (int2 mapMin, int2 mapMax) = MapBounds;
        startcorner = math.clamp(startcorner, mapMin, mapMax);
        endcorner = math.clamp(endcorner, mapMin, mapMax);

        playerView[sender.identity.GetComponent<ClientPlayer>()] = (startcorner, endcorner);
    }

    /// <summary>Drops a leaving player's view box so their entities stop being synced.</summary>
    [Server]
    public void RemovePlayerView(ClientPlayer player)
    {
        if (player != null) playerView.Remove(player);
    }

    /// <summary>
    /// Updates which units and buildings are visible to each player based on their view area.
    /// </summary>
    [Server, BurstCompile]
    public void UpdatePlayerViews()
    {
        foreach (ClientPlayer dead in playerView.Keys.Where(p => p == null).ToList()) playerView.Remove(dead);

        foreach (KeyValuePair<ClientPlayer, (int2, int2)> player in playerView)
        {
            int2 startcorner = player.Value.Item1;
            int2 endcorner = player.Value.Item2;

            NativeList<Entity> entitiesInBox = FindEntitiesInBox(startcorner, endcorner);

            List<int> clientUnits = new List<int>();
            List<int> clientBuildings = new List<int>();

            foreach (Entity entity in entitiesInBox)
            {
                // Handle Buildings
                if (!EntityManager.HasComponent<ClientUnit>(entity))
                {
                    if (EntityManager.HasComponent<BuildingData>(entity))
                    {
                        BuildingData buildingData = EntityManager.GetComponentData<BuildingData>(entity);
                        buildingData.position = EntityManager.GetComponentData<LocalTransform>(entity).Position.xy;

                        HealthComponent health = EntityManager.GetComponentData<HealthComponent>(entity);
                        health.entityId = buildingData.id;

                        buildingData.rotation = MinerRules.ZDegrees(EntityManager.GetComponentData<LocalTransform>(entity).Rotation);

                        clientBuildings.Add(buildingData.id);

                        if (player.Key.visuableBuildings.Any(b => b.id == buildingData.id))
                        {
                            player.Key.visuableBuildings[player.Key.visuableBuildings.FindIndex(b => b.id == buildingData.id)] = buildingData;
                        }
                        else
                        {
                            player.Key.visuableBuildings.Add(buildingData);
                        }
                        ////Debug.Log($"Player {player.Key.nickname} can see building {buildingData.id}. Health: {health.currentHealth}/{health.maxHealth}");
                        // Update HealthComponent list
                        if (player.Key.entityHealth.Any(h => h.entityId == health.entityId))
                        {
                            player.Key.entityHealth[player.Key.entityHealth.FindIndex(h => h.entityId == health.entityId)] = health;
                        }
                        else
                        {
                            player.Key.entityHealth.Add(health);
                        }
                    }
                    continue;
                }

                // Handle Units
                ClientUnit clientUnit = EntityManager.GetComponentData<ClientUnit>(entity);
                clientUnit.position = EntityManager.GetComponentData<LocalTransform>(entity).Position.xy;
                HealthComponent unitHealth = EntityManager.GetComponentData<HealthComponent>(entity);
                unitHealth.entityId = clientUnit.id;

                clientUnits.Add(clientUnit.id);

                if (player.Key.visuableUnits.Any(u => u.id == clientUnit.id))
                {
                    player.Key.visuableUnits[player.Key.visuableUnits.FindIndex(u => u.id == clientUnit.id)] = clientUnit;
                }
                else
                {
                    player.Key.visuableUnits.Add(clientUnit);
                }

                ////Debug.Log($"Player {player.Key.nickname} can see unit {clientUnit.id}. Health: {unitHealth.currentHealth}/{unitHealth.maxHealth}");
                if (player.Key.entityHealth.Any(h => h.entityId == unitHealth.entityId))
                {
                    player.Key.entityHealth[player.Key.entityHealth.FindIndex(h => h.entityId == unitHealth.entityId)] = unitHealth;
                }
                else
                {
                    player.Key.entityHealth.Add(unitHealth);
                }
            }
            entitiesInBox.Dispose();

            // Cleanup invisible units
            for (int i = player.Key.visuableUnits.Count - 1; i >= 0; i--)
            {
                if (!clientUnits.Contains(player.Key.visuableUnits[i].id))
                {
                    player.Key.visuableUnits.RemoveAt(i);
                }
            }

            // Cleanup invisible buildings
            for (int i = player.Key.visuableBuildings.Count - 1; i >= 0; i--)
            {
                if (!clientBuildings.Contains(player.Key.visuableBuildings[i].id))
                {
                    player.Key.visuableBuildings.RemoveAt(i);
                }
            }

            //Cleanup invisible health components
            for (int i = player.Key.entityHealth.Count - 1; i >= 0; i--)
            {
                if (!clientUnits.Contains(player.Key.entityHealth[i].entityId) &&
                    !clientBuildings.Contains(player.Key.entityHealth[i].entityId))
                {
                    player.Key.entityHealth.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Registers a unit entity.
    /// </summary>
    [Server]
    public void AddUnit(Entity entity, int id)
    {
        Units.Add(id, entity);
    }

    /// <summary>
    /// Registers a building entity.
    /// </summary>
    [Server]
    public void AddBuilding(Entity entity, int id)
    {
        Buildings.Add(id, entity);
    }

    /// <summary>
    /// Checks if a position is available for a unit.
    /// </summary>
    [Server]
    public bool IsAvaliable(int2 position, int id)
    {
        return Occupancy.IsAvailable(position, id);
    }

    /// <summary>
    /// Claims a position for a unit.
    /// </summary>
    [Server]
    public bool ClaimLocation(int2 position, int id)
    {
        return Occupancy.TryClaim(position, id);
    }

    /// <summary>
    /// Releases a position claimed by a unit.
    /// </summary>
    [Server]
    public void ReleaseLocation(int2 position, int id)
    {
        Occupancy.Release(position, id);
    }

    /// <summary>
    /// Releases all locations claimed by a unit.
    /// </summary>
    [Server]
    public void ReleaseAllLocations(int id)
    {
        Occupancy.ReleaseAll(id);
    }

    /// <summary>
    /// Commands units to move to a goal.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdMoveUnits(int2 goal, int2 startcorner, int2 endcorner, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdMoveUnits)) || !CommandValidator.IsBoxValid(startcorner, endcorner)) return;
        if (GameCore.Instance.CurrentState != GameState.Playing) return;
        ServerPlayer acting = GameCore.Instance?.GetServerPlayerById(BuildingData.UIntToInt(sender.identity.netId));
        if (acting == null || acting.state != PlayerState.Playing) return;

        (int2 mapMin, int2 mapMax) = MapBounds;
        startcorner = math.clamp(startcorner, mapMin, mapMax);
        endcorner = math.clamp(endcorner, mapMin, mapMax);

        List<ClientUnit> units = new List<ClientUnit>();
        List<int2> setGoals = new List<int2>();

        NativeList<Entity> entitiesInBox = FindEntitiesInBox(startcorner, endcorner);
        foreach (Entity entity in entitiesInBox)
        {
            if (!EntityManager.HasComponent<ClientUnit>(entity))
            {
                continue;
            }
            ClientUnit clientUnit = EntityManager.GetComponentData<ClientUnit>(entity);
            clientUnit.position = EntityManager.GetComponentData<LocalTransform>(entity).Position.xy;

            if (clientUnit.ownerId != BuildingData.UIntToInt(sender.identity.netId))
            {
                continue;
            }

            units.Add(clientUnit);
        }

        foreach (ClientUnit unit in units)
        {
            if (Units.TryGetValue(unit.id, out Entity entity))
            {
                int2 specificgoal = FindBestGoalLocation(goal, unit.id, setGoals);
                setGoals.Add(specificgoal);

                //Make sure the unit doesnt own any location
                ReleaseAllLocations(unit.id);

                MoveUnit(entity, specificgoal);
            }
            else
            {
                Debug.LogWarning($"Unit with id {unit.id} not found when trying to move units.");
            }
        }
        entitiesInBox.Dispose();
    }

    private const int MaxGoalSearchTiles = 4096;

    /// <summary>
    /// Finds the best available goal location near the target, avoiding collisions.
    /// Uses a bounded BFS to find the nearest valid tile.
    /// </summary>
    [Server]
    private int2 FindBestGoalLocation(int2 goal, int id, List<int2> setGoals)
    {
        bool found = TileSearch.FindNearest(goal,
            t => !setGoals.Contains(t) && IsFreeGround(t) && Occupancy.IsAvailable(t, id),
            t => Map.Grid.TileAt(t) == TileType.Ground,
            MaxGoalSearchTiles, out int2 best);
        return found ? best : goal;
    }

    private bool IsFreeGround(int2 tile) => Map.Grid.IsWalkable(tile);

    /// <summary>Nearest free, unclaimed ground tile to <paramref name="origin"/> (e.g. outside a spawner).</summary>
    [Server]
    public bool TryFindFreeTileNear(int2 origin, int unitId, out int2 tile)
    {
        return TileSearch.FindNearest(origin,
            t => IsFreeGround(t) && Occupancy.IsAvailable(t, unitId),
            t => Map.Grid.TileAt(t) == TileType.Ground,
            MaxGoalSearchTiles, out tile);
    }

    /// <summary>
    /// Finds all entities within a specified rectangular area.
    /// </summary>
    [Server]
    private NativeList<Entity> FindEntitiesInBox(int2 startcorner, int2 endcorner)
    {
        return FindEntitiesInBoxJobMethod(startcorner, endcorner);
    }

    /// <summary>
    /// Moves a unit entity to a goal position using pathfinding.
    /// </summary>
    [Server]
    private void MoveUnit(Entity entity, int2 goal)
    {
        LocalTransform localTransform = EntityManager.GetComponentData<LocalTransform>(entity);
        int2 startInt = (int2)math.round(localTransform.Position.xy);

        if (startInt.Equals(goal))
        {
            Debug.LogWarning($"Unit at {startInt} is already at goal {goal}");
            return;
        }

        List<int2> path = Pathfinding.FindPath(Map.Grid, startInt, goal);
        if (path.Count == 0)
        {
            Debug.LogWarning($"No path found for unit at {startInt} to {goal}");
            return;
        }
        DynamicBuffer<PathPoint> pathBuffer = EntityManager.GetBuffer<PathPoint>(entity);
        pathBuffer.Clear();
        foreach (int2 node in path) pathBuffer.Add(new PathPoint { position = node });
    }

    /// <summary>
    /// Job to filter entities within a bounding box.
    /// </summary>
    [BurstCompile]
    public struct FindEntitiesInBoxJob : IJob
    {
        [Unity.Collections.ReadOnly] public NativeArray<Entity> entities;
        [Unity.Collections.ReadOnly] public NativeArray<LocalTransform> transforms;
        public int2 startcorner;
        public int2 endcorner;
        public NativeList<Entity> result;

        public void Execute()
        {
            int2 minCorner = math.min(startcorner, endcorner);
            int2 maxCorner = math.max(startcorner, endcorner);

            for (int i = 0; i < entities.Length; i++)
            {
                if (transforms[i].Position.x >= minCorner.x && transforms[i].Position.x <= maxCorner.x &&
                    transforms[i].Position.y >= minCorner.y && transforms[i].Position.y <= maxCorner.y)
                {
                    result.Add(entities[i]);
                }
            }
        }
    }

    /// <summary>
    /// Executes the FindEntitiesInBoxJob.
    /// </summary>
    [Server]
    public NativeList<Entity> FindEntitiesInBoxJobMethod(int2 startcorner, int2 endcorner)
    {
        NativeList<Entity> entitiesInBox = new(Allocator.TempJob);

        //UNITS
        var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MovementComponent>(), ComponentType.ReadOnly<LocalTransform>());
        NativeArray<Entity> entities = query.ToEntityArray(Allocator.TempJob);
        NativeArray<LocalTransform> transforms = query.ToComponentDataArray<LocalTransform>(Allocator.TempJob);

        //BUILDINGS
        var buildingQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BuildingData>(), ComponentType.ReadOnly<LocalTransform>());
        NativeArray<Entity> buildingEntities = buildingQuery.ToEntityArray(Allocator.TempJob);
        NativeArray<LocalTransform> buildingTransforms = buildingQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob);


        //EVERYTHING
        NativeArray<Entity> allEntities = new NativeArray<Entity>(entities.Length + buildingEntities.Length, Allocator.TempJob);
        NativeArray<LocalTransform> allTransforms = new NativeArray<LocalTransform>(transforms.Length + buildingTransforms.Length, Allocator.TempJob);

        allEntities.Slice(0, entities.Length).CopyFrom(entities);
        allEntities.Slice(entities.Length, buildingEntities.Length).CopyFrom(buildingEntities);

        allTransforms.Slice(0, transforms.Length).CopyFrom(transforms);
        allTransforms.Slice(transforms.Length, buildingTransforms.Length).CopyFrom(buildingTransforms);

        FindEntitiesInBoxJob job = new()
        {
            entities = allEntities,
            transforms = allTransforms,
            startcorner = startcorner,
            endcorner = endcorner,
            result = entitiesInBox
        };

        JobHandle handle = job.Schedule();
        handle.Complete();

        entities.Dispose();
        transforms.Dispose();

        buildingEntities.Dispose();
        buildingTransforms.Dispose();

        allEntities.Dispose();
        allTransforms.Dispose();

        // Ensure the entitiesInBox is disposed of properly
        NativeList<Entity> result = new NativeList<Entity>(entitiesInBox.Length, Allocator.Persistent);
        result.AddRange(entitiesInBox.AsArray());
        entitiesInBox.Dispose();

        return result;
    }

    #endregion

    #region Building Management

    /// <summary>Server-side placement check for a player (used by commands and tests).</summary>
    [Server]
    public PlacementResult CheckPlacement(BuildingType type, int2 anchor, float rotation, ClientPlayer player)
    {
        if (type == BuildingType.None || !System.Enum.IsDefined(typeof(BuildingType), type)) return PlacementResult.InvalidType;

        ServerPlayer acting = GameCore.Instance?.GetServerPlayerById(BuildingData.UIntToInt(player.netId));
        if (acting == null || acting.state != PlayerState.Playing) return PlacementResult.WrongGameState;

        return PlacementRules.Check(type, anchor, rotation, GetBuildingSize(type), GameCore.Instance.CurrentState,
            player.hasPlacedHQ, Map.Grid.TileAt, Map.Grid.IsUsed, tile => !IsAvaliable(tile, -1));
    }

    /// <summary>Test helper: first anchor (scanning the map) where the player may place this building.</summary>
    internal bool TryFindBuildableAnchor(BuildingType type, float rotation, ClientPlayer player, out int2 anchor)
    {
        MapGrid grid = Map.Grid;
        for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
        {
            int2 tile = new int2(x, y);
            if (grid.TileAt(tile) == TileType.Ground && CheckPlacement(type, tile, rotation, player) == PlacementResult.Ok)
            {
                anchor = tile;
                return true;
            }
        }
        anchor = default;
        return false;
    }

    /// <summary>
    /// Attempts to add a building at the specified position.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void TryAddBuilding(int2 positon, BuildingType type, float rotation, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(TryAddBuilding))) return;
        ClientPlayer placer = sender.identity.GetComponent<ClientPlayer>();
        if (CheckPlacement(type, positon, rotation, placer) != PlacementResult.Ok)
        {
            return;
        }

        BuildingConfig buildingConfig = ConfigLoader.LoadConfig().GetBuilding(type);

        // Check the owner can afford the building before it is placed.
        ServerPlayer owner = GameCore.Instance?.GetServerPlayerById(BuildingData.UIntToInt(sender.identity.GetComponent<ClientPlayer>().netId));
        if (owner == null || !owner.TrySpend(buildingConfig.UpfrontCost))
        {
            return;
        }

        BuildingData buildingData = new BuildingData
        {
            position = new float2(positon.x, positon.y),
            id = Ids.Allocate(),
            buildingType = type,
            ownerId = BuildingData.UIntToInt(sender.identity.GetComponent<ClientPlayer>().netId),
            rotation = rotation
        };


        Debug.Log($"Placing building of type {type} at position {positon} with rotation {rotation}, owned by player {buildingData.ownerId}");

        Entity building = EntityManager.CreateEntity();
        EntityManager.AddComponentData<BuildingData>(building, buildingData);

        EntityManager.AddComponentData<LocalTransform>(building, new LocalTransform
        {
            Position = new float3(buildingData.position.x, buildingData.position.y, 0),
            Rotation = quaternion.Euler(0, 0, math.radians(rotation)),
            Scale = 1f
        });

        // Every building pays its running cost; unpaid buildings decay.
        EntityManager.AddComponentData(building, new UpkeepComponent
        {
            ownerId = buildingData.ownerId,
            runningCostPerSecond = buildingConfig.RunningCost,
        });

        // Add Health Component
        EntityManager.AddComponentData(building, new HealthComponent
        {
            entityId = buildingData.id,
            currentHealth = buildingConfig.Health,
            maxHealth = buildingConfig.Health
        });

        switch (type)
        {
            case BuildingType.Base:
                EntityManager.AddComponentData(building, new HQComponent { ownerId = buildingData.ownerId });
                sender.identity.GetComponent<ClientPlayer>().hasPlacedHQ = true;
                // Notify GameCore to check if all players have placed HQ
                GameCore.Instance.CheckHQPlacementProgress();
                break;
            case BuildingType.Miner:
                // Add mining component to miner buildings
                EntityManager.AddComponentData(building, new MiningComponent
                {
                    timeSinceLastMining = 0f,
                    isActive = false
                });
                break;
            case BuildingType.SmallUnitSpawner:
                EntityManager.AddComponentData(building,
                new SpawnerData
                {
                    count = 0,
                    ownerId = buildingData.ownerId,
                    position = buildingData.position,
                    unitType = UnitType.Tank,
                    spawnRate = buildingConfig.SpawnRate
                });
                break;
            default:
                Debug.LogError("BuildingType not found in WorldStateManager");
                break;
        }

        //Set the tiles the building will cover to be used
        List<int2> tiles = Footprint.Tiles(positon, GetBuildingSize(type));
        foreach (int2 tile in tiles) Map.SetUsed(tile, true);
        buildingFootprints[buildingData.id] = tiles;

        AddBuilding(building, buildingData.id);
    }

    /// <summary>
    /// Command to check if a building can be built (for client prediction/UI).
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CanBuildBuildingCommand(int2 position, BuildingType type, float rotation, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CanBuildBuildingCommand))) return;
        ClientPlayer player = sender.identity.GetComponent<ClientPlayer>();
        player.TargetReceiveCanBuildBuildingResponse(sender, CheckPlacement(type, position, rotation, player) == PlacementResult.Ok);
    }

    /// <summary>
    /// Handles a click on a building (e.g., to spawn units).
    /// </summary>
    [Command(requiresAuthority = false)]
    public void BuildingClicked(int buildingId, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(BuildingClicked))) return;
        ServerPlayer acting = GameCore.Instance?.GetServerPlayerById(BuildingData.UIntToInt(sender.identity.netId));
        if (acting == null || acting.state != PlayerState.Playing) return;
        // Queuing units is only allowed while the game is actually being played.
        if (GameCore.Instance.CurrentState != GameState.Playing) return;

        if (Buildings.TryGetValue(buildingId, out Entity building))
        {
            BuildingData buildingData = EntityManager.GetComponentData<BuildingData>(building);
            ClientPlayer player = sender.identity.GetComponent<ClientPlayer>();

            if (buildingData.ownerId != BuildingData.UIntToInt(player.netId))
            {
                Debug.LogWarning($"Player {player.nickname} tried to click on building {buildingId} that they do not own.");
                return;
            }

            if (EntityManager.HasComponent<SpawnerData>(building))
            {

                SpawnerData spawnerData = EntityManager.GetComponentData<SpawnerData>(building);
                if (spawnerData.count < SpawnerRules.MaxQueue)
                {
                    spawnerData.count += 1;

                    EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                    commandBuffer.SetComponent(building, spawnerData);
                    commandBuffer.Playback(EntityManager);
                    commandBuffer.Dispose();
                }
                else
                {
                    Debug.LogWarning($"Player {player.nickname} tried to queue more than {SpawnerRules.MaxQueue} units on building {buildingId}.");
                }

                Debug.Log($"Player {player.nickname} clicked on building {buildingId}. Spawner count is now {spawnerData.count}");

            }
        }
        else
        {
            Debug.LogWarning($"Building with id {buildingId} not found when trying to click on building.");
        }
    }

    //HELPER FUNCTIONS

    public static int2 GetBuildingSize(BuildingType type)
    {
        return ConfigLoader.LoadConfig().GetBuilding(type).Size;
    }

    #endregion

}
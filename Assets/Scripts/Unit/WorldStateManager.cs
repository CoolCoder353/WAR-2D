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

    /// <summary>
    /// The internal representation of the tilemap.
    /// </summary>
    public TilemapStruct world { get; private set; }

    private EntityManager EntityManager => World.DefaultGameObjectInjectionWorld.EntityManager;

    [Header("Tilemaps")]
    /// <summary>The tilemap defining walkable areas.</summary>
    public Tilemap WalkableTilemap;
    /// <summary>The tilemap defining unwalkable areas (walls, gems).</summary>
    public Tilemap UnwalkableTilemap;

    [Header("Debug")]
    /// <summary>Whether to visualize the tilemap weights in the editor.</summary>
    public bool showTileMapweights = false;

    /// <summary>Offset for visualizing tiles to center them.</summary>
    public Vector3 visualOffset = new Vector3(0.5f, 0.5f, 0);

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
            GenerateTileMap();
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
    }

    [ServerCallback]
    public void OnDrawGizmos()
    {
        //Also check if the game is running
        if (showTileMapweights && Application.isPlaying)
        {
            DrawTileMap(world.tiles);
        }
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
                foreach (int2 tile in footprint)
                {
                    TileNode node = world.GetTile(tile);
                    node.used = 0;
                    world.SetTile(tile, node);
                }
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
            foreach (int2 tile in footprint)
            {
                TileNode node = world.GetTile(tile);
                node.used = 0;
                world.SetTile(tile, node);
            }
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

    #region Tilemap Management

    /// <summary>
    /// Draws the tilemap gizmos for debugging.
    /// </summary>
    [Server]
    private void DrawTileMap(NativeHashMap<int2, TileNode> tilemap)
    {
        foreach (KVPair<int2, TileNode> tilepair in tilemap)
        {
            TileNode tile = tilepair.Value;
            Vector3 position = new Vector3(tile.position.x, tile.position.y, 0) + visualOffset;
            if (tile.tileType == TileType.Gem)
            {
                Gizmos.color = Color.cyan;
            }
            else if (!tile.isWalkable)
            {
                Gizmos.color = Color.red;
            }
            else if (tile.isUsed)
            {
                Gizmos.color = Color.yellow;
            }
            else
            {
                Gizmos.color = Color.Lerp(Color.white, Color.black, tile.weight / 10f);
            }
            Gizmos.DrawCube(position, Vector3.one);
        }
    }

    /// <summary>
    /// Generates the internal tilemap structure from the Unity Tilemaps.
    /// </summary>
    public void GenerateTileMap()
    {
        BoundsInt bounds = WalkableTilemap.cellBounds;
        //Add 2 to the size to account for the border of the chunk
        NativeHashMap<int2, TileNode> tiles = new NativeHashMap<int2, TileNode>((bounds.size.x + 1) * (bounds.size.y + 1), Allocator.Persistent);

        //Go through all the tiles in the chunk
        for (int i = 0; i < bounds.size.x + 1; i++)
        {
            for (int j = 0; j < bounds.size.y + 1; j++)
            {
                int tilex = i + bounds.position.x - 1;
                int tiley = j + bounds.position.y - 1;
                Vector3Int localPlace = new Vector3Int(tilex, tiley, 0);
                Vector3 worldPosition = WalkableTilemap.CellToWorld(localPlace);
                int2 worldPlace = new int2((int)worldPosition.x, (int)worldPosition.y);
                TileType tileType = TileType.Ground;
                int weight = 1;

                if (UnwalkableTilemap.GetTile(localPlace) != null)
                {
                    weight = 0;
                    tileType = UnwalkableTilemap.GetTile(localPlace).name.Contains("Gems") ? TileType.Gem : TileType.Wall;
                }

                TileNode tileNode = new TileNode
                {
                    position = worldPlace,
                    weight = weight,
                    used = 0,
                    tileType = tileType
                };

                tiles[worldPlace] = tileNode;
            }
        }

        TilemapStruct tilemap = new TilemapStruct
        {
            tiles = tiles,
            width = bounds.size.x + 1,
            height = bounds.size.y + 1
        };

        world = tilemap;
    }

    /// <summary>
    /// Gets a tile at a specific position (Server side).
    /// </summary>
    [Server]
    public TileNode GetTile(int2 position)
    {
        return world.GetTile(position);
    }

    /// <summary>
    /// Sets a tile at a specific position.
    /// </summary>
    [Server]
    public void SetTile(int2 position, TileNode tile)
    {
        world.SetTile(position, tile);
    }

    #endregion

    #region Unit Management

    /// <summary>
    /// Updates the view area for a client.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void UpdateClientView(int2 startcorner, int2 endcorner, NetworkConnectionToClient sender = null)
    {
        playerView[sender.identity.GetComponent<ClientPlayer>()] = (startcorner, endcorner);
    }

    /// <summary>
    /// Updates which units and buildings are visible to each player based on their view area.
    /// </summary>
    [Server, BurstCompile]
    public void UpdatePlayerViews()
    {
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

            if (clientUnit.ownerId != sender.identity.GetComponent<ClientPlayer>().netId)
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
            t => world.GetTile(t).isWalkable,
            MaxGoalSearchTiles, out int2 best);
        return found ? best : goal;
    }

    private bool IsFreeGround(int2 tile)
    {
        TileNode node = world.GetTile(tile);
        return node.isWalkable && !node.isUsed;
    }

    /// <summary>Nearest free, unclaimed ground tile to <paramref name="origin"/> (e.g. outside a spawner).</summary>
    [Server]
    public bool TryFindFreeTileNear(int2 origin, int unitId, out int2 tile)
    {
        return TileSearch.FindNearest(origin,
            t => IsFreeGround(t) && Occupancy.IsAvailable(t, unitId),
            t => world.GetTile(t).isWalkable,
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

        List<int2> path = Pathfinding.FindPath(world, startInt, goal);
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
        return PlacementRules.Check(type, anchor, rotation, GetBuildingSize(type), GameCore.Instance.CurrentState,
            player.hasPlacedHQ, world.GetTile, tile => !IsAvaliable(tile, -1));
    }

    /// <summary>
    /// Attempts to add a building at the specified position.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void TryAddBuilding(int2 positon, BuildingType type, float rotation, NetworkConnectionToClient sender = null)
    {
        ClientPlayer placer = sender.identity.GetComponent<ClientPlayer>();
        if (CheckPlacement(type, positon, rotation, placer) != PlacementResult.Ok)
        {
            return;
        }

        BuildingConfig buildingConfig = ConfigLoader.LoadConfig().GetBuilding(type);

        // Check if owner has sufficient resources
        ServerPlayer owner = GameCore.Instance?.GetServerPlayerById(BuildingData.UIntToInt(sender.identity.GetComponent<ClientPlayer>().netId));
        if (owner != null)
        {
            if (owner.data.resources < buildingConfig.UpfrontCost)
            {
                return;
            }

            // Deduct upfront cost
            owner.RemoveResources(buildingConfig.UpfrontCost);

            // Update client display
            if (owner.connection != null && owner.connection.identity != null)
            {
                ClientPlayer clientPlayer = owner.connection.identity.GetComponent<ClientPlayer>();
                if (clientPlayer != null)
                {
                    clientPlayer.TargetUpdateResources(owner.connection, owner.data.resources);
                }
            }
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

        // Add resource cost component to all buildings
        EntityManager.AddComponentData(building, new BuildingResourceComponent
        {
            upfrontCost = buildingConfig.UpfrontCost,
            runningCostPerSecond = buildingConfig.RunningCost,
            timeSinceLastCost = 0f
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
                    miningRate = ConfigLoader.LoadConfig().Resources.MiningRate,
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
                    unitType = UnitType.Tank
                });
                break;
            default:
                Debug.LogError("BuildingType not found in WorldStateManager");
                break;
        }

        //Set the tiles the building will cover to be used
        List<int2> tiles = Footprint.Tiles(positon, GetBuildingSize(type));
        foreach (int2 tile in tiles)
        {
            TileNode tileNode = world.GetTile(tile);
            tileNode.used = 1;
            world.SetTile(tile, tileNode);
        }
        buildingFootprints[buildingData.id] = tiles;

        AddBuilding(building, buildingData.id);
    }

    /// <summary>
    /// Command to check if a building can be built (for client prediction/UI).
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CanBuildBuildingCommand(int2 position, BuildingType type, float rotation, NetworkConnectionToClient sender = null)
    {
        ClientPlayer player = sender.identity.GetComponent<ClientPlayer>();
        player.TargetReceiveCanBuildBuildingResponse(sender, CheckPlacement(type, position, rotation, player) == PlacementResult.Ok);
    }

    /// <summary>
    /// Handles a click on a building (e.g., to spawn units).
    /// </summary>
    [Command(requiresAuthority = false)]
    public void BuildingClicked(int buildingId, NetworkConnectionToClient sender = null)
    {
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
                spawnerData.count += 1;

                EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                commandBuffer.SetComponent(building, spawnerData);
                commandBuffer.Playback(EntityManager);
                commandBuffer.Dispose();

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
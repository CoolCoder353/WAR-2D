using System;
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
using WAR2D.Sim;
using WAR2D.Net.Replication;

/// <summary>
/// Manages the state of the game world, including the tilemap, units, and buildings.
/// Handles server-side logic for movement, building placement, and visibility.
/// </summary>
[BurstCompile]
public partial class WorldStateManager : NetworkBehaviour
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

    /// <summary>Server-side id source for units and buildings in this match.</summary>
    public NetIdAllocator Ids => Sim?.Ids;

    /// <summary>This match's unit replication (server only).</summary>
    public ReplicationService Replication { get; private set; }

    /// <summary>This match's simulation (server only).</summary>
    public SimContext Sim { get; private set; }

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

        Sim = SimContext.Create(World.DefaultGameObjectInjectionWorld, Map, ConfigLoader.LoadConfig());
        Sim.BuildingCreated += OnBuildingCreated;
        Sim.UnitDied += OnUnitDied;
        Sim.TeamResolver = ownerId => GameCore.Instance != null ? GameCore.Instance.TeamOf(ownerId) : -ownerId - 1;
        Sim.Settled += OnSettled;
        Replication = new ReplicationService(Sim, ConfigLoader.LoadConfig()) { CollectBuildings = FillBuildingViews };
        if (GameCore.Instance != null)
            foreach (NetworkIdentity identity in GameCore.Instance.ServerPlayers.Keys)
                if (identity != null && identity.connectionToClient != null)
                    Replication.OnClientJoined(identity.connectionToClient, BuildingData.UIntToInt(identity.netId));
        Sim.BudgetOf = ownerId => GameCore.Instance?.GetServerPlayerById(ownerId)?.Resources ?? 0f;
        Sim.Spend = (ownerId, amount) =>
        {
            ServerPlayer player = GameCore.Instance?.GetServerPlayerById(ownerId);
            if (player != null && !player.TrySpend(amount)) player.TrySpend(player.Resources);
        };
    }

    /// <summary>Records a unit the simulation removed, for its explosion.</summary>
    [Server]
    private void OnUnitDied(int id, float2 position) => pendingDeathPositions.Add(position);

    /// <summary>Registers a building the simulation created, and checks whether every HQ is down.</summary>
    [Server]
    private void OnBuildingCreated(int id, Entity entity)
    {
        AddBuilding(entity, id);
        if (EntityManager.GetComponentData<BuildingData>(entity).buildingType == BuildingType.Base)
            GameCore.Instance?.CheckHQPlacementProgress();
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
        Replication?.Dispose();
        Replication = null;
        if (Sim != null)
        {
            Sim.BuildingCreated -= OnBuildingCreated;
            Sim.Settled -= OnSettled;
            Sim.UnitDied -= OnUnitDied;
            Sim.Dispose();
            Sim = null;
        }
        Map?.Dispose();
        Map = null;
        if (Instance == this) Instance = null;
    }

    /// <summary>Runs on the settled world at each tick boundary: death explosions, filtered by fog.</summary>
    [Server]
    private void OnSettled(SimData data, int tick, int count) => FlushDeathEvents(data);

    /// <summary>Every live building, for the replication layer (main thread, on the settled world).</summary>
    [Server]
    private void FillBuildingViews(List<BuildingView> views)
    {
        foreach (Entity entity in Buildings.Values)
        {
            if (!EntityManager.Exists(entity)) continue;
            BuildingData building = EntityManager.GetComponentData<BuildingData>(entity);
            LocalTransform transform = EntityManager.GetComponentData<LocalTransform>(entity);
            HealthComponent health = EntityManager.GetComponentData<HealthComponent>(entity);
            if (health.currentHealth <= 0f) continue;
            float2 p = transform.Position.xy;
            views.Add(new BuildingView
            {
                Id = building.id,
                OwnerId = building.ownerId,
                Team = Sim.TeamOf(building.ownerId),
                Type = building.buildingType,
                Anchor = (int2)math.round(p),
                Rotation = (byte)((int)math.round(MinerRules.ZDegrees(transform.Rotation) / 90f) & 3),
                Health = (int)math.ceil(health.currentHealth),
                MaxHealth = (int)math.ceil(health.maxHealth),
                Position = p,
            });
        }
    }


    private readonly List<float2> pendingDeathPositions = new List<float2>();

    private const int MaxExplosionsPerMessage = 256;
    private readonly List<float2> seenDeaths = new List<float2>();

    /// <summary>
    /// Sends the deaths recorded since the last tick to each client whose view contains them and whose
    /// team sees where they happened.
    /// </summary>
    [Server]
    private void FlushDeathEvents(SimData data)
    {
        List<float2> deaths = TakeDeathPositions();
        if (deaths.Count == 0) return;

        foreach (KeyValuePair<ClientPlayer, (int2, int2)> view in playerView)
        {
            if (view.Key == null || view.Key.connectionToClient == null) continue;
            int grid = Sim.VisionOf(BuildingData.UIntToInt(view.Key.netId));
            seenDeaths.Clear();
            foreach (float2 death in deaths) if (data.Sees(grid, death)) seenDeaths.Add(death);
            List<Vector2> visible = VisibilityRules.Filter(seenDeaths, view.Value.Item1, view.Value.Item2);
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

        if (EntityManager.HasComponent<BuildingData>(entity))
        {
            int id = EntityManager.GetComponentData<BuildingData>(entity).id;
            Buildings.Remove(id);
            Ids?.Free(id);
            if (buildingFootprints.Remove(id, out List<int2> footprint))
            {
                foreach (int2 tile in footprint) Map?.SetUsed(tile, false);
            }
        }
    }

    /// <summary>
    /// Kills everything the player owns, with explosions: buildings drop to 0 health (DestructionSystem
    /// removes them) and the simulation kills the units at the next tick boundary.
    /// </summary>
    [Server]
    public void KillAllEntitiesOwnedBy(int ownerId)
    {
        foreach (Entity e in Buildings.Values) Kill(e, ownerId);
        Sim?.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillOwner, OwnerId = ownerId });
        Squads.Forget(ownerId);
        ForgetOrderTokens(ownerId);
    }

    private void Kill(Entity entity, int ownerId)
    {
        if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<HealthComponent>(entity)) return;
        if (EntityManager.GetComponentData<BuildingData>(entity).ownerId != ownerId) return;
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
        Sim?.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.DestroyAll });
        foreach (List<int2> footprint in buildingFootprints.Values)
        {
            foreach (int2 tile in footprint) Map?.SetUsed(tile, false);
        }
        Buildings.Clear();
        buildingFootprints.Clear();
    }

    /// <summary>Records the entity's death position (when it has one), then destroys it.</summary>
    private void DestroyWithDeathPosition(Entity entity)
    {
        if (!EntityManager.Exists(entity)) return;
        if (EntityManager.HasComponent<LocalTransform>(entity))
        {
            pendingDeathPositions.Add(EntityManager.GetComponentData<LocalTransform>(entity).Position.xy);
        }
        if (EntityManager.HasComponent<BuildingData>(entity)) Ids?.Free(EntityManager.GetComponentData<BuildingData>(entity).id);
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
        Replication?.SetView(sender, startcorner, endcorner);
    }

    /// <summary>Drops a leaving player's view box.</summary>
    [Server]
    public void RemovePlayerView(ClientPlayer player)
    {
        if (player != null) playerView.Remove(player);
    }

    /// <summary>Registers a building entity.</summary>
    [Server]
    public void AddBuilding(Entity entity, int id)
    {
        Buildings.Add(id, entity);
    }

    private const int MaxSpawnSearchTiles = 4096;

    /// <summary>Nearest free walkable tile to <paramref name="origin"/>, searching through footprints (e.g. out of a spawner).</summary>
    [Server]
    public bool TryFindSpawnTile(int2 origin, out int2 tile)
    {
        MapGrid grid = Map.Grid;
        return TileSearch.FindNearest(origin, grid.IsWalkable, t => grid.TileAt(t) == TileType.Ground, MaxSpawnSearchTiles, out tile);
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
            player.hasPlacedHQ, Map.Grid.TileAt, Map.Grid.IsUsed, tile => false); // units are pushed out of new footprints
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

        // Set at once so a second HQ in the same tick fails the placement check.
        if (type == BuildingType.Base) placer.hasPlacedHQ = true;
        Sim.Commands.Enqueue(new SimCommand
        {
            Kind = SimCommandKind.CreateBuilding,
            OwnerId = buildingData.ownerId,
            Building = new BuildingSpec { Data = buildingData, Rotation = rotation, Config = buildingConfig },
        });

        //Set the tiles the building will cover to be used
        List<int2> tiles = Footprint.Tiles(positon, GetBuildingSize(type));
        foreach (int2 tile in tiles) Map.SetUsed(tile, true);
        buildingFootprints[buildingData.id] = tiles;
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

    /// <summary>
    /// Dev only (performance harness): places a building for any owner, bypassing the economy and the
    /// game-state rules (the footprint must still be free ground). Returns false when refused.
    /// </summary>
    [Server]
    internal bool DevPlaceBuilding(int ownerId, BuildingType type, int2 anchor, float health = 0f)
    {
        if (!DevApi.Allowed || Sim == null) return false;
        BuildingConfig buildingConfig = ConfigLoader.LoadConfig().GetBuilding(type);
        List<int2> tiles = Footprint.Tiles(anchor, GetBuildingSize(type));
        foreach (int2 tile in tiles) if (!Map.Grid.IsWalkable(tile)) return false;
        if (health > 0f)
        {
            buildingConfig = new BuildingConfig
            {
                Health = (int)health, Width = buildingConfig.Width, Height = buildingConfig.Height,
                UpfrontCost = 0f, RunningCost = 0f, SpawnRate = buildingConfig.SpawnRate,
            };
        }
        var data = new BuildingData { position = anchor, id = Ids.Allocate(), buildingType = type, ownerId = ownerId, rotation = 0f };
        Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.CreateBuilding, OwnerId = ownerId, Building = new BuildingSpec { Data = data, Rotation = 0f, Config = buildingConfig } });
        foreach (int2 tile in tiles) Map.SetUsed(tile, true);
        buildingFootprints[data.id] = tiles;
        return true;
    }

    /// <summary>Dev only (performance harness): queues a free unit for any owner.</summary>
    [Server]
    internal bool DevSpawnUnit(int ownerId, float2 position, Action<int> onSpawned = null)
    {
        if (!DevApi.Allowed || Sim == null) return false;
        Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SpawnUnit, OwnerId = ownerId, Position = position, UnitType = UnitType.Tank, OnSpawned = onSpawned });
        return true;
    }

    //HELPER FUNCTIONS

    public static int2 GetBuildingSize(BuildingType type)
    {
        return ConfigLoader.LoadConfig().GetBuilding(type).Size;
    }

    #endregion

}
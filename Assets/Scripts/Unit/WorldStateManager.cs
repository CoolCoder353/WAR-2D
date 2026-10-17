using WAR2D.Dev.Logging;
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

    /// <summary>The match's recorder (server, when Replays/Enabled and not a perf run), or null.</summary>
    public WAR2D.Net.Replay.MatchRecorder Recorder { get; private set; }

    /// <summary>Streams the finished recording to the players who accept it (after GameOver).</summary>
    public WAR2D.Net.Replay.ReplaySender ReplaySender { get; private set; }

    /// <summary>Where each bomb is: bombs do not mark their tile used (units walk over them), so placement looks here.</summary>
    private readonly Dictionary<int2, (int id, int owner)> bombTiles = new Dictionary<int2, (int, int)>();

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

        // The lobby's settings are the only source of the map's size and seed (the config gives the defaults).
        MapConfig m = ConfigLoader.LoadConfig().Match.Map;
        int size = GameCore.Instance != null ? GameCore.Instance.Settings.MapSize : m.Size;
        uint settingsSeed = GameCore.Instance != null ? GameCore.Instance.Settings.Seed : m.Seed;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (size > 0)
        {
            uint seed = settingsSeed != 0 ? settingsSeed : (uint)UnityEngine.Random.Range(1, int.MaxValue);
            Map = MapStore.Generate(size, seed, m.GemChance);
            MapSize = size;
            MapSeed = seed;
            MapHash = Map.Hash();
        }
        else
        {
            Map = MapStore.FromTilemaps(WalkableTilemap, UnwalkableTilemap);
        }
        WarLog.Info(LogCategory.Sim, $"{Map.Grid.Width}x{Map.Grid.Height} map ready in {timer.ElapsedMilliseconds} ms (seed {MapSeed})");

        Sim = SimContext.Create(World.DefaultGameObjectInjectionWorld, Map, ConfigLoader.LoadConfig());
        // The last settled tick, not the clock: WarLog runs on any thread and must never touch the EntityManager.
        WAR2D.Dev.Logging.WarLog.TickSource = () => settledTick;
        Sim.BuildingCreated += OnBuildingCreated;
        Sim.UnitDied += OnUnitDied;
        Sim.BuildingCompleted += (id, owner) =>
        {
            var (found, _, tile) = BuildingOwnerAndTile(id);
            if (found) Alerts.Add(owner, new Alert { Kind = AlertKind.ConstructionFinished, Tile = tile });
        };
        Sim.RepairStalled += (owner, tile) => Alerts?.AddThrottled(owner, new Alert { Kind = AlertKind.RepairsStopped, Tile = tile }, NetworkTime.localTime);
        Sim.TeamResolver = ownerId => GameCore.Instance != null ? GameCore.Instance.TeamOf(ownerId) : -ownerId - 1;
        Sim.Settled += OnSettled;
        explored = null; // the last match's
        WAR2D.Dev.DebugApi.SettledSnapshot.Attach(Sim);
        AlertsConfig alertConfig = ConfigLoader.LoadConfig().Alerts;
        Alerts = new AlertService(alertConfig.ThrottleSeconds, alertConfig.AreaTiles);
        SiegeConfig siegeConfig = ConfigLoader.LoadConfig().Siege;
        radars = new RadarService(siegeConfig.RadarClearSeconds, siegeConfig.RadarAlertCooldownSeconds);
        Sim.UnitKilled += (owner, killer) => GameCore.Instance?.Stats.UnitDied(owner, killer);
        Sim.UnitSpawned += owner => GameCore.Instance?.Stats.UnitBuilt(owner);
        Sim.VisionShareChanged += (from, to, on) =>
            Alerts.Add(to, new Alert { Kind = on ? AlertKind.VisionSharedWithYou : AlertKind.VisionUnshared, OtherOwnerId = from });
        Sim.DiplomacyChanged += (from, to) =>
        {
            if (GameCore.Instance == null) return;
            GameCore.Instance.SendDiplomacy(from);
            GameCore.Instance.SendDiplomacy(to);
        };
        Replication = new ReplicationService(Sim, ConfigLoader.LoadConfig())
        {
            CollectBuildings = FillBuildingViews,
            TerrainCellOf = TerrainCellAt,
            SharesVision = SharesVision,
            // A defeated player still connected watches the rest of the match with the whole map shown (P10).
            Spectating = owner => GameCore.Instance != null && GameCore.Instance.CurrentState == GameState.Playing &&
                                  GameCore.Instance.GetServerPlayerById(owner)?.state == PlayerState.Eliminated,
        };
        ReplaysConfig replays = ConfigLoader.LoadConfig().Replays;
        if (replays.Enabled && !DevApi.PerfFlag && GameCore.Instance != null)
        {
            var header = new WAR2D.Net.Replay.ReplayHeader
            {
                GameVersion = UnityEngine.Application.version, MapSize = MapSize, Seed = MapSeed,
                Settings = GameCore.Instance.Settings, TickSeconds = ConfigLoader.LoadConfig().Simulation.TickSeconds,
                StartedUtc = (System.DateTime.UtcNow - System.DateTime.UnixEpoch).TotalSeconds,
            };
            foreach (var p in GameCore.Instance.ServerPlayers)
            {
                if (p.Key == null) continue;
                var cp = p.Key.GetComponent<ClientPlayer>();
                int owner = BuildingData.UIntToInt(p.Key.netId);
                header.Players.Add(new WAR2D.Net.Replay.ReplayPlayerInfo { OwnerId = owner, Nickname = cp.nickname, Colour = cp.colourIndex, Team = GameCore.Instance.TeamOf(owner) });
            }
            string path = System.IO.Path.Combine(WAR2D.Net.Replay.ReplayLibrary.Folder, $"{System.DateTime.UtcNow:yyyyMMdd-HHmmss}-{MapSeed}{WAR2D.Net.Replay.ReplayFile.Extension}");
            Recorder = new WAR2D.Net.Replay.MatchRecorder(Replication, Sim, header, path, (long)replays.MaxMegabytes << 20);
            RecordingFinished += finished =>
            {
                WAR2D.Net.Replay.ReplayLibrary.Prune(replays.KeepCount);
                if (GameCore.Instance == null) return;
                ReplaySender = new WAR2D.Net.Replay.ReplaySender(1, finished, replays.SendKilobytesPerSecond * 1024, (float)(Sim.Clock.Tick * Sim.Config.Simulation.TickSeconds));
                foreach (NetworkIdentity id in GameCore.Instance.ServerPlayers.Keys)
                    if (id != null && id.connectionToClient != null && id.connectionToClient != NetworkServer.localConnection)
                        ReplaySender.Offer(id.connectionToClient, GameCore.Instance.CurrentState);
            };
        }
        if (GameCore.Instance != null)
            foreach (NetworkIdentity identity in GameCore.Instance.ServerPlayers.Keys)
                if (identity != null && identity.connectionToClient != null)
                    Replication.OnClientJoined(identity.connectionToClient, BuildingData.UIntToInt(identity.netId));
        Sim.Income = (ownerId, amount) =>
        {
            ServerPlayer p = GameCore.Instance?.GetServerPlayerById(ownerId);
            if (p == null) return;
            p.AddIncome(amount);
            GameCore.Instance.Stats.Mined(ownerId, amount);
        };
        Sim.BudgetOf = ownerId => GameCore.Instance?.GetServerPlayerById(ownerId)?.Resources ?? 0f;
        Sim.Spend = (ownerId, amount) =>
        {
            ServerPlayer player = GameCore.Instance?.GetServerPlayerById(ownerId);
            if (player == null) return;
            float before = player.Resources;
            if (!player.TrySpend(amount)) player.TrySpend(player.Resources);
            player.AddUpkeep(before - player.Resources);
        };
        // The map and simulation exist: deal each player their private start options and start the pick clock.
        if (GameCore.Instance != null && GameCore.Instance.CurrentState == GameState.PlacingHQ) GameCore.Instance.BeginPlacementPhase(this);
    }

    /// <summary>Records a unit the simulation removed, for its explosion.</summary>
    [ServerCallback]
    private void OnUnitDied(int id, float2 position) => pendingDeathPositions.Add(position);

    /// <summary>Registers a building the simulation created, and checks whether every HQ is down.</summary>
    [ServerCallback]
    private void OnBuildingCreated(int id, Entity entity)
    {
        AddBuilding(entity, id);
        GameCore.Instance?.Stats.BuildingBuilt(EntityManager.GetComponentData<BuildingData>(entity).ownerId);
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
        if (MapSize <= 0)
        {
            // An authored (tilemap) map, in test scenes only: a pure client can't rebuild it, but the host's
            // client still learns the seen walls and tunnels (its own map view is the scene's tilemaps).
            if (isServer && Map != null)
            {
                terrain = ClientTerrain.Current;
                terrain.Init(Map.Grid);
            }
            return;
        }
        if (!isServer)
        {
            Map = MapStore.Generate(MapSize, MapSeed, ConfigLoader.LoadConfig().Match.Map.GemChance);
            if (Map.Hash() != MapHash)
            {
                WarLog.Error(LogCategory.Sim, $"hash mismatch: local {Map.Hash():X16}, server {MapHash:X16}. Disconnecting.");
                NetworkClient.Disconnect();
                return;
            }
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (WalkableTilemap != null) WalkableTilemap.GetComponent<TilemapRenderer>().enabled = false;
        if (UnwalkableTilemap != null) UnwalkableTilemap.GetComponent<TilemapRenderer>().enabled = false;
        MapView.Show(Map.Grid);
        terrain = ClientTerrain.Current;
        terrain.Init(Map.Grid);
        terrain.Changed += OnTerrainBatch;
        WarLog.Info(LogCategory.Sim, $"baked the {MapSize}² map texture in {timer.ElapsedMilliseconds} ms");
    }

    /// <summary>The client terrain this map subscribed to.</summary>
    private ClientTerrain terrain;

    /// <summary>
    /// A batch of seen terrain changes arrived. A pure client patches its own grid to match (no simulation runs
    /// there); a host's grid is already the truth, so only the view refreshes, and only for tiles its player sees.
    /// </summary>
    private void OnTerrainBatch(IReadOnlyList<int2> tiles)
    {
        if (Map == null) return;
        if (!isServer)
        {
            NativeArray<byte> grid = Map.Grid.Tiles;
            byte[] known = terrain.Tiles;
            foreach (int2 t in tiles) grid[Map.Grid.Index(t)] = known[Map.Grid.Index(t)];
        }
        // Rock, a gem or a wall that turned to floor broke up where the player can see it.
        foreach (int2 t in tiles)
        {
            TileType was = MapView.ShownType(t);
            if (TileRules.IsDiggable(was) && TileRules.IsFloor(Map.Grid.TileAt(t))) Effects.DigDebris(new Vector2(t.x + 0.5f, t.y + 0.5f));
        }
        MapView.Refresh(tiles);
    }

    private volatile int settledTick = -1;

    private double recorderEndAt = -1;
    
    /// <summary>Called by GameCore when the match ends: the recording finishes 3 s later, after the wipe has settled.</summary>
    [Server]
    public void MatchEnded(string outcomeJson)
    {
        pendingOutcome = outcomeJson;
        recorderEndAt = NetworkTime.localTime + 3.0;
    }
    private string pendingOutcome;
    
    [ServerCallback]
    private void Update()
    {
        ReplaySender?.Update(Time.unscaledDeltaTime, GameCore.Instance != null ? GameCore.Instance.CurrentState : GameState.Lobby);
        if (Recorder == null || recorderEndAt < 0 || NetworkTime.localTime < recorderEndAt) return;
        recorderEndAt = -1;
        Recorder.End(Sim.Clock.Tick, pendingOutcome);
        RecordingFinished?.Invoke(Recorder.FinishedPath);
    }
    
    /// <summary>Raised on the server when the match's recording is saved (its path); it is then offered to the players.</summary>
    public event System.Action<string> RecordingFinished;

    private void OnDestroy()
    {
        Recorder?.Dispose();
        Recorder = null;
        // The instance subscribed to, not Current: ClientWorld may have replaced it already (ClientTerrain.Clear).
        if (terrain != null) terrain.Changed -= OnTerrainBatch;
        Replication?.Dispose();
        Replication = null;
        if (Sim != null)
        {
            Sim.BuildingCreated -= OnBuildingCreated;
            Sim.Settled -= OnSettled;
            WAR2D.Dev.DebugApi.SettledSnapshot.Detach(Sim);
            Sim.UnitDied -= OnUnitDied;
            WAR2D.Dev.Logging.WarLog.TickSource = null;
            Sim.Dispose();
            Sim = null;
        }
        Map?.Dispose();
        Map = null;
        if (Instance == this) Instance = null;
    }

    private readonly System.Diagnostics.Stopwatch settledWatch = new System.Diagnostics.Stopwatch();

    /// <summary>Runs on the settled world at each tick boundary: death explosions (filtered by fog) and squad counts (timed for the perf gate).</summary>
    [ServerCallback]
    private void OnSettled(SimData data, int tick, int count)
    {
        settledWatch.Restart();
        OnSettledParts(data, tick, count);
        SimTiming.LastMatchSettledMs = settledWatch.Elapsed.TotalMilliseconds;
    }

    [ServerCallback]
    private void OnSettledParts(SimData data, int tick, int count)
    {
        settledTick = tick;
        Recorder?.OnTick(tick);
        double t = PerfSplits.Now;
        FlushBlasts(data);
        PerfSplits.Mark(1, ref t);
        if ((tick - 1) % data.VisionInterval == 0) { WatchRadars(data, count); SnapshotExplored(data); }
        PerfSplits.Mark(0, ref t);
        FlushDeathEvents(data);
        PerfSplits.Mark(2, ref t);
        SendSquadCounts(data, tick, count);
        PerfSplits.Mark(3, ref t);
        if (tick % UnpaidScanTicks == 0) MarkUnpaidOwners(data, count);
        PerfSplits.Mark(4, ref t);
        Alerts.CollectDamage(data, count, BuildingOwnerAndTile, NetworkTime.localTime);
        PerfSplits.Mark(5, ref t);
        Alerts.Flush();
        PerfSplits.Mark(6, ref t);
    }

    private RadarService radars;
    private readonly List<RadarView> radarScratch = new List<RadarView>();

    /// <summary>Steps every finished Radar's contact watch (once per vision pass) and pings its owner on a new contact.</summary>
    [Server]
    private void WatchRadars(SimData data, int unitCount)
    {
        radarScratch.Clear();
        BuildingConfig radarConfig = ConfigLoader.LoadConfig().GetBuilding(BuildingType.Radar);
        foreach (Entity entity in Buildings.Values)
        {
            if (!EntityManager.Exists(entity)) continue;
            BuildingData building = EntityManager.GetComponentData<BuildingData>(entity);
            if (building.buildingType != BuildingType.Radar || EntityManager.HasComponent<Construction>(entity)) continue;
            if (EntityManager.GetComponentData<HealthComponent>(entity).currentHealth <= 0f) continue;
            int2 anchor = (int2)math.round(building.position);
            radarScratch.Add(new RadarView { Id = building.id, OwnerId = building.ownerId, Tile = anchor, Centre = Footprint.VisualCenter(anchor, radarConfig.Size), Radius = radarConfig.Sight });
        }
        radars.Update(data, unitCount, radarScratch, NetworkTime.localTime, Alerts.Add);
    }

    private readonly List<float2> blastScratch = new List<float2>(), bomberScratch = new List<float2>();

    /// <summary>Reports the bombs and bombers that went off: an alert for a bomb's owner, and the blast to every client whose team sees the tile.</summary>
    [Server]
    private void FlushBlasts(SimData data)
    {
        if (data.BombBlasts.Count == 0) return;
        blastScratch.Clear();
        bomberScratch.Clear();
        foreach (BombBlast blast in data.BombBlasts.ToArray(Unity.Collections.Allocator.Temp))
        {
            // A bomber's owner set it off (or sent it in): only hidden bombs alert their owner.
            if (blast.Bomber != 0) { bomberScratch.Add(blast.Position); continue; }
            Alerts.Add(blast.OwnerId, new Alert { Kind = AlertKind.BombTriggered, Tile = (int2)math.floor(blast.Position) });
            blastScratch.Add(blast.Position);
        }
        Recorder?.Blasts(Sim.Clock.Tick, blastScratch);
        Recorder?.BomberBlasts(Sim.Clock.Tick, bomberScratch);
        foreach (KeyValuePair<ClientPlayer, (int2, int2)> view in playerView)
        {
            if (view.Key == null || view.Key.connectionToClient == null) continue;
            int grid = Sim.VisionOf(BuildingData.UIntToInt(view.Key.netId));
            Vector2[] bombs = VisibilityRules.SeenBlasts(data, grid, blastScratch, view.Value.Item1, view.Value.Item2);
            Vector2[] bombers = VisibilityRules.SeenBlasts(data, grid, bomberScratch, view.Value.Item1, view.Value.Item2);
            if (bombs != null || bombers != null) view.Key.TargetPlayBlasts(view.Key.connectionToClient, bombs, bombers);
        }
    }

    private const int UnpaidScanTicks = 20; // once a second at 20 Hz

    /// <summary>The server's alerts (own damage, upkeep, vision sharing, gifts), sent once per tick.</summary>
    public AlertService Alerts { get; private set; }

    private readonly Dictionary<int, int> armyScratch = new Dictionary<int, int>();

    /// <summary>
    /// Once a second: marks every owner with an unpaid unit (the upkeep alert fires on the first unpaid
    /// second) and records each owner's army size (the end screen's peak army).
    /// </summary>
    [Server]
    private void MarkUnpaidOwners(SimData data, int count)
    {
        GameCore core = GameCore.Instance;
        if (core == null) return;
        // One Burst pass by owner slot: a dictionary update per unit cost ~6 ms at 80,000 units (docs/perf/v0.12).
        using var armies = new NativeArray<int>(SimData.MaxOwners, Allocator.TempJob);
        using var unpaid = new NativeArray<byte>(SimData.MaxOwners, Allocator.TempJob);
        new CountArmiesJob { Count = count, OwnerSlot = data.OwnerSlot, Unpaid = data.Unpaid, Armies = armies, UnpaidBySlot = unpaid }.Run();
        armyScratch.Clear();
        for (int s = 0; s < SimData.MaxOwners; s++)
        {
            int owner = data.OwnerIdBySlot[s];
            if (owner == 0 || armies[s] == 0) continue;
            armyScratch[owner] = armies[s];
            if (unpaid[s] != 0) core.GetServerPlayerById(owner)?.MarkUnpaid();
        }
        foreach (KeyValuePair<int, int> army in armyScratch) core.Stats.Army(army.Key, army.Value);
        Recorder?.Timeline(Sim.Clock.Tick, TimelineRows(core, armyScratch));
    }

    /// <summary>Units per owner slot, and whether any of a slot's units is unpaid.</summary>
    [Unity.Burst.BurstCompile]
    private struct CountArmiesJob : Unity.Jobs.IJob
    {
        public int Count;
        [Unity.Collections.ReadOnly] public NativeArray<byte> OwnerSlot, Unpaid;
        public NativeArray<int> Armies;
        public NativeArray<byte> UnpaidBySlot;

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                int s = OwnerSlot[i];
                if (s >= Armies.Length) continue;
                Armies[s]++;
                if (Unpaid[i] != 0) UnpaidBySlot[s] = 1;
            }
        }
    }

    private IEnumerable<(int, float, int, int)> TimelineRows(GameCore core, Dictionary<int, int> armies)
    {
        var buildingsBy = new Dictionary<int, int>();
        foreach (Entity e in Buildings.Values)
            if (EntityManager.Exists(e)) { int o = EntityManager.GetComponentData<BuildingData>(e).ownerId; buildingsBy.TryGetValue(o, out int n); buildingsBy[o] = n + 1; }
        foreach (var p in core.ServerPlayers)
        {
            if (p.Key == null) continue;
            int owner = BuildingData.UIntToInt(p.Key.netId);
            armies.TryGetValue(owner, out int units);
            buildingsBy.TryGetValue(owner, out int b);
            yield return (owner, p.Value.Resources, units, b);
        }
    }

    /// <summary>A live building's owner and anchor tile, by id.</summary>
    private (bool, int, int2) BuildingOwnerAndTile(int id)
    {
        if (!Buildings.TryGetValue(id, out Entity entity) || !EntityManager.Exists(entity)) return (false, 0, default);
        BuildingData building = EntityManager.GetComponentData<BuildingData>(entity);
        return (true, building.ownerId, (int2)math.round(building.position));
    }

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
                Type = building.buildingType,
                Anchor = (int2)math.round(p),
                Rotation = (byte)((int)math.round(MinerRules.ZDegrees(transform.Rotation) / 90f) & 3),
                Health = (int)math.ceil(health.currentHealth),
                MaxHealth = (int)math.ceil(health.maxHealth),
                Hidden = building.buildingType == BuildingType.Bomb,
                Progress = EntityManager.HasComponent<Construction>(entity)
                    ? (byte)math.clamp((int)math.floor(EntityManager.GetComponentData<Construction>(entity).Progress * 100f), 0, 99)
                    : (byte)100,
                Position = p,
            });
        }
    }


    private readonly List<float2> pendingDeathPositions = new List<float2>();

    private const int MaxExplosionsPerMessage = 256;
    /// <summary>
    /// Most explosions one client is sent per tick (each is a GameObject on the client). Only a mass death
    /// in view, such as the match-end wipe of a big battle, reaches it; the rest of that tick's are dropped.
    /// </summary>
    private const int MaxExplosionsPerFlush = 4 * MaxExplosionsPerMessage;
    private readonly List<float2> seenDeaths = new List<float2>();

    /// <summary>
    /// Sends the deaths recorded since the last tick to each client whose view contains them and whose
    /// team sees where they happened. Once the match is over (GameOver) nothing is secret any more, so the
    /// match-end wipe skips the fog test (by then the wipe has emptied the fog grids).
    /// </summary>
    [Server]
    private void FlushDeathEvents(SimData data)
    {
        List<float2> deaths = TakeDeathPositions();
        if (deaths.Count == 0) return;
        Recorder?.Explosions(SimContext.Current.Clock.Tick, deaths);
        GameState state = GameCore.Instance != null ? GameCore.Instance.CurrentState : GameState.Playing;

        foreach (KeyValuePair<ClientPlayer, (int2, int2)> view in playerView)
        {
            if (view.Key == null || view.Key.connectionToClient == null) continue;
            int grid = Sim.VisionOf(BuildingData.UIntToInt(view.Key.netId));
            seenDeaths.Clear();
            foreach (float2 death in deaths) if (VisibilityRules.ShowsDeath(state, data.Sees(grid, death))) seenDeaths.Add(death);
            List<Vector2> visible = VisibilityRules.Filter(seenDeaths, view.Value.Item1, view.Value.Item2);
            int count = Mathf.Min(visible.Count, MaxExplosionsPerFlush);
            for (int i = 0; i < count; i += MaxExplosionsPerMessage)
            {
                int n = Mathf.Min(MaxExplosionsPerMessage, count - i);
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
            BuildingData building = EntityManager.GetComponentData<BuildingData>(entity);
            int id = building.id;
            bool cancelled = cancelledBlueprints.Remove(id);
            if (GameCore.Instance != null && GameCore.Instance.CurrentState == GameState.Playing)
            {
                if (cancelled) GameCore.Instance.Stats.BlueprintCancelled(building.ownerId);
                else GameCore.Instance.Stats.BuildingLost(building.ownerId);
            }
            Buildings.Remove(id);
            if (building.buildingType == BuildingType.Bomb)
            {
                // Only drop the entry if it is still this bomb's (another may have been built on the tile since).
                int2 bombTile = (int2)math.round(building.position);
                if (bombTiles.TryGetValue(bombTile, out var registered) && registered.id == id) bombTiles.Remove(bombTile);
            }
            if (building.buildingType == BuildingType.Radar) radars?.Forget(id);
            Sim?.ForgetBuilding(id);
            Ids?.Free(id);
            if (buildingFootprints.Remove(id, out List<int2> footprint))
            {
                foreach (int2 tile in footprint) Map?.QueueUsed(tile, false);
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
        Alerts?.Forget(ownerId);
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
        // NOT cleared here: FlushDeathEvents drains it at the next settled boundary (the tick keeps
        // running in GameOver for this, see SimBoundarySystem.RunsIn).
        foreach (Entity e in Buildings.Values) DestroyWithDeathPosition(e);
        Sim?.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.DestroyAll });
        foreach (List<int2> footprint in buildingFootprints.Values)
        {
            foreach (int2 tile in footprint) Map?.QueueUsed(tile, false);
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
    public bool TryFindSpawnTile(int2 origin, out int2 tile, bool large = false)
    {
        MapGrid grid = Map.Grid;
        // Clearance is part of the goal, not the walk: the search starts inside the producer's footprint,
        // where no tile has a large unit's clearance, and must cross it to reach open ground.
        return TileSearch.FindNearest(origin, t => Map.IsWalkable(t) && (!large || HasLargeClearance(t)), t => TileRules.IsFloor(grid.TileAt(t)), MaxSpawnSearchTiles, out tile);
    }

    /// <summary>
    /// True when a large unit (2 tiles of clearance) fits on <paramref name="tile"/>: the tile and its three
    /// neighbours towards each corner of a 2x2 block are walkable, for at least one block holding the tile.
    /// </summary>
    private bool HasLargeClearance(int2 tile)
    {
        for (int dx = -1; dx <= 0; dx++)
        for (int dy = -1; dy <= 0; dy++)
        {
            bool fits = true;
            for (int x = 0; x < 2 && fits; x++)
            for (int y = 0; y < 2 && fits; y++)
            {
                int2 t = tile + new int2(dx + x, dy + y);
                fits = Map.Grid.Contains(t) && Map.IsWalkable(t) && TileRules.IsFloor(Map.Grid.TileAt(t));
            }
            if (fits) return true;
        }
        return false;
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

        // Only on ground the player's side has seen: a blueprint in the fog would probe it (the HQ is placed before anyone sees).
        if (type != BuildingType.Base)
            foreach (int2 tile in Footprint.Tiles(anchor, GetBuildingSize(type)))
                if (!HasExplored(BuildingData.UIntToInt(player.netId), tile)) return PlacementResult.Unexplored;

        return PlacementRules.Check(type, anchor, rotation, GetBuildingSize(type), GameCore.Instance.CurrentState,
            player.hasPlacedHQ, Map.Grid.TileAt, tile => Map.IsUsed(tile) || KnownBombAt(tile, BuildingData.UIntToInt(player.netId)), tile => false, // units are pushed out of new footprints
            OwnStartOptions(player));
    }

    private byte[] explored;
    private int exploredFogW, exploredFogH, exploredCellSize, exploredCells;

    /// <summary>
    /// Copies every vision grid's explored cells at <c>Settled</c> after a vision pass, so placement checks
    /// between ticks never read <see cref="SimData"/>.
    /// </summary>
    [Server]
    private void SnapshotExplored(SimData data)
    {
        if (explored == null || explored.Length != data.Explored.Length) explored = new byte[data.Explored.Length];
        data.Explored.CopyTo(explored);
        exploredFogW = data.FogW;
        exploredFogH = data.FogH;
        exploredCellSize = data.FogCellSize;
        exploredCells = data.FogCells;
    }

    /// <summary>True when the owner's side has ever seen <paramref name="tile"/> (as of the last vision pass).</summary>
    [Server]
    internal bool HasExplored(int ownerId, int2 tile)
    {
        if (explored == null || Sim == null || exploredCellSize <= 0 || !Sim.TrySlotOf(ownerId, out int grid)) return false;
        int2 cell = math.clamp(tile / exploredCellSize, 0, new int2(exploredFogW - 1, exploredFogH - 1));
        int index = grid * exploredCells + cell.y * exploredFogW + cell.x;
        return (uint)index < (uint)explored.Length && explored[index] != 0;
    }

    /// <summary>The test for "inside one of this player's start options", or null when they have none (dev bots and tilemap maps).</summary>
    private System.Func<int2, bool> OwnStartOptions(ClientPlayer player)
    {
        int2[] sites = Map.HqSites;
        int[] options = GameCore.Instance?.GetServerPlayerById(BuildingData.UIntToInt(player.netId))?.StartOptions;
        if (sites == null || options == null) return null;
        return tile =>
        {
            foreach (int option in options)
                if (option >= 0 && option < sites.Length && PlacementRules.InClearing(tile, sites[option], WAR2D.World.MapGenerator.HqClearRadius)) return true;
            return false;
        };
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
        PlaceBuilding(sender.identity.GetComponent<ClientPlayer>(), positon, type, rotation);
    }

    /// <summary>Checks, charges and queues a building for <paramref name="placer"/>. Returns false when the placement is refused.</summary>
    [Server]
    internal bool PlaceBuilding(ClientPlayer placer, int2 positon, BuildingType type, float rotation)
    {
        if (CheckPlacement(type, positon, rotation, placer) != PlacementResult.Ok)
        {
            return false;
        }

        BuildingConfig buildingConfig = ConfigLoader.LoadConfig().GetBuilding(type);

        // Check the owner can afford the building before it is placed.
        ServerPlayer owner = GameCore.Instance?.GetServerPlayerById(BuildingData.UIntToInt(placer.netId));
        if (owner == null || !owner.TrySpend(buildingConfig.UpfrontCost))
        {
            return false;
        }

        int buildingId;
        try { buildingId = Ids.Allocate(); }
        catch (System.InvalidOperationException)
        {
            owner.Refund(buildingConfig.UpfrontCost); // out of ids: the player keeps the money
            return false;
        }
        BuildingData buildingData = new BuildingData
        {
            position = new float2(positon.x, positon.y),
            id = buildingId,
            buildingType = type,
            ownerId = BuildingData.UIntToInt(placer.netId),
            rotation = rotation
        };


        WarLog.Info(LogCategory.Sim, $"Placing building of type {type} at position {positon} with rotation {rotation}, owned by player {buildingData.ownerId}");

        // Set at once so a second HQ in the same tick fails the placement check.
        if (type == BuildingType.Base)
        {
            placer.hasPlacedHQ = true;
            GameCore.Instance?.OnHqPlaced(placer, positon);
        }
        Sim.Commands.Enqueue(new SimCommand
        {
            Kind = SimCommandKind.CreateBuilding,
            OwnerId = buildingData.ownerId,
            Building = new BuildingSpec { Data = buildingData, Rotation = rotation, Config = buildingConfig },
        });

        List<int2> tiles = Footprint.Tiles(positon, GetBuildingSize(type));
        // Building over a hidden bomb the placer may not know sets it off (the placement never says it was there).
        foreach (int2 tile in tiles) DetonateUnknownBomb(tile, buildingData.ownerId);
        if (type == BuildingType.Bomb)
        {
            // A bomb's tile is not marked used: units walk over it.
            bombTiles[positon] = (buildingData.id, buildingData.ownerId);
            return true;
        }
        //Set the tiles the building will cover to be used
        foreach (int2 tile in tiles) Map.QueueUsed(tile, true);
        buildingFootprints[buildingData.id] = tiles;
        return true;
    }

    /// <summary>
    /// Command to check if a building can be built (for client prediction/UI).
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CanBuildBuildingCommand(int2 position, BuildingType type, float rotation, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CanBuildBuildingCommand))) return;
        ClientPlayer player = sender.identity.GetComponent<ClientPlayer>();
        player.TargetReceiveCanBuildBuildingResponse(sender, position, type, rotation, CheckPlacement(type, position, rotation, player) == PlacementResult.Ok);
    }

    /// <summary>
    /// Adds or removes units of the selected type on each named producer the sender owns (the queue's −, +, +10
    /// and +50). Steps other than <see cref="SpawnerRules.QueueSteps"/> and lists longer than
    /// <see cref="SpawnerRules.MaxSpawnersPerCommand"/> are refused.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdChangeQueues(int[] buildingIds, int delta, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdChangeQueues))) return;
        if (buildingIds == null || buildingIds.Length > SpawnerRules.MaxSpawnersPerCommand || !SpawnerRules.IsQueueStep(delta)) return;
        foreach (int id in buildingIds) ChangeQueue(id, sender, delta);
    }

    /// <summary>
    /// Sets the unit type each named, finished producer the sender owns makes, and clears its queue and the
    /// progress towards its next unit. Producers that can't make the type are skipped.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdSetProduction(int[] buildingIds, byte unitType, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdSetProduction))) return;
        if (buildingIds == null || buildingIds.Length > SpawnerRules.MaxSpawnersPerCommand) return;
        if (unitType == 0 || !System.Enum.IsDefined(typeof(UnitType), (UnitType)unitType)) return;
        if (!TryActingOwner(sender, out int owner)) return;
        foreach (int id in buildingIds)
        {
            if (!TryOwnSpawner(id, owner, out Entity building) || EntityManager.HasComponent<Construction>(building)) continue;
            BuildingType buildingType = EntityManager.GetComponentData<BuildingData>(building).buildingType;
            if (!SpawnerRules.MayProduce(buildingType, (UnitType)unitType)) continue;
            SpawnerData spawner = EntityManager.GetComponentData<SpawnerData>(building);
            if (spawner.unitType == unitType) continue; // already making it: keep the queue
            spawner.unitType = unitType;
            spawner.count = 0;
            spawner.progress = 0f;
            EntityManager.SetComponentData(building, spawner);
            SendSpawnerQueue(owner, id, unitType, 0);
        }
    }

    /// <summary>
    /// Sets (or, with <paramref name="clear"/>, removes) the rally tile of each named spawner the sender owns:
    /// new units from it get a move order there. The tile must be on the map; an order onto rock snaps like any
    /// other (<see cref="WAR2D.Sim.OrderBook.SnapGoal"/>). Only the owner is told.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdSetRally(int[] buildingIds, int2 tile, bool clear, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdSetRally))) return;
        if (buildingIds == null || buildingIds.Length > SpawnerRules.MaxSpawnersPerCommand || Map == null || !Map.Grid.Contains(tile)) return;
        if (!TryActingOwner(sender, out int owner)) return;
        foreach (int id in buildingIds)
        {
            if (!TryOwnSpawner(id, owner, out Entity building)) continue;
            SpawnerData spawner = EntityManager.GetComponentData<SpawnerData>(building);
            spawner.rally = tile;
            spawner.hasRally = !clear;
            EntityManager.SetComponentData(building, spawner);
            NetworkConnectionToClient connection = GameCore.Instance?.GetServerPlayerById(owner)?.connection;
            if (connection?.identity != null && connection.identity.TryGetComponent(out ClientPlayer player))
                player.TargetSpawnerRally(connection, id, tile, !clear);
        }
    }

    /// <summary>True when <paramref name="buildingId"/> is a live spawner owned by <paramref name="owner"/>.</summary>
    [Server]
    private bool TryOwnSpawner(int buildingId, int owner, out Entity building)
    {
        if (!Buildings.TryGetValue(buildingId, out building) || !EntityManager.Exists(building)) return false;
        return EntityManager.GetComponentData<BuildingData>(building).ownerId == owner && EntityManager.HasComponent<SpawnerData>(building);
    }

    /// <summary>
    /// Adds or removes <paramref name="delta"/> units of the selected type on a finished producer the sender
    /// owns, while Playing, and tells the owner the new count.
    /// </summary>
    [Server]
    private void ChangeQueue(int buildingId, NetworkConnectionToClient sender, int delta)
    {
        if (!TryActingOwner(sender, out int owner) || !TryOwnSpawner(buildingId, owner, out Entity building)) return;
        if (EntityManager.HasComponent<Construction>(building)) return;
        SpawnerData spawner = EntityManager.GetComponentData<SpawnerData>(building);
        if (SpawnerRules.Apply(ref spawner.count, delta) == 0) return;
        EntityManager.SetComponentData(building, spawner);
        SendSpawnerQueue(owner, buildingId, spawner.unitType, spawner.count);
    }

    /// <summary>Tells a producer's owner (only) its selected type and queue count.</summary>
    [Server]
    public void SendSpawnerQueue(int ownerId, int buildingId, byte unitType, int count)
    {
        NetworkConnectionToClient connection = GameCore.Instance?.GetServerPlayerById(ownerId)?.connection;
        if (connection?.identity == null || !connection.identity.TryGetComponent(out ClientPlayer player)) return;
        player.TargetSpawnerQueue(connection, buildingId, unitType, count);
    }

    /// <summary>
    /// Dev only (performance harness): places a building for any owner, bypassing the economy and the
    /// game-state rules (the footprint must still be free ground). Returns false when refused.
    /// </summary>
    [Server]
    internal bool DevPlaceBuilding(int ownerId, BuildingType type, int2 anchor, float health = 0f, float rotation = 0f)
    {
        if (!DevApi.Allowed || Sim == null) return false;
        BuildingConfig buildingConfig = ConfigLoader.LoadConfig().GetBuilding(type);
        List<int2> tiles = Footprint.Tiles(anchor, GetBuildingSize(type));
        foreach (int2 tile in tiles) if (!Map.IsWalkable(tile)) return false;
        if (health > 0f)
        {
            buildingConfig = new BuildingConfig
            {
                Health = (int)health, Width = buildingConfig.Width, Height = buildingConfig.Height,
                UpfrontCost = 0f, RunningCost = 0f,
            };
        }
        var data = new BuildingData { position = anchor, id = Ids.Allocate(), buildingType = type, ownerId = ownerId, rotation = rotation };
        Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.CreateBuilding, OwnerId = ownerId, Building = new BuildingSpec { Data = data, Rotation = rotation, Config = buildingConfig, Complete = true } });
        foreach (int2 tile in tiles) Map.QueueUsed(tile, true);
        buildingFootprints[data.id] = tiles;
        return true;
    }

    /// <summary>Dev only (performance harness): queues a free unit for any owner.</summary>
    [Server]
    internal bool DevSpawnUnit(int ownerId, float2 position, Action<int> onSpawned = null, UnitType type = UnitType.Tank)
    {
        if (!DevApi.Allowed || Sim == null) return false;
        Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SpawnUnit, OwnerId = ownerId, Position = position, UnitType = type, OnSpawned = onSpawned });
        return true;
    }

    //HELPER FUNCTIONS

    public static int2 GetBuildingSize(BuildingType type)
    {
        return ConfigLoader.LoadConfig().GetBuilding(type).Size;
    }

    #endregion

}
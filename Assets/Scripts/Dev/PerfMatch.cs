using WAR2D.Dev.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Config;
using Mirror;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Net.Replication;
using WAR2D.Sim;

/// <summary>
/// The v0.4 performance gate (spec §4.1). Started with <c>-perf</c> (optionally <c>-perfOut &lt;dir&gt;</c>),
/// it hosts a match on a generated 1024² map (seed 1) with the host plus seven bots, 10,000 units
/// each, runs the v0.3 battle script (four fronts, a quarter of each army re-ordered every 2 s), warms
/// up for 100 ticks, samples 1,200 ticks, writes <c>perf.csv</c> and quits. The bots' replication
/// streams go to virtual clients that count payload plus KCP/UDP overhead. Armies are topped back up to
/// 10,000 every second (as the v0.3 spike's spawn target did), so the load stays at 80,000 units while
/// the battle kills them. With <c>-perfClients n</c> the first n bots are instead real players (see
/// <see cref="PerfClient"/>) who measure what they receive over KCP. Does nothing without the flag.
/// </summary>
public sealed class PerfMatch : MonoBehaviour
{
    public const int Owners = 8, UnitsPerOwner = 10000, WarmupTicks = 100, SampleTicks = 1200, OrderEveryTicks = 40;
    private const int BotIdBase = 1000001;

    private readonly PerfStats stats = new PerfStats();
    private readonly HashSet<int>[] armyIds = new HashSet<int>[Owners];
    private readonly int[] pendingSpawns = new int[Owners];
    private const int TopUpEveryTicks = 20, MaxTopUpPerOwner = 2000, ReinforcementTiles = 2048;
    private List<int2>[] reinforcementTiles;
    private readonly int[] reinforcementCursor = new int[Owners];
    private readonly long[] botBytesThisSecond = new long[Owners];
    private WAR2D.Net.Replay.MatchRecorder perfRecorder;
    private long lastRecordBytes;
    private int[] owners;
    /// <summary>Owners before this index are real players (the host and -perfClients); from it on, bots.</summary>
    private int firstBot = 1;
    private int2[] armyStart;
    private int lastTick = -1, startTick = -1;
    private float secondTimer;

    // v0.7 siege load per owner: walls, Radars, bombs, Builders on the walls and Diggers on the rock.
    public const int WallsPerOwner = 200, RadarsPerOwner = 5, BombsPerOwner = 20, BuildersPerOwner = 50, DiggersPerOwner = 20, SiegeSetupTick = 60;
    private bool siegeSetUp;
    private readonly List<int>[] builderIds = new List<int>[Owners], diggerIds = new List<int>[Owners];
    private readonly List<int2>[] wallTiles = new List<int2>[Owners], rockTiles = new List<int2>[Owners];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (!DevApi.PerfFlag || ArgValue("-perfClient") != null) return;
        var go = new GameObject("PerfMatch");
        DontDestroyOnLoad(go);
        go.AddComponent<PerfMatch>();
    }

    private IEnumerator Start()
    {
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        yield return new WaitUntil(() => GameManager.Instance != null);

        GameConfigData config = ConfigLoader.LoadConfig();
        config.Match.Map.Size = 1024;
        config.Match.Map.Seed = 1;
        config.Match.CountdownSeconds = 0.5f;
        config.Resources.StartingResources = 1e9f;
        // Tuning experiments without a rebuild (values still have to be inside the config ranges).
        if (float.TryParse(ArgValue("-perfCorrectionThreshold"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float threshold))
            config.Replication.CorrectionThreshold = math.clamp(threshold, 0.01f, 8f);
        if (int.TryParse(ArgValue("-perfCorrectionInterval"), out int interval)) config.Replication.CorrectionIntervalTicks = math.clamp(interval, 1, 20);
        if (int.TryParse(ArgValue("-perfDeltaScale"), out int scale)) config.Replication.DeltaScale = math.clamp(scale, 1, 127);
        if (float.TryParse(ArgValue("-perfSeparationStrength"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float strength))
            config.Simulation.SeparationStrength = math.clamp(strength, 0f, 4f);

        GameManager.Instance.HostServer();
        yield return new WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null);
        // Real players (-perfClients n): wait for them; the rest of the owners are bots.
        int realClients = int.TryParse(ArgValue("-perfClients"), out int wanted) ? math.clamp(wanted, 0, Owners - 1) : 0;
        float waitStart = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => GameCore.Instance.ServerPlayers.Count >= 1 + realClients || Time.realtimeSinceStartup - waitStart > 180f);
        if (GameCore.Instance.ServerPlayers.Count < 1 + realClients) Fail($"only {GameCore.Instance.ServerPlayers.Count - 1} of {realClients} perf clients joined");
        GameCore.Instance.Cmd_StartGame();
        yield return new WaitUntil(() => WorldStateManager.Instance != null && WorldStateManager.Instance.Sim != null && GameCore.Instance.CurrentState == GameState.PlacingHQ);
        WorldStateManager world = WorldStateManager.Instance;

        int host = BuildingData.UIntToInt(NetworkClient.localPlayer.netId);
        owners = new int[Owners];
        owners[0] = host;
        firstBot = 1;
        foreach (NetworkIdentity identity in GameCore.Instance.ServerPlayers.Keys)
        {
            int id = BuildingData.UIntToInt(identity.netId);
            if (id != host && firstBot < Owners) owners[firstBot++] = id;
        }
        for (int i = firstBot; i < Owners; i++)
        {
            owners[i] = BotIdBase + i;
            GameCore.Instance.AddBot(owners[i], 1e9f);
        }

        int2[] sites = PairedSites(world.Map.HqSites);
        for (int i = 0; i < Owners; i++)
            if (!PlaceNear(world, owners[i], sites[i])) Fail($"no room for owner {i}'s HQ near {sites[i]}");
        foreach (NetworkIdentity identity in GameCore.Instance.ServerPlayers.Keys) identity.GetComponent<ClientPlayer>().hasPlacedHQ = true;
        GameCore.Instance.CheckHQPlacementProgress();
        yield return new WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing);

        // Armies: each pair of neighbouring clearings meets at its front's midpoint, each army on its own side.
        armyStart = new int2[Owners];
        for (int i = 0; i < Owners; i++)
        {
            int2 a = sites[i & ~1], b = sites[(i & ~1) + 1];
            float2 mid = ((float2)a + b) * 0.5f;
            armyStart[i] = (int2)math.round(mid + math.normalizesafe((float2)sites[i] - mid) * 12f);
            armyIds[i] = new HashSet<int>();
            int placed = Spawn(world, i, UnitsPerOwner, WalkableSpiral(world, armyStart[i], UnitsPerOwner));
            if (placed < UnitsPerOwner) Fail($"owner {i}: only {placed} spawn tiles");
        }
        // Reinforcements appear on a rotating ring of tiles around each HQ (as the v0.3 spike's lifecycle
        // did), not on top of the army in the fight.
        reinforcementTiles = new List<int2>[Owners];
        for (int i = 0; i < Owners; i++) reinforcementTiles[i] = new List<int2>(WalkableSpiral(world, sites[i] + new int2(0, 6), ReinforcementTiles));
        world.Sim.UnitDied += (id, _) => { foreach (HashSet<int> army in armyIds) if (army.Remove(id)) break; };

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-perfRecord") >= 0)
        {
            var header = new WAR2D.Net.Replay.ReplayHeader { GameVersion = Application.version, MapSize = world.MapSize, Seed = world.MapSeed, TickSeconds = config.Simulation.TickSeconds };
            string recPath = Path.Combine(ArgValue("-perfOut") ?? Application.persistentDataPath, "perf-record" + WAR2D.Net.Replay.ReplayFile.Extension);
            perfRecorder = new WAR2D.Net.Replay.MatchRecorder(world.Replication, world.Sim, header, recPath, 1L << 33);
        }

        bool breakdown = Array.IndexOf(Environment.GetCommandLineArgs(), "-perfBreakdown") >= 0;
        for (int i = firstBot; i < Owners; i++)
        {
            int bot = i;
            if (breakdown && i == firstBot)
            {
                world.Replication.AddVirtualClient(owners[i], (segment, channel) => { botBytesThisSecond[bot] += WireLimits.WireBytes(segment.Count); if (channel >= 0) CountTypes(segment); });
                world.Replication.SetVirtualView(owners[i], armyStart[i] - new int2(32, 18), armyStart[i] + new int2(32, 18));
                continue;
            }
            world.Replication.AddVirtualClient(owners[i], (segment, channel) => botBytesThisSecond[bot] += WireLimits.WireBytes(segment.Count));
            int2 halfView = new int2(32, 18); // a 1080p view at the harness zoom
            world.Replication.SetVirtualView(owners[i], armyStart[i] - halfView, armyStart[i] + halfView);
        }

        Camera cam = Camera.main;
        if (cam != null)
        {
            cam.transform.position = new Vector3(armyStart[0].x, armyStart[0].y, cam.transform.position.z);
            cam.orthographicSize = 34f;
        }
        WarLog.Info(LogCategory.Dev, "match ready; sampling");
    }

    private void Update()
    {
        SimContext sim = SimContext.Current;
        if (owners == null || armyStart == null || sim == null) return;
        int tick = sim.Clock.Tick;

        if (startTick >= 0)
        {
            stats.Add("perf.fps", 1.0 / Math.Max(1e-6, Time.unscaledDeltaTime));
            stats.Add("perf.frame.ms", Time.unscaledDeltaTime * 1000.0);
            secondTimer += Time.unscaledDeltaTime;
            if (secondTimer >= 1f)
            {
                for (int i = firstBot; i < Owners; i++)
                {
                    stats.Add("perf.bw.avg", botBytesThisSecond[i] / secondTimer);
                    stats.Add("perf.bw.peak1s", botBytesThisSecond[i] / secondTimer);
                    botBytesThisSecond[i] = 0;
                }
                if (perfRecorder != null)
                {
                    stats.Add("perf.recordKBps", (perfRecorder.QueuedBytes - lastRecordBytes) / 1024.0 / secondTimer);
                    lastRecordBytes = perfRecorder.QueuedBytes;
                }
                secondTimer = 0f;
            }
        }

        if (tick == lastTick) return;
        lastTick = tick;
        if (!siegeSetUp && tick >= SiegeSetupTick) SetUpSiege(sim);
        if (tick % OrderEveryTicks == 0) { IssueOrders(sim, tick); IssueSiegeOrders(sim, tick); }
        if (tick % TopUpEveryTicks == 0) TopUp();

        if (startTick < 0)
        {
            if (sim.Clock.UnitCount >= Owners * UnitsPerOwner * 9 / 10 && tick > WarmupTicks)
            {
                startTick = tick;
                for (int i = 0; i < Owners; i++) botBytesThisSecond[i] = 0;
                secondTimer = 0f;
            }
            return;
        }
        stats.Add("perf.tick.main", SimTiming.LastMainThreadMs - SimTiming.LastBoundaryWaitMs);
        stats.Add("perf.tick.wait", SimTiming.LastBoundaryWaitMs);
        stats.Add("perf.tick.replication", ReplicationService.LastMilliseconds);
        stats.Add("perf.units", sim.Clock.UnitCount);
        if (tick - startTick >= SampleTicks) Finish();
    }

    /// <summary>A quarter of each army (rotating) heads for its front partner's start point.</summary>
    private void IssueOrders(SimContext sim, int tick)
    {
        int quarter = tick / OrderEveryTicks % 4;
        for (int i = 0; i < Owners; i++)
        {
            HashSet<int> ids = armyIds[i];
            if (ids == null || ids.Count == 0) continue;
            var picked = new List<int>(ids.Count / 4 + 1);
            foreach (int id in ids) if (NetIdAllocator.IndexOf(id) % 4 == quarter) picked.Add(id);
            sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = OrderKind.AttackMove, OwnerId = owners[i], Tile = armyStart[i ^ 1], Ids = picked.ToArray() });
        }
    }

    /// <summary>Places each owner's walls (blueprints), Radars and bombs near its HQ and spawns its Builders and Diggers.</summary>
    private void SetUpSiege(SimContext sim)
    {
        siegeSetUp = true;
        WorldStateManager world = WorldStateManager.Instance;
        int2[] sites = PairedSites(world.Map.HqSites);
        for (int i = 0; i < Owners; i++)
        {
            int owner = owners[i];
            int2 site = sites[i];
            builderIds[i] = new List<int>();
            diggerIds[i] = new List<int>();
            wallTiles[i] = new List<int2>();
            rockTiles[i] = new List<int2>();
            int radars = 0, bombs = 0, step = 0;
            foreach (int2 tile in WalkableSpiral(world, site, 900))
            {
                step++;
                if (radars < RadarsPerOwner && step % 37 == 0 && world.DevPlaceBuilding(owner, BuildingType.Radar, tile, 1e6f)) radars++;
                else if (bombs < BombsPerOwner && step % 11 == 0 && world.DevPlaceBuilding(owner, BuildingType.Bomb, tile, 1e6f)) bombs++;
            }
            int walls = 0;
            foreach (int2 tile in WalkableSpiral(world, site, 600))
            {
                if (walls >= WallsPerOwner * 3 / 2) break;
                if (math.abs(tile.x - site.x) < 5 && math.abs(tile.y - site.y) < 5) continue;
                if (tile.x % 2 != 0 || tile.y % 2 != 0) continue; // spaced so units can still path
                walls++;
                int2 t = tile;
                List<int2> list = wallTiles[i];
                sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.PlaceWall, OwnerId = owner, Tile = t, OnApplied = ok => { if (ok && list.Count < WallsPerOwner) list.Add(t); } });
            }
            for (int dy = -40; dy <= 40; dy++)
            for (int dx = -40; dx <= 40; dx++)
            {
                int2 t = site + new int2(dx, dy);
                if (world.Map.Grid.Contains(t) && world.Map.Grid.TileAt(t) == TileType.Wall && rockTiles[i].Count < 400) rockTiles[i].Add(t);
            }
            for (int k = 0; k < BuildersPerOwner; k++) { List<int> ids = builderIds[i]; world.DevSpawnUnit(owner, (float2)site + new float2(-3 + k % 8, -7 - k / 8), id => { if (id > 0) ids.Add(id); }, UnitType.Builder); }
            for (int k = 0; k < DiggersPerOwner; k++) { List<int> ids = diggerIds[i]; world.DevSpawnUnit(owner, (float2)site + new float2(-3 + k % 8, -12 - k / 8), id => { if (id > 0) ids.Add(id); }, UnitType.Digger); }
        }
    }

    /// <summary>Keeps the Builders building walls and the Diggers digging rock, in batches of ten.</summary>
    private void IssueSiegeOrders(SimContext sim, int tick)
    {
        if (!siegeSetUp) return;
        int round = tick / OrderEveryTicks;
        for (int i = 0; i < Owners; i++)
        {
            Batches(sim, builderIds[i], wallTiles[i], OrderKind.Build, owners[i], round);
            Batches(sim, diggerIds[i], rockTiles[i], OrderKind.Dig, owners[i], round);
        }
    }

    private static void Batches(SimContext sim, List<int> ids, List<int2> targets, OrderKind kind, int owner, int round)
    {
        if (ids == null || ids.Count == 0 || targets == null || targets.Count == 0) return;
        for (int b = 0; b * 10 < ids.Count; b++)
        {
            int count = math.min(10, ids.Count - b * 10);
            var batch = new int[count];
            ids.CopyTo(b * 10, batch, 0, count);
            sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = kind, OwnerId = owner, Tile = targets[(round * 7 + b) % targets.Count], Ids = batch });
        }
    }

    private readonly long[] typeBytes = new long[8], typeUnits = new long[8];
    private readonly List<EnterUnit> scratchEnters = new List<EnterUnit>();
    private readonly List<RouteUnit> scratchRoutes = new List<RouteUnit>();
    private readonly List<CorrectionUnit> scratchCorrections = new List<CorrectionUnit>();
    private readonly List<HealthUnit> scratchHealth = new List<HealthUnit>();
    private readonly List<LeaveUnit> scratchLeaves = new List<LeaveUnit>();
    private readonly List<AttackEvent> scratchAttacks = new List<AttackEvent>();
    private readonly List<int2> scratchTiles = new List<int2>();

    /// <summary>Diagnostics (-perfBreakdown): bytes and units per message type in one bot's stream.</summary>
    private void CountTypes(ArraySegment<byte> segment)
    {
        if (startTick < 0) return;
        var reader = new NetworkReader(segment);
        while (reader.Remaining > 0)
        {
            int before = reader.Position;
            MessageType type = Messages.PeekType(reader);
            int units;
            scratchTiles.Clear();
            switch (type)
            {
                case MessageType.Enter: scratchEnters.Clear(); Messages.DecodeEnter(reader, scratchEnters, scratchTiles); units = scratchEnters.Count; break;
                case MessageType.MoveOrder: scratchRoutes.Clear(); Messages.DecodeMoveOrder(reader, scratchRoutes, scratchTiles); units = scratchRoutes.Count; break;
                case MessageType.Correction: scratchCorrections.Clear(); Messages.DecodeCorrection(reader, scratchCorrections, out _); units = scratchCorrections.Count; break;
                case MessageType.Health: scratchHealth.Clear(); Messages.DecodeHealth(reader, scratchHealth); units = scratchHealth.Count; break;
                case MessageType.Leave: scratchLeaves.Clear(); Messages.DecodeLeave(reader, scratchLeaves); units = scratchLeaves.Count; break;
                case MessageType.Attack: scratchAttacks.Clear(); Messages.DecodeAttack(reader, scratchAttacks); units = scratchAttacks.Count; break;
                default: return;
            }
            typeBytes[(int)type] += reader.Position - before;
            typeUnits[(int)type] += units;
        }
    }

    /// <summary>Spawns a unit for army <paramref name="army"/> on each of <paramref name="tiles"/> (up to <paramref name="count"/>).</summary>
    private int Spawn(WorldStateManager world, int army, int count, IEnumerable<int2> tiles)
    {
        int placed = 0;
        foreach (int2 tile in tiles)
        {
            if (placed >= count) break;
            pendingSpawns[army]++;
            world.DevSpawnUnit(owners[army], (float2)tile + 0.5f, id =>
            {
                pendingSpawns[army]--;
                if (id > 0) armyIds[army].Add(id);
            });
            placed++;
        }
        return placed;
    }

    /// <summary>Keeps every army at strength while the battle kills it.</summary>
    private void TopUp()
    {
        WorldStateManager world = WorldStateManager.Instance;
        if (world == null) return;
        for (int i = 0; i < Owners; i++)
        {
            int missing = UnitsPerOwner - armyIds[i].Count - pendingSpawns[i];
            if (missing <= 0) continue;
            List<int2> ring = reinforcementTiles[i];
            int n = math.min(missing, MaxTopUpPerOwner);
            var tiles = new List<int2>(n);
            for (int k = 0; k < n && ring.Count > 0; k++) tiles.Add(ring[reinforcementCursor[i]++ % ring.Count]);
            Spawn(world, i, n, tiles);
        }
    }

    private void Finish()
    {
        string dir = ArgValue("-perfOut") ?? Path.Combine(Application.persistentDataPath, "PerfResults");
        string path = Path.Combine(dir, "perf.csv");
        stats.WriteCsv(path, new Dictionary<string, (string, double, bool)>
        {
            ["perf.tick.main"] = ("p95", 25.0, true),
            ["perf.fps"] = ("mean", 60.0, false),
            ["perf.bw.avg"] = ("mean", 262144.0, true),
            ["perf.bw.peak1s"] = ("max", 786432.0, true),
        });
        WarLog.Info(LogCategory.Dev, $"wrote {path}");
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-perfBreakdown") >= 0)
        {
            var lines = new System.Text.StringBuilder("type,bytes,units\n");
            for (int t = 1; t < typeBytes.Length; t++) lines.AppendLine($"{(MessageType)t},{typeBytes[t]},{typeUnits[t]}");
            File.WriteAllText(Path.Combine(dir, "breakdown.csv"), lines.ToString());
        }
        enabled = false;
        Application.Quit();
    }

    /// <summary>
    /// The perf layout's eight sites, ordered so that sites (0,1), (2,3), (4,5) and (6,7) are front partners: the
    /// generated map's start sites are paired nearest-first (a fixed rule on the seeded map, so the gate's layout is stable).
    /// </summary>
    internal static int2[] PairedSites(int2[] all)
    {
        var free = new List<int2>(all);
        var result = new List<int2>();
        while (result.Count < Owners && free.Count >= 2)
        {
            int bi = 0, bj = 1;
            float best = float.MaxValue;
            for (int i = 0; i < free.Count; i++)
            for (int j = i + 1; j < free.Count; j++)
            {
                float d = math.distancesq(free[i], free[j]);
                if (d < best) { best = d; bi = i; bj = j; }
            }
            result.Add(free[bi]);
            result.Add(free[bj]);
            free.RemoveAt(bj);
            free.RemoveAt(bi);
        }
        return result.ToArray();
    }

    private static bool PlaceNear(WorldStateManager world, int owner, int2 site)
    {
        for (int ring = 0; ring < 40; ring++)
        for (int i = 0; i < math.max(1, 8 * ring); i++)
            if (world.DevPlaceBuilding(owner, BuildingType.Base, RingTile(site, ring, i), 1e7f)) return true;
        return false;
    }

    private static IEnumerable<int2> WalkableSpiral(WorldStateManager world, int2 origin, int count)
    {
        int found = 0;
        for (int ring = 0; found < count && ring < 300; ring++)
        for (int i = 0; i < math.max(1, 8 * ring) && found < count; i++)
        {
            int2 tile = RingTile(origin, ring, i);
            if (!world.Map.Grid.IsWalkable(tile)) continue;
            found++;
            yield return tile;
        }
    }

    private static int2 RingTile(int2 origin, int ring, int i)
    {
        if (ring == 0) return origin;
        int side = 2 * ring;
        if (i < side) return new int2(origin.x - ring + i, origin.y - ring);
        i -= side;
        if (i < side) return new int2(origin.x + ring, origin.y - ring + i);
        i -= side;
        if (i < side) return new int2(origin.x + ring - i, origin.y + ring);
        i -= side;
        return new int2(origin.x - ring, origin.y + ring - i);
    }

    internal static string ArgValue(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void Fail(string reason)
    {
        WarLog.Error(LogCategory.Dev, $"{reason}");
        Application.Quit(1);
    }
}

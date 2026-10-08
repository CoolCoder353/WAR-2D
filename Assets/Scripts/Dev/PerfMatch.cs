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
    private int[] owners;
    /// <summary>Owners before this index are real players (the host and -perfClients); from it on, bots.</summary>
    private int firstBot = 1;
    private int2[] armyStart;
    private int lastTick = -1, startTick = -1;
    private float secondTimer;

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

        int2[] sites = world.Map.HqSites;
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
        Debug.Log("[Perf] match ready; sampling");
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
                secondTimer = 0f;
            }
        }

        if (tick == lastTick) return;
        lastTick = tick;
        if (tick % OrderEveryTicks == 0) IssueOrders(sim, tick);
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
            sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.MoveUnits, OwnerId = owners[i], Tile = armyStart[i ^ 1], Ids = picked.ToArray() });
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
        Debug.Log($"[Perf] wrote {path}");
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-perfBreakdown") >= 0)
        {
            var lines = new System.Text.StringBuilder("type,bytes,units\n");
            for (int t = 1; t < typeBytes.Length; t++) lines.AppendLine($"{(MessageType)t},{typeBytes[t]},{typeUnits[t]}");
            File.WriteAllText(Path.Combine(dir, "breakdown.csv"), lines.ToString());
        }
        enabled = false;
        Application.Quit();
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
        Debug.LogError($"[Perf] {reason}");
        Application.Quit(1);
    }
}

using System.Collections;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.TestTools;
using WAR2D.Client;
using WAR2D.Net.Replication;
using WAR2D.Sim;

/// <summary>Units reach the host's client through the replication pipeline, and only what it may see.</summary>
public class ReplicationPlayModeTests
{
    private GameConfigData config;
    private ClientUnitStore store;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        store = new ClientUnitStore(config.Simulation.MaxEntities, config.Simulation.TickSeconds);
        ReplicationClient.Received += OnBatch;
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        ReplicationClient.Received -= OnBatch;
        yield return PlayModeMatch.TearDown();
        store.Dispose();
    }

    private void OnBatch(ReplicationBatch batch) => Assert.IsTrue(store.Apply(batch.Payload, batch.Tick), "malformed batch from the server");

    private static void Spawn(int owner, float2 position, System.Action<int> onSpawned) =>
        SimCommandQueue.Instance.Enqueue(new SimCommand { Kind = SimCommandKind.SpawnUnit, OwnerId = owner, Position = position, UnitType = UnitType.Tank, OnSpawned = onSpawned });

    [UnityTest, Timeout(90000)]
    public IEnumerator HostClientSeesItsSpawnedUnit()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        Assert.That(WorldStateManager.Instance.TryFindSpawnTile(hq, out int2 tile), Is.True);
        int id = -1;
        Spawn(PlayModeMatch.LocalOwner, (float2)tile + 0.5f, v => id = v);
        yield return PlayModeMatch.WaitUntil(() => store.IsKnown(NetIdAllocator.IndexOf(math.max(id, 0))) && id > 0, 5f);
        Assert.That(ReplicationService.Instance.LastEnterCount(NetworkServer.localConnection), Is.GreaterThanOrEqualTo(1));
        Assert.That(store.IdOf(NetIdAllocator.IndexOf(id)), Is.EqualTo(id));
        Assert.That(store.OwnerOf(NetIdAllocator.IndexOf(id)), Is.EqualTo(PlayModeMatch.LocalOwner));
        yield return PlayModeMatch.WaitUntil(() => ClientWorld.Instance != null && ClientWorld.Instance.KnownCount >= 1, 5f);
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator AssigningAnEmptySelectionEmptiesTheSquad()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        Assert.That(WorldStateManager.Instance.TryFindSpawnTile(hq, out int2 tile), Is.True);
        var ids = new System.Collections.Generic.List<int>();
        for (int i = 0; i < 3; i++) Spawn(PlayModeMatch.LocalOwner, (float2)tile + 0.5f, v => ids.Add(v));
        yield return PlayModeMatch.WaitUntil(() => ids.Count == 3 && ClientWorld.Instance != null && ClientWorld.Instance.KnownCount >= 3, 10f);

        UnitCommander.Instance.Selection.SelectIds(ids);
        UnitCommander.Instance.Selection.AssignSquad(2);
        yield return PlayModeMatch.WaitUntil(() => WorldStateManager.Instance.Squads.Members(PlayModeMatch.LocalOwner, 2).Count == 3, 5f);

        // Ctrl+3 with nothing selected empties the squad here and on the server alike.
        UnitCommander.Instance.Selection.SelectIds(new int[0]);
        UnitCommander.Instance.Selection.AssignSquad(2);
        yield return PlayModeMatch.WaitUntil(() => WorldStateManager.Instance.Squads.Members(PlayModeMatch.LocalOwner, 2).Count == 0, 5f);
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator MatchEndWipeReachesTheClient()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        Assert.That(WorldStateManager.Instance.TryFindSpawnTile(hq, out int2 tile), Is.True);
        int id = -1;
        Spawn(PlayModeMatch.LocalOwner, (float2)tile + 0.5f, v => id = v);
        yield return PlayModeMatch.WaitUntil(() => id > 0 && ClientWorld.Instance != null && ClientWorld.Instance.IsKnownId(id), 5f);

        // The match ends with the unit alive: the wipe after GameOver must still reach the client (a Leave),
        // instead of leaving it predicted on its route behind the end screen.
        GameCore.Instance.DeclareDraw();
        yield return PlayModeMatch.WaitUntil(() => !ClientWorld.Instance.IsKnownId(id) && !store.IsKnown(NetIdAllocator.IndexOf(id)), 5f);
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator UnitDrawsWhereServerHasIt()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        Assert.That(WorldStateManager.Instance.TryFindSpawnTile(hq, out int2 tile), Is.True);
        int id = -1;
        Spawn(PlayModeMatch.LocalOwner, (float2)tile + 0.5f, v => id = v);
        yield return PlayModeMatch.WaitUntil(() => id > 0 && ClientWorld.Instance != null && ClientWorld.Instance.TryGet(id, out _), 5f);

        // Order it to the walkable tile farthest from the HQ within a short search, then let it travel.
        var grid = WorldStateManager.Instance.Map.Grid;
        int2 goal = tile;
        for (int r = 12; r > 2 && goal.Equals(tile); r--)
            foreach (int2 d in new[] { new int2(r, 0), new int2(-r, 0), new int2(0, r), new int2(0, -r) })
                if (grid.IsWalkable(tile + d)) { goal = tile + d; break; }
        SimCommandQueue.Instance.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, OwnerId = PlayModeMatch.LocalOwner, Tile = goal, Ids = new[] { id } });

        float end = UnityEngine.Time.realtimeSinceStartup + 3f, nextCheck = 0f;
        for (int frame = 0; UnityEngine.Time.realtimeSinceStartup < end; frame++)
        {
            yield return null;
            if (UnityEngine.Time.realtimeSinceStartup < nextCheck) continue;
            nextCheck = UnityEngine.Time.realtimeSinceStartup + 0.2f;
            Assert.IsTrue(ClientWorld.Instance.TryGet(id, out ClientUnitView view));
            float2 server = ServerPosition(id);
            Assert.That(math.distance(view.Position, server), Is.LessThan(1f), $"frame {frame}: client {view.Position}, server {server}");
        }
        Assert.That(math.distance(ServerPosition(id), (float2)tile + 0.5f), Is.GreaterThan(1f), "the unit should have moved");
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator BoxSelectAndSquadOrderMoveUnits()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        Assert.That(WorldStateManager.Instance.TryFindSpawnTile(hq, out int2 tile), Is.True);
        var ids = new System.Collections.Generic.List<int>();
        for (int i = 0; i < 30; i++) Spawn(PlayModeMatch.LocalOwner, (float2)tile + 0.5f, v => ids.Add(v));
        yield return PlayModeMatch.WaitUntil(() => ids.Count == 30 && ClientWorld.Instance != null && ClientWorld.Instance.KnownCount >= 30, 10f);
        Assert.That(ids, Has.None.LessThan(0));

        // A goal ~10 tiles away whose 7x7 neighbourhood is open ground.
        var grid = WorldStateManager.Instance.Map.Grid;
        int2 goal = tile;
        float best = float.MaxValue;
        for (int y = 3; y < grid.Height - 3; y++)
        for (int x = 3; x < grid.Width - 3; x++)
        {
            float score = math.abs(math.distance(new float2(x, y), tile) - 10f);
            if (score >= best) continue;
            bool open = true;
            for (int dy = -3; dy <= 3 && open; dy++)
            for (int dx = -3; dx <= 3 && open; dx++) open = grid.IsWalkable(new int2(x + dx, y + dy));
            if (open) { best = score; goal = new int2(x, y); }
        }
        Assert.That(best, Is.LessThan(6f), "Map_2 should have an open area near the HQ");

        UnitCommander.Instance.Selection.SelectIds(ids);
        UnitCommander.Instance.Selection.AssignSquad(3);
        yield return PlayModeMatch.WaitUntil(() => WorldStateManager.Instance.Squads.Members(PlayModeMatch.LocalOwner, 3).Count == 30, 5f);
        UnitCommander.Instance.Selection.OrderMove(goal); // the selection is exactly squad 3: a squad order

        var goalCentre = (float2)goal + 0.5f;
        float end = UnityEngine.Time.realtimeSinceStartup + 15f;
        int nearCount = 0;
        while (UnityEngine.Time.realtimeSinceStartup < end)
        {
            nearCount = 0;
            foreach (int id in ids) if (math.distance(ServerPosition(id), goalCentre) <= 3f) nearCount++;
            if (nearCount >= 25) yield break;
            yield return null;
        }
        var sample = new System.Text.StringBuilder();
        for (int i = 0; i < 30; i += 5) sample.Append(ServerPosition(ids[i])).Append(' ');
        Assert.Fail($"only {nearCount}/30 reached {goalCentre} from {tile}; sample {sample}; orders live {WAR2D.Sim.SimContext.Current.Orders.LiveCount}");
    }

    private static float2 ServerPosition(int id)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(Unit));
        using var units = q.ToComponentDataArray<Unit>(Unity.Collections.Allocator.Temp);
        foreach (Unit u in units) if (u.Id == id) return u.Position;
        Assert.Fail($"unit {id} not found on the server");
        return default;
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator EnemyOutsideViewIsNeverSent()
    {
        config.Match.Map.Size = 256;
        config.Match.Map.Seed = 5;
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        // The host camera's box is a few tens of tiles; the clearing opposite the host's own (where its HQ
        // must stand) is ~195 tiles away across the map.
        WorldStateManager.Instance.UpdateClientView(new int2(0, 0), new int2(40, 40));
        int2[] sites = WorldStateManager.Instance.Map.HqSites;
        int own = NetworkClient.localPlayer.GetComponent<ClientPlayer>().startSite;
        Assert.That(own, Is.GreaterThanOrEqualTo(0), "the host was given a clearing at the start");
        int2 far = sites[(own + sites.Length / 2) % sites.Length];
        int enemy = -1;
        Spawn(424242, (float2)far + 0.5f, v => enemy = v);
        yield return PlayModeMatch.WaitUntil(() => enemy > 0, 5f);
        float end = UnityEngine.Time.realtimeSinceStartup + 2f;
        while (UnityEngine.Time.realtimeSinceStartup < end)
        {
            Assert.IsFalse(store.IsKnown(NetIdAllocator.IndexOf(enemy)), "an enemy outside the view reached the client");
            yield return null;
        }
    }
}

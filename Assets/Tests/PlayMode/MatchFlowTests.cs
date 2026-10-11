using System;
using System.Collections;
using System.Collections.Generic;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class MatchFlowTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown() => PlayModeMatch.TearDown();

    [UnityTest, Timeout(90000)]
    public IEnumerator Host_PlacesHQ_ReachesPlaying()
    {
        yield return StartMatchAsHost();
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();

        Assert.That(WorldStateManager.Instance.TryFindBuildableAnchor(BuildingType.Base, 0f, local, out int2 anchor), Is.True);
        WorldStateManager.Instance.TryAddBuilding(anchor, BuildingType.Base, 0f);

        yield return WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 10f);
        Assert.That(local.hasPlacedHQ, Is.True);
        Assert.That(GameCore.Instance.MatchStartPlayerCount, Is.EqualTo(1));
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator Spawner_ProducesUnit_OnFreeTileOutsideFootprint()
    {
        yield return StartMatchAsHost();
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        WorldStateManager wsm = WorldStateManager.Instance;

        wsm.TryFindBuildableAnchor(BuildingType.Base, 0f, local, out int2 hq);
        wsm.TryAddBuilding(hq, BuildingType.Base, 0f);
        yield return WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 10f);

        Assert.That(wsm.TryFindBuildableAnchor(BuildingType.SmallUnitSpawner, 0f, local, out int2 spawnerAnchor), Is.True);
        wsm.TryAddBuilding(spawnerAnchor, BuildingType.SmallUnitSpawner, 0f);
        // Host-local Commands travel through Mirror's loopback, which the server drains once per
        // tick (sendInterval), not once per frame - one frame is not enough for the entity to exist.
        yield return WaitUntil(() => HasBuilding(BuildingType.SmallUnitSpawner), 10f);

        int spawnerId = FindBuildingId(BuildingType.SmallUnitSpawner);
        wsm.BuildingClicked(spawnerId);

        yield return WaitUntil(() => CountUnits() == 1, 10f);
        var footprint = Footprint.Tiles(spawnerAnchor, config.GetBuilding(BuildingType.SmallUnitSpawner).Size);
        float2 unitPos = FirstUnitPosition();
        int2 unitTile = (int2)math.floor(unitPos);
        Assert.That(footprint, Has.No.Member(unitTile), "the unit spawns outside the footprint");
        Assert.That(wsm.Map.Grid.IsWalkable(unitTile), Is.True, "the unit spawns on walkable ground");
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator SpawnerQueueStepsAndRallyPoint()
    {
        yield return StartMatchAsHost();
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        WorldStateManager wsm = WorldStateManager.Instance;
        wsm.TryFindBuildableAnchor(BuildingType.Base, 0f, local, out int2 hq);
        wsm.TryAddBuilding(hq, BuildingType.Base, 0f);
        yield return WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 10f);
        Assert.That(wsm.TryFindBuildableAnchor(BuildingType.SmallUnitSpawner, 0f, local, out int2 spawnerAnchor), Is.True);
        wsm.TryAddBuilding(spawnerAnchor, BuildingType.SmallUnitSpawner, 0f);
        yield return WaitUntil(() => HasBuilding(BuildingType.SmallUnitSpawner), 10f);
        int spawner = FindBuildingId(BuildingType.SmallUnitSpawner);

        // A rally point on open floor 6–12 tiles away, set before any unit spawns.
        int2 rally = default;
        bool found = false;
        for (int r = 6; r <= 12 && !found; r++)
        for (int dx = -r; dx <= r && !found; dx++)
        {
            int2 t = spawnerAnchor + new int2(dx, r);
            if (wsm.Map.Grid.IsWalkable(t)) { rally = t; found = true; }
        }
        Assert.That(found, Is.True, "open floor near the spawner");
        wsm.CmdSetRally(new[] { spawner }, rally, false);
        yield return WaitUntil(() => local.TryGetRally(spawner, out _), 5f);
        Assert.That(local.TryGetRally(spawner, out int2 told) && told.Equals(rally), Is.True, "the owner is told the rally tile");

        wsm.CmdChangeQueues(new[] { spawner }, 7); // not a button's step: refused
        wsm.CmdChangeQueues(new[] { spawner }, 10);
        yield return WaitUntil(() => local.SpawnerQueue(spawner) >= 8, 5f);
        Assert.That(local.SpawnerQueue(spawner), Is.InRange(8, 10), "+10 queued (minus any already spawned), the 7 was refused");

        float2 goal = (float2)rally + 0.5f;
        yield return WaitUntil(() => CountUnits() > 0 && math.distance(FirstUnitPosition(), goal) < 3f, 20f);
        Assert.That(math.distance(FirstUnitPosition(), goal), Is.LessThan(3f), "new units drive to the rally point");
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator DestroyingOnlyHQ_InSoloMatch_EndsInDraw()
    {
        yield return StartMatchAsHost();
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        WorldStateManager.Instance.TryFindBuildableAnchor(BuildingType.Base, 0f, local, out int2 hq);
        WorldStateManager.Instance.TryAddBuilding(hq, BuildingType.Base, 0f);
        yield return WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 10f);

        WorldStateManager.Instance.KillAllEntitiesOwnedBy((int)NetworkClient.localPlayer.netId);

        yield return WaitUntil(() => GameCore.Instance.CurrentState == GameState.GameOver, 10f);
        Assert.That(NetworkClient.localPlayer.GetComponent<ClientPlayer>().drawDeclared, Is.True, "draw screen RPC must reach the client");
        Assert.That(CountUnits(), Is.EqualTo(0));
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator GeneratedMap_ClientHashMatches()
    {
        config.Match.Map.Size = 256;
        config.Match.Map.Seed = 5;
        yield return StartMatchAsHost();
        WorldStateManager wsm = WorldStateManager.Instance;
        Assert.That(wsm.Map, Is.Not.Null);
        Assert.That(wsm.MapSize, Is.EqualTo(256));
        Assert.That(wsm.MapSeed, Is.EqualTo(5u));
        Assert.That(wsm.Map.Hash(), Is.EqualTo(wsm.MapHash));
        Assert.That(wsm.Map.HqSites.Length, Is.EqualTo(8));
    }

    // ---------- helpers ----------

    private IEnumerator StartMatchAsHost() => PlayModeMatch.StartMatchAsHost(config);

    private static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds) => PlayModeMatch.WaitUntil(condition, timeoutSeconds);

    private static EntityManager Em => World.DefaultGameObjectInjectionWorld.EntityManager;

    private static int CountUnits()
    {
        using var q = Em.CreateEntityQuery(typeof(WAR2D.Sim.Unit));
        return q.CalculateEntityCount();
    }

    private static float2 FirstUnitPosition()
    {
        using var q = Em.CreateEntityQuery(typeof(WAR2D.Sim.Unit));
        using var units = q.ToComponentDataArray<WAR2D.Sim.Unit>(Unity.Collections.Allocator.Temp);
        return units[0].Position;
    }

    private static bool HasBuilding(BuildingType type)
    {
        using var q = Em.CreateEntityQuery(typeof(BuildingData));
        using var data = q.ToComponentDataArray<BuildingData>(Unity.Collections.Allocator.Temp);
        foreach (BuildingData b in data) if (b.buildingType == type) return true;
        return false;
    }

    private static int FindBuildingId(BuildingType type)
    {
        using var q = Em.CreateEntityQuery(typeof(BuildingData));
        using var data = q.ToComponentDataArray<BuildingData>(Unity.Collections.Allocator.Temp);
        foreach (BuildingData b in data) if (b.buildingType == type) return b.id;
        Assert.Fail($"No {type} found");
        return -1;
    }
}

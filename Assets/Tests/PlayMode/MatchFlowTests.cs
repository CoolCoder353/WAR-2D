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
        ConfigLoader.ResetForTests();
        config = ConfigLoader.LoadConfig();
        Assert.That(ConfigLoader.IsValid, Is.True, string.Join("\n", ConfigLoader.Errors));
        config.Match.CountdownSeconds = 0.5f;
        config.Resources.StartingResources = 10000f;

        SceneManager.LoadScene("Main_Menu");
        yield return null;
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (NetworkServer.active || NetworkClient.active) GameManager.Instance.StopHost();
        yield return WaitUntil(() => !NetworkServer.active && !NetworkClient.active, 10f);
        foreach (GameObject root in DontDestroyRoots()) UnityEngine.Object.Destroy(root);
        ConfigLoader.ResetForTests();
        yield return null;
    }

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
        float3 unitPos = FirstUnitPosition();
        Assert.That(footprint, Has.No.Member(new int2((int)math.round(unitPos.x), (int)math.round(unitPos.y))));
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

    // ---------- helpers ----------

    private IEnumerator StartMatchAsHost()
    {
        GameManager.Instance.HostServer();
        yield return WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        Assert.That(GameCore.Instance.ServerPlayers.Count, Is.EqualTo(1));

        GameCore.Instance.Cmd_StartGame();
        yield return WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        Assert.That(GameCore.Instance.CurrentState, Is.EqualTo(GameState.PlacingHQ));
    }

    private static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds)
    {
        float end = Time.realtimeSinceStartup + timeoutSeconds;
        while (!condition())
        {
            if (Time.realtimeSinceStartup > end) Assert.Fail($"Timed out after {timeoutSeconds}s");
            yield return null;
        }
    }

    private static EntityManager Em => World.DefaultGameObjectInjectionWorld.EntityManager;

    private static int CountUnits()
    {
        using var q = Em.CreateEntityQuery(typeof(ClientUnit));
        return q.CalculateEntityCount();
    }

    private static float3 FirstUnitPosition()
    {
        using var q = Em.CreateEntityQuery(typeof(ClientUnit), typeof(Unity.Transforms.LocalTransform));
        using var transforms = q.ToComponentDataArray<Unity.Transforms.LocalTransform>(Unity.Collections.Allocator.Temp);
        return transforms[0].Position;
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

    private static IEnumerable<GameObject> DontDestroyRoots()
    {
        var probe = new GameObject("probe");
        UnityEngine.Object.DontDestroyOnLoad(probe);
        Scene dd = probe.scene;
        UnityEngine.Object.DestroyImmediate(probe);
        foreach (GameObject root in dd.GetRootGameObjects())
        {
            // Unity.Entities parks a hidden DefaultWorldInitializationProxy in the DontDestroyOnLoad
            // scene. Destroying it runs DomainUnloadOrPlayModeChangeShutdown(), which disposes the ECS
            // default world for the rest of the play-mode session - every later test would then fail
            // to spawn WorldStateManager. It is engine infrastructure, not match state: leave it up.
            if (HasEntitiesWorldProxy(root)) continue;
            yield return root;
        }
    }

    private static bool HasEntitiesWorldProxy(GameObject root)
    {
        foreach (Component component in root.GetComponents<Component>())
        {
            if (component != null && component.GetType().Name == "DefaultWorldInitializationProxy") return true;
        }
        return false;
    }
}

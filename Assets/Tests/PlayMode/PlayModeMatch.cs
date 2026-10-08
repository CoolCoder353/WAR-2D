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

/// <summary>Shared PlayMode helpers: config for tests, hosting a match, placing the HQ, and cleanup.</summary>
public static class PlayModeMatch
{
    /// <summary>Reloads the config with fast test settings (Map_2, short countdown, rich start).</summary>
    public static GameConfigData Configure()
    {
        ConfigLoader.ResetForTests();
        GameConfigData config = ConfigLoader.LoadConfig();
        Assert.That(ConfigLoader.IsValid, Is.True, string.Join("\n", ConfigLoader.Errors));
        config.Match.CountdownSeconds = 0.5f;
        config.Resources.StartingResources = 10000f;
        config.Match.Map.Size = 0; // Map_2's authored tilemaps unless a test asks for a generated map
        return config;
    }

    public static IEnumerator LoadMenu()
    {
        SceneManager.LoadScene("Main_Menu");
        yield return null;
        yield return null;
    }

    public static IEnumerator TearDown()
    {
        if (NetworkServer.active || NetworkClient.active) GameManager.Instance.StopHost();
        yield return WaitUntil(() => !NetworkServer.active && !NetworkClient.active, 10f);
        foreach (GameObject root in DontDestroyRoots()) UnityEngine.Object.Destroy(root);
        ConfigLoader.ResetForTests();
        yield return null;
    }

    public static IEnumerator StartMatchAsHost(GameConfigData config)
    {
        GameManager.Instance.HostServer();
        yield return WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        Assert.That(GameCore.Instance.ServerPlayers.Count, Is.EqualTo(1));

        GameCore.Instance.Cmd_StartGame();
        yield return WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        Assert.That(GameCore.Instance.CurrentState, Is.EqualTo(GameState.PlacingHQ));
    }

    /// <summary>Places the local player's HQ and waits for Playing; returns the anchor.</summary>
    public static IEnumerator PlaceHQ(Action<int2> anchor)
    {
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        Assert.That(WorldStateManager.Instance.TryFindBuildableAnchor(BuildingType.Base, 0f, local, out int2 hq), Is.True);
        WorldStateManager.Instance.TryAddBuilding(hq, BuildingType.Base, 0f);
        yield return WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 10f);
        anchor?.Invoke(hq);
    }

    /// <summary>The local player's owner id.</summary>
    public static int LocalOwner => BuildingData.UIntToInt(NetworkClient.localPlayer.netId);

    public static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds)
    {
        float end = Time.realtimeSinceStartup + timeoutSeconds;
        while (!condition())
        {
            if (Time.realtimeSinceStartup > end) Assert.Fail($"Timed out after {timeoutSeconds}s");
            yield return null;
        }
    }

    public static EntityManager Em => World.DefaultGameObjectInjectionWorld.EntityManager;

    public static IEnumerable<GameObject> DontDestroyRoots()
    {
        var probe = new GameObject("probe");
        UnityEngine.Object.DontDestroyOnLoad(probe);
        Scene dd = probe.scene;
        UnityEngine.Object.DestroyImmediate(probe);
        foreach (GameObject root in dd.GetRootGameObjects())
        {
            // Unity.Entities parks a hidden DefaultWorldInitializationProxy in the DontDestroyOnLoad
            // scene. Destroying it disposes the ECS default world for the rest of the play-mode session,
            // so every later test would fail to spawn WorldStateManager. Leave it up.
            if (HasEntitiesWorldProxy(root)) continue;
            yield return root;
        }
    }

    private static bool HasEntitiesWorldProxy(GameObject root)
    {
        foreach (Component component in root.GetComponents<Component>())
            if (component != null && component.GetType().Name == "DefaultWorldInitializationProxy") return true;
        return false;
    }
}

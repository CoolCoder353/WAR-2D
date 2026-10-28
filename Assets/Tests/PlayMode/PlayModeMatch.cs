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
        config.Replays.Enabled = false; // hosted tests would fill the real Replays folder; the replay tests turn it on
        return config;
    }

    /// <summary>
    /// Makes every frame advance exactly <paramref name="ticksPerFrame"/> sim ticks of game time, however long the
    /// frame took in real time (<c>Time.captureDeltaTime</c>). Tests then see the same number of ticks per frame on
    /// every run and machine, and run as fast as the frames render instead of waiting on the wall clock.
    /// </summary>
    public static void FastForward(int ticksPerFrame)
    {
        float tick = ConfigLoader.LoadConfig().Simulation.TickSeconds;
        Time.maximumDeltaTime = math.max(1f / 3f, tick * ticksPerFrame * 1.5f);
        Time.captureDeltaTime = tick * ticksPerFrame;
    }

    public static IEnumerator LoadMenu()
    {
        SceneManager.LoadScene("Main_Menu");
        yield return null;
        yield return null;
    }

    public static IEnumerator TearDown()
    {
        Time.captureDeltaTime = 0f;
        Time.timeScale = 1f;
        Time.maximumDeltaTime = 1f / 3f;
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

        // A solo host may start without readying up or a second team. The host's own Command runs on a
        // later server update, so the bypass stays on until the match scene is up.
        bool devAllowed = DevApi.AllowForTests;
        DevApi.AllowForTests = true;
        GameCore.Instance.Cmd_StartGame();
        yield return WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        DevApi.AllowForTests = devAllowed;
        Assert.That(GameCore.Instance.CurrentState, Is.EqualTo(GameState.PlacingHQ));
    }

    /// <summary>Places the local player's HQ and waits for Playing; returns the anchor.</summary>
    public static IEnumerator PlaceHQ(Action<int2> anchor)
    {
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        Assert.That(WorldStateManager.Instance.TryFindBuildableAnchor(BuildingType.Base, 0f, local, out int2 hq), Is.True);
        WorldStateManager.Instance.TryAddBuilding(hq, BuildingType.Base, 0f);
        yield return WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 10f);
        yield return WaitForSight(hq);
        anchor?.Invoke(hq);
    }

    /// <summary>
    /// Waits for the first vision pass to explore the HQ's ground: buildings may only be placed where the
    /// player's side has explored.
    /// </summary>
    public static IEnumerator WaitForSight(int2 hq) =>
        WaitUntil(() => WorldStateManager.Instance != null && WorldStateManager.Instance.HasExplored(LocalOwner, hq), 10f);

    /// <summary>The local player's owner id.</summary>
    public static int LocalOwner => BuildingData.UIntToInt(NetworkClient.localPlayer.netId);

    public static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds, Func<string> describe = null)
    {
        float end = Time.realtimeSinceStartup + timeoutSeconds;
        while (!condition())
        {
            if (Time.realtimeSinceStartup > end) Assert.Fail($"Timed out after {timeoutSeconds}s" + (describe != null ? ": " + describe() : ""));
            yield return null;
        }
    }

    /// <summary>Places a finished building for the local player (a normal placement is a blueprint until Builders finish it).</summary>
    public static bool PlaceBuilt(BuildingType type, int2 anchor, float rotation = 0f)
    {
        DevApi.AllowForTests = true;
        try { return WorldStateManager.Instance.DevPlaceBuilding(LocalOwner, type, anchor, 0f, rotation); }
        finally { DevApi.AllowForTests = false; }
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

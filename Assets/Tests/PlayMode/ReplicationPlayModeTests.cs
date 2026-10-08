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
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator EnemyOutsideViewIsNeverSent()
    {
        config.Match.Map.Size = 256;
        config.Match.Map.Seed = 5;
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        // The host camera's box is a few tens of tiles; the far HQ clearing is ~190 tiles away from the near one.
        WorldStateManager.Instance.UpdateClientView(new int2(0, 0), new int2(40, 40));
        int2 far = WorldStateManager.Instance.Map.HqSites[0];
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

using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using WAR2D.Client;
using WAR2D.Net.Replication;

/// <summary>The client world's intake and clock.</summary>
public class ClientWorldTests
{
    private GameObject go;
    private ClientWorld world;

    [SetUp]
    public void SetUp()
    {
        go = new GameObject("ClientWorldTest");
        world = go.AddComponent<ClientWorld>();
        world.Initialise();
    }

    [TearDown]
    public void TearDown() => UnityEngine.Object.DestroyImmediate(go);

    private static ReplicationBatch EnterBatch(int tick, int id, float2 position)
    {
        var bytes = new NativeList<byte>(64, Allocator.Temp);
        var sink = new NativeSink(bytes);
        var tiles = new NativeArray<int2>(new[] { (int2)math.round(position) }, Allocator.Temp);
        var units = new NativeArray<EnterUnit>(new[]
        {
            new EnterUnit { Id = id, Type = (byte)UnitType.Tank, OwnerId = 9, X = Quantise.Encode(position.x), Y = Quantise.Encode(position.y), Health = 100, First = 0, Count = 1 },
        }, Allocator.Temp);
        Messages.EncodeEnter(ref sink, tiles, units);
        var batch = new ReplicationBatch { Tick = tick, Payload = new ArraySegment<byte>(bytes.AsArray().ToArray()) };
        bytes.Dispose(); tiles.Dispose(); units.Dispose();
        return batch;
    }

    [Test]
    public void EnterMakesTheUnitKnownWhereTheServerHasIt()
    {
        int id = (1 << NetIdAllocator.IndexBits) | 12;
        world.Apply(EnterBatch(40, id, new float2(10.25f, 20.5f)));
        Assert.AreEqual(1, world.KnownCount);
        Assert.IsTrue(world.TryGet(id, out ClientUnitView view));
        Assert.AreEqual(9, view.OwnerId);
        Assert.AreEqual(new float2(10.25f, 20.5f), view.Position);
        Assert.AreEqual(40 * 0.05f, world.ServerTime, 1e-5f, "the first batch sets the clock");
    }

    [Test]
    public void MalformedBatchIsDroppedNotThrown()
    {
        LogAssert.ignoreFailingMessages = true;
        var rng = new System.Random(3);
        for (int i = 0; i < 1000; i++)
        {
            var bytes = new byte[rng.Next(1, 48)];
            rng.NextBytes(bytes);
            Assert.DoesNotThrow(() => world.Apply(new ReplicationBatch { Tick = i, Payload = new ArraySegment<byte>(bytes) }));
        }
        Assert.Greater(world.MalformedBatches, 0);
    }

    [Test]
    public void ClockNeverRunsBackward()
    {
        world.Apply(EnterBatch(100, (1 << NetIdAllocator.IndexBits) | 1, new float2(5, 5)));
        float previous = world.ServerTime;
        world.Apply(new ReplicationBatch { Tick = 50, Payload = new ArraySegment<byte>(Array.Empty<byte>()) });
        for (int i = 0; i < 100; i++)
        {
            world.AdvanceClock(0.016f);
            Assert.GreaterOrEqual(world.ServerTime, previous);
            previous = world.ServerTime;
        }
    }
}

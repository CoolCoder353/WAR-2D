using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Client;
using WAR2D.Net.Replication;

/// <summary>
/// What the client draws between the server's answers: orders at once, corrections without a step back, a steady clock.
/// All of it drawing only: <see cref="ClientUnitStore.Predicted"/> stays what the server knows.
/// </summary>
public class ClientSmoothingTests
{
    private const float Dt = 0.05f, Speed = 5f;
    private static readonly int Id = (1 << NetIdAllocator.IndexBits) | 7;
    private const int Index = 7;

    private ClientUnitStore store;

    [SetUp]
    public void SetUp()
    {
        store = new ClientUnitStore(32, Dt);
        Send(1, (ref NativeSink sink) =>
        {
            var tiles = new NativeArray<int2>(new[] { RouteWire.Encode(new float2(10, 10)) }, Allocator.Temp);
            var units = new NativeArray<EnterUnit>(new[]
            {
                new EnterUnit { Id = Id, Type = (byte)UnitType.Tank, OwnerId = 9, X = Quantise.Encode(10f), Y = Quantise.Encode(10f), Health = 100, First = 0, Count = 1 },
            }, Allocator.Temp);
            Messages.EncodeEnter(ref sink, tiles, units);
        });
        Assert.AreEqual(Speed, store.StateOf(Index).FullSpeed, "the Tank's speed in GameConfig.xml");
        store.Now = Dt;
        store.PredictAll(Dt);
    }

    [TearDown]
    public void TearDown() => store.Dispose();

    private delegate void Encode(ref NativeSink sink);

    private void Send(int tick, Encode encode)
    {
        var bytes = new NativeList<byte>(64, Allocator.Temp);
        var sink = new NativeSink(bytes);
        encode(ref sink);
        Assert.IsTrue(store.Apply(new ArraySegment<byte>(bytes.AsArray().ToArray()), tick));
        bytes.Dispose();
    }

    /// <summary>Sends a route of whole tiles (on the wire in <see cref="RouteWire"/> half tiles).</summary>
    private void SendRoute(int tick, params int2[] route) => Send(tick, (ref NativeSink sink) =>
    {
        var tiles = new NativeArray<int2>(route.Length, Allocator.Temp);
        for (int i = 0; i < route.Length; i++) tiles[i] = RouteWire.Encode(route[i]);
        var units = new NativeArray<RouteUnit>(new[] { new RouteUnit { Index = Index, First = 0, Count = route.Length } }, Allocator.Temp);
        Messages.EncodeMoveOrder(ref sink, tick, tiles, units);
    });

    private void SendStopped(int tick, short dx) => Send(tick, (ref NativeSink sink) =>
    {
        var units = new NativeArray<CorrectionUnit>(new[] { new CorrectionUnit { Index = Index, DX = dx, Resume = 0, Speed = 0 } }, Allocator.Temp);
        Messages.EncodeCorrection(ref sink, tick, 8, units);
    });

    [Test]
    public void AnOrderMovesTheDrawnUnitAtOnceAndLeavesThePredictionAlone()
    {
        store.ShowMove(Index, new float2(20f, 10f), 0.5f);
        store.PredictAll(Dt + 0.1f);
        Assert.Greater(store.Drawn[0].x, 10.2f, "it set off");
        Assert.AreEqual(10f, store.Predicted[0].x, 1e-4f, "the prediction waits for the server");
    }

    [Test]
    public void TheRouteTakesOverWithoutASnapOrAStepBack()
    {
        store.ShowMove(Index, new float2(30f, 10f), 0.5f);
        float now = Dt, lastX = 10f;
        for (int frame = 0; frame < 120; frame++)
        {
            if (frame == 18) // the route gets back 0.3 s after the click, between two frames
            {
                store.Now = now;
                SendRoute((int)math.round(now / Dt), new int2(10, 10), new int2(30, 10));
            }
            store.PredictAll(now += 1f / 60f);
            float x = store.Drawn[0].x;
            Assert.GreaterOrEqual(x, lastX - 1e-3f, $"a step back on frame {frame}");
            Assert.LessOrEqual(x - lastX, 1.05f * Speed / 60f, $"a jump on frame {frame}");
            lastX = x;
        }
        Assert.AreEqual(store.Predicted[0].x, lastX, 1e-2f, "and it ends on the server's route");
    }

    [Test]
    public void ARefusedOrderGlidesBack()
    {
        store.ShowMove(Index, new float2(30f, 10f), 0.5f);
        float now = Dt;
        for (int frame = 0; frame < 30; frame++) store.PredictAll(now += 1f / 60f);
        Assert.Greater(store.Drawn[0].x, 10.5f);
        for (int frame = 0; frame < 180; frame++) store.PredictAll(now += 1f / 60f);
        Assert.AreEqual(10f, store.Drawn[0].x, 1e-2f);
    }

    [Test]
    public void AShownOrderNeverGoesFarAheadOrPastItsStop()
    {
        store.ShowMove(Index, new float2(60f, 10f), 0.5f);
        float now = Dt;
        for (int frame = 0; frame < 50; frame++) store.PredictAll(now += 1f / 60f);
        Assert.LessOrEqual(store.Drawn[0].x, 10f + ClientUnitStore.MaxLeadTiles + 1e-3f);

        store.ShowMove(Index, new float2(store.Drawn[0].x + 0.4f, 10f), 0.5f);
        float held = store.Drawn[0].x;
        store.PredictAll(now += 0.1f);
        Assert.AreEqual(held, store.Drawn[0].x, 1e-4f, "already within its stop");
    }

    [Test]
    public void AShownOrderStopsAtAWall()
    {
        store.LeadOpen = p => p.x < 10.5f;
        store.ShowMove(Index, new float2(30f, 10f), 0.5f);
        float now = Dt;
        for (int frame = 0; frame < 30; frame++) store.PredictAll(now += 1f / 60f);
        Assert.Less(store.Drawn[0].x, 10.5f);
    }

    [Test]
    public void StopHoldsTheDrawnUnitUntilTheServerSaysItStopped()
    {
        store.Now = Dt;
        SendRoute(1, new int2(10, 10), new int2(30, 10));
        store.PredictAll(0.5f);
        float x = store.Drawn[0].x;
        Assert.Greater(x, 11f, "on its way");

        store.ShowHalt(Index);
        store.PredictAll(0.7f);
        Assert.AreEqual(x, store.Drawn[0].x, 1e-4f, "held at once");
        Assert.Greater(store.Predicted[0].x, x + 0.5f, "while the prediction runs on until the server answers");

        store.Now = 0.7f;
        SendStopped(14, 0);
        store.PredictAll(0.7f);
        Assert.AreEqual(x, store.Drawn[0].x, 1e-3f, "the answer doesn't jump it");
        store.PredictAll(3f);
        Assert.AreEqual(store.Predicted[0].x, store.Drawn[0].x, 1e-3f, "and it glides onto the server's answer");
    }

    [Test]
    public void ACorrectionNeverDrawsAMovingUnitBackward()
    {
        store.Now = Dt;
        SendRoute(1, new int2(10, 10), new int2(30, 10));
        float now = 1f;
        store.PredictAll(now);
        store.Now = now;
        Send(20, (ref NativeSink sink) =>
        {
            var units = new NativeArray<CorrectionUnit>(new[] { new CorrectionUnit { Index = Index, DX = -8, Resume = 1, Speed = 64 } }, Allocator.Temp);
            Messages.EncodeCorrection(ref sink, 20, 8, units); // a tile back, still at full speed
        });
        float lastX = store.Drawn[0].x;
        for (int frame = 0; frame < 60; frame++)
        {
            store.PredictAll(now += 1f / 60f);
            Assert.Greater(store.Drawn[0].x, lastX, $"a step back on frame {frame}");
            lastX = store.Drawn[0].x;
        }
        Assert.AreEqual(store.Predicted[0].x, lastX, 1e-2f);
    }

    [Test]
    public void ALateBatchDoesNotStallTheClock()
    {
        var go = new GameObject("ClockTest");
        ClientWorld world = go.AddComponent<ClientWorld>();
        world.Initialise();
        try
        {
            const float frame = 1f / 144f;
            var empty = new ArraySegment<byte>(Array.Empty<byte>());
            world.Apply(new ReplicationBatch { Tick = 100, Payload = empty });
            int tick = 100;
            float sinceBatch = 0f;
            var jitter = new System.Random(4);
            float wait = Dt;
            for (int f = 0; f < 1440; f++)
            {
                float before = world.ServerTime;
                world.AdvanceClock(frame);
                Assert.GreaterOrEqual(world.ServerTime - before, 0.85f * frame, $"the clock stalled on frame {f}");
                Assert.LessOrEqual(world.ServerTime - before, 1.15f * frame, $"the clock raced on frame {f}");
                sinceBatch += frame;
                if (sinceBatch < wait) continue;
                sinceBatch -= wait;
                wait = Dt + (float)jitter.NextDouble() * 0.04f - 0.02f; // 20 ms either way
                world.Apply(new ReplicationBatch { Tick = ++tick, Payload = empty });
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void AClockFarOffStillClosesTheGapQuickly()
    {
        var go = new GameObject("ClockTest");
        ClientWorld world = go.AddComponent<ClientWorld>();
        world.Initialise();
        try
        {
            var empty = new ArraySegment<byte>(Array.Empty<byte>());
            world.Apply(new ReplicationBatch { Tick = 100, Payload = empty });
            world.Apply(new ReplicationBatch { Tick = 200, Payload = empty }); // 5 s ahead, as after a seek
            for (int f = 0; f < 120; f++) world.AdvanceClock(1f / 60f);
            Assert.AreEqual(200 * Dt + 2f, world.ServerTime, 0.3f);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}

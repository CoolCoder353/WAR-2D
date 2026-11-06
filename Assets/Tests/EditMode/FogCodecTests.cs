using System;
using System.Collections.Generic;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using WAR2D.Net.Replication;

public class FogCodecTests
{
    [Test]
    public void SnapshotThenDeltaRoundTrips()
    {
        var visible = new NativeArray<byte>(2 * 12, Allocator.Temp);
        var explored = new NativeArray<byte>(2 * 12, Allocator.Temp);
        // grid 1 (offset 12) of a 4x3 fog: cells 0-1 visible, cell 5 explored
        visible[12] = 1; visible[13] = 1; explored[12] = 1; explored[13] = 1; explored[17] = 1;
        visible[0] = 1; // grid 0 must not leak into grid 1's snapshot

        var writer = new NetworkWriter();
        FogCodec.WriteSnapshot(writer, 4, 3, 2, visible, explored, 12);
        visible.Dispose();
        explored.Dispose();
        var fog = new ClientFog();
        Assert.IsTrue(FogCodec.Apply(fog, writer.ToArraySegment()));
        Assert.AreEqual(4, fog.Width);
        Assert.AreEqual(2, fog.CellSize);
        CollectionAssert.AreEqual(new byte[] { 2, 2, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, fog.State);
        Assert.IsTrue(fog.Sees(2.5f, 0.5f));
        Assert.IsFalse(fog.Sees(4.5f, 0.5f));

        writer.Position = 0;
        FogCodec.WriteDelta(writer, new List<int> { 1, 6, 11 }, 0, 3);
        Assert.IsTrue(FogCodec.Apply(fog, writer.ToArraySegment()));
        CollectionAssert.AreEqual(new byte[] { 2, 1, 0, 0, 0, 1, 2, 0, 0, 0, 0, 2 }, fog.State);
        var changed = new List<int>();
        Assert.IsTrue(fog.TakeChanges(changed), "the snapshot reports everything changed");
        Assert.IsFalse(fog.TakeChanges(changed));
    }

    [Test]
    public void MalformedPayloadsAreRejected()
    {
        var fog = new ClientFog();
        Assert.IsFalse(FogCodec.Apply(fog, new ArraySegment<byte>(new byte[] { FogCodec.Delta, 1, 0 })), "a delta before any snapshot");
        Assert.IsFalse(FogCodec.Apply(fog, new ArraySegment<byte>(new byte[] { FogCodec.Snapshot, 2, 2, 1, 0, 0, 9 })), "a run past the grid");
        Assert.IsFalse(FogCodec.Apply(new ClientFog(), new ArraySegment<byte>(new byte[] { FogCodec.Snapshot, 2, 2, 1, 2, 0, 1 })), "a later part with no first part");
        var other = new ClientFog();
        Assert.IsTrue(FogCodec.Apply(other, new ArraySegment<byte>(new byte[] { FogCodec.Snapshot, 3, 1, 1, 0, 0, 3 })));
        Assert.IsFalse(FogCodec.Apply(other, new ArraySegment<byte>(new byte[] { FogCodec.Snapshot, 2, 2, 1, 2, 0, 1 })), "a later part of a grid of another shape");
        Assert.IsFalse(FogCodec.Apply(fog, new ArraySegment<byte>(new byte[] { FogCodec.Snapshot, 2, 2, 1, 4, 0, 1 })), "a part starting past the grid");
        Assert.IsFalse(FogCodec.Apply(fog, new ArraySegment<byte>(new byte[] { 7 })), "an unknown kind");
        Assert.IsFalse(FogCodec.Apply(fog, new ArraySegment<byte>(new byte[] { FogCodec.Snapshot, 2 })), "truncated");
    }

    [Test]
    public void ABigSnapshotGoesInPartsUnderTheCap()
    {
        // A checkerboard is the worst case for runs: a 512 x 512 grid of single cells is far past the 16 KB a message may take.
        const int W = 512, H = 512;
        var visible = new NativeArray<byte>(W * H, Allocator.Temp);
        var explored = new NativeArray<byte>(W * H, Allocator.Temp);
        for (int i = 0; i < W * H; i++) { visible[i] = (byte)((i + i / W) & 1); explored[i] = (byte)(i % 3 == 0 ? 1 : 0); }
        var fog = new ClientFog();
        var writer = new NetworkWriter();
        int parts = 0;
        for (int start = 0; start < W * H; parts++)
        {
            writer.Position = 0;
            int next = FogCodec.WriteSnapshot(writer, W, H, 2, visible, explored, 0, start, FogCodec.MaxPayloadBytes);
            Assert.Greater(next, start);
            Assert.LessOrEqual(writer.Position, FogCodec.MaxPayloadBytes);
            Assert.IsTrue(FogCodec.Apply(fog, writer.ToArraySegment()), $"part {parts}");
            start = next;
        }
        Assert.Greater(parts, 1);
        for (int i = 0; i < W * H; i++)
            Assert.AreEqual(visible[i] != 0 ? 2 : explored[i] != 0 ? 1 : 0, fog.State[i], $"cell {i}");
        visible.Dispose();
        explored.Dispose();
    }

    [Test]
    public void ABigDeltaGoesInPartsUnderTheCap()
    {
        const int W = 1024, H = 1024;
        var visible = new NativeArray<byte>(W * H, Allocator.Temp);
        var explored = new NativeArray<byte>(W * H, Allocator.Temp);
        var fog = new ClientFog();
        var writer = new NetworkWriter();
        FogCodec.WriteSnapshot(writer, W, H, 1, visible, explored, 0);
        Assert.IsTrue(FogCodec.Apply(fog, writer.ToArraySegment()));
        visible.Dispose();
        explored.Dispose();

        var cells = new List<int>();
        for (int i = 0; i < W * H; i += 130) cells.Add(i); // 8000-odd gaps of two-byte varints: about 16 KB in one
        int parts = 0;
        for (int start = 0; start < cells.Count; parts++)
        {
            int count = FogCodec.DeltaCountWithin(cells, start, FogCodec.MaxPayloadBytes);
            writer.Position = 0;
            FogCodec.WriteDelta(writer, cells, start, count);
            Assert.LessOrEqual(writer.Position, FogCodec.MaxPayloadBytes);
            Assert.IsTrue(FogCodec.Apply(fog, writer.ToArraySegment()));
            start += count;
        }
        Assert.Greater(parts, 1);
        foreach (int cell in cells) Assert.AreEqual((byte)FogState.Visible, fog.State[cell]);
        Assert.AreEqual((byte)FogState.Unexplored, fog.State[1]);
    }
}

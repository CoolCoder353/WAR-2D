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
        Assert.IsFalse(FogCodec.Apply(fog, new ArraySegment<byte>(new byte[] { FogCodec.Snapshot, 2, 2, 1, 0, 9 })), "a run past the grid");
        Assert.IsFalse(FogCodec.Apply(fog, new ArraySegment<byte>(new byte[] { 7 })), "an unknown kind");
        Assert.IsFalse(FogCodec.Apply(fog, new ArraySegment<byte>(new byte[] { FogCodec.Snapshot, 2 })), "truncated");
    }
}

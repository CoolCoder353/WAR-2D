using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Pathing;
using WAR2D.World;

/// <summary>The job-readable table mirrors the cache's fields and republishes only what changed.</summary>
public class OrderFieldTableTests
{
    private const int Size = 160;

    private sealed class Rig : System.IDisposable
    {
        public MapStore Map = SimHarness.OpenMap(Size);
        public SectorFieldCache Cache;
        public OrderFieldTable Table;

        public Rig()
        {
            Cache = new SectorFieldCache(Map.Grid, Allocator.Persistent, cellSize: 2);
            Table = Cache.CreateTable(Allocator.Persistent);
        }

        public int Order(int2 goal, params int2[] starts)
        {
            var cells = new NativeArray<int>(starts.Length, Allocator.Temp);
            for (int i = 0; i < starts.Length; i++) cells[i] = starts[i].y * Size + starts[i].x;
            int handle = Cache.Acquire(goal, FlowSizeClass.Small, 1, cells);
            cells.Dispose();
            Rebuild();
            return handle;
        }

        public void Rebuild()
        {
            Cache.ScheduleRebuilds(16).Complete();
            Cache.CompleteRebuilds(ref Table);
        }

        public void Dispose()
        {
            Cache.Dispose();
            Table.Dispose();
            Map.Dispose();
        }
    }

    [Test]
    public void BlockMatchesSectorField()
    {
        using var rig = new Rig();
        int handle = rig.Order(new int2(150, 150), new int2(5, 5));
        int covered = 0;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            byte expected = rig.Cache.DirectionAtCell(handle, y * Size + x);
            Assert.AreEqual(expected, rig.Table.DirectionAt(handle, new float2(x + 0.5f, y + 0.5f), Size), $"tile {x},{y}");
            if (expected != FlowDirections.None) covered++;
        }
        Assert.Greater(covered, 0);
    }

    [Test]
    public void UncoveredSectorReturnsNone()
    {
        using var rig = new Rig();
        int handle = rig.Order(new int2(20, 20), new int2(5, 5));
        Assert.AreEqual(FlowDirections.None, rig.Table.DirectionAt(handle, new float2(150.5f, 5.5f), Size));
        Assert.AreEqual(FlowDirections.None, rig.Table.DirectionAt(handle + 1, new float2(5.5f, 5.5f), Size), "unknown handle");
    }

    [Test]
    public void AddStartSectorCoversItAfterRebuild()
    {
        using var rig = new Rig();
        int handle = rig.Order(new int2(20, 20), new int2(5, 5));
        var far = new float2(150.5f, 10.5f);
        Assert.AreEqual(FlowDirections.None, rig.Table.DirectionAt(handle, far, Size));
        rig.Cache.AddStartSector(handle, rig.Table.SectorOf((int2)far));
        rig.Rebuild();
        byte direction = rig.Table.DirectionAt(handle, far, Size);
        Assert.IsTrue(FlowDirections.IsStep(direction), $"direction {direction}");
        Assert.Less(FlowDirections.Step(direction).x, 0, "the step should head back west toward the goal");
    }

    [Test]
    public void InvalidateOnlyRepublishesTouchedSectors()
    {
        using var rig = new Rig();
        // A route along the bottom row of sectors; a footprint far above it must not republish anything.
        int handle = rig.Order(new int2(150, 10), new int2(5, 10));
        int[] before = rig.Table.BlockVersion.AsArray().ToArray();
        rig.Map.SetUsed(new int2(80, 140), true);
        foreach (int2 tile in rig.Map.ChangedTiles) rig.Cache.Invalidate(tile);
        rig.Map.ChangedTiles.Clear();
        rig.Rebuild();
        CollectionAssert.AreEqual(before, rig.Table.BlockVersion.AsArray().ToArray());

        // A footprint on the route rewrites only the blocks whose directions changed.
        rig.Map.SetUsed(new int2(80, 10), true);
        foreach (int2 tile in rig.Map.ChangedTiles) rig.Cache.Invalidate(tile);
        rig.Rebuild();
        int[] after = rig.Table.BlockVersion.AsArray().ToArray();
        int rewritten = 0;
        for (int i = 0; i < before.Length; i++) if (after[i] != before[i]) rewritten++;
        Assert.Greater(rewritten, 0);
        Assert.Less(rewritten, before.Length, "untouched sectors keep their blocks");
        Assert.IsTrue(rig.Cache.IsReady(handle));
    }

    [Test]
    public void OpenMapRouteCoversEverySectorBetween()
    {
        // Regression: corner-sharing portals once made every exit of an open sector tie at cost 0,
        // and the route walk stopped after the first sector.
        using var rig = new Rig();
        int handle = rig.Order(new int2(150, 10), new int2(5, 10));
        for (int x = 5; x < 150; x += 32)
            Assert.IsTrue(FlowDirections.IsStep(rig.Table.DirectionAt(handle, new float2(x + 0.5f, 10.5f), Size)), $"x {x}");
    }
}

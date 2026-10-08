using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>Fog of war: sight, walls, explored memory, shared team vision and change reporting.</summary>
public class SimVisionTests
{
    /// <summary>A 64x64 open map with a full-height wall at x = 20..21.</summary>
    private static SimHarness WalledSim()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
            rows[r] = r == 0 || r == 63 ? new string('B', 64) : "B" + new string('.', 19) + "##" + new string('.', 41) + "B";
        return new SimHarness(MapStore.FromAscii(rows));
    }

    private static void Pass(SimHarness sim, int ticks = 6)
    {
        for (int i = 0; i < ticks; i++) sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
    }

    [Test]
    public void UnitSeesAroundItButNotThroughWalls()
    {
        using var sim = WalledSim();
        sim.Spawn(SimHarness.OwnerA, new float2(15, 32));
        Pass(sim);
        int grid = sim.Context.VisionOf(SimHarness.OwnerA);
        SimData data = sim.Context.Data;
        Assert.IsTrue(data.Sees(grid, new float2(15, 32)));
        Assert.IsTrue(data.Sees(grid, new float2(19, 32)), "open ground in range");
        Assert.IsTrue(data.Sees(grid, new float2(20.5f, 32)), "the wall face itself is seen");
        Assert.IsFalse(data.Sees(grid, new float2(22.5f, 32)), "behind the wall");
        Assert.IsFalse(data.Sees(grid, new float2(15, 50)), "out of range");
    }

    [Test]
    public void ExploredRemainsAfterTheUnitLeaves()
    {
        using var sim = WalledSim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        Pass(sim);
        sim.Teleport(a[0], new float2(10, 50));
        Pass(sim, 8);
        int grid = sim.Context.VisionOf(SimHarness.OwnerA);
        SimData data = sim.Context.Data;
        int cell = grid * data.FogCells + data.FogCellOf(new float2(10, 10));
        Assert.AreEqual(0, data.Visible[cell], "no longer seen");
        Assert.AreEqual(1, data.Explored[cell], "but remembered");
    }

    [Test]
    public void AlliesShareVisionAndEnemiesDoNot()
    {
        using var sim = WalledSim();
        sim.Context.TeamResolver = owner => owner == SimHarness.OwnerA || owner == SimHarness.OwnerB ? 0 : 1;
        const int OwnerC = 303;
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Spawn(SimHarness.OwnerB, new float2(40, 40));
        sim.Spawn(OwnerC, new float2(40, 10));
        Pass(sim);
        int ab = sim.Context.VisionOf(SimHarness.OwnerA);
        Assert.AreEqual(ab, sim.Context.VisionOf(SimHarness.OwnerB));
        int c = sim.Context.VisionOf(OwnerC);
        Assert.AreNotEqual(ab, c);
        SimData data = sim.Context.Data;
        Assert.IsTrue(data.Sees(ab, new float2(10, 10)) && data.Sees(ab, new float2(40, 40)));
        Assert.IsFalse(data.Sees(c, new float2(10, 10)));
    }

    [Test]
    public void ChangesAreQueuedForTheGridThatChanged()
    {
        using var sim = WalledSim();
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Tick(); // the pass on tick 1 sees the new unit; its changes stay queued until the next boundary
        sim.Em.CompleteAllTrackedJobs();
        SimData data = sim.Context.Data;
        int grid = sim.Context.VisionOf(SimHarness.OwnerA);
        Assert.Greater(data.FogChanges.Count, 0);
        while (data.FogChanges.TryDequeue(out int index)) Assert.AreEqual(grid, index / data.FogCells);
    }
}

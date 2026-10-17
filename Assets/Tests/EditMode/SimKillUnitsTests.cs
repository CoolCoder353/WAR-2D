using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

public class SimKillUnitsTests
{
    [Test]
    public void KillUnitsRemovesOnlyThoseIdsAndRaisesUnitDied()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(5.5f, 5.5f));
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(9.5f, 5.5f));
        sim.Tick();
        int died = 0;
        sim.Context.UnitDied += (id, _) => { if (id == a[0]) died++; };
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillUnits, Ids = new[] { a[0] } });
        sim.Tick();
        Assert.AreEqual(1, sim.UnitCount);
        Assert.AreEqual(1, died);
    }
}

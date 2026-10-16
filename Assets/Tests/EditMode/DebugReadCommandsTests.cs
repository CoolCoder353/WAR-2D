using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Dev.DebugApi;
using WAR2D.Sim;

public class DebugReadCommandsTests
{
    [Test]
    public void UnitsFiltersByOwnerTypeAndBox()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Spawn(SimHarness.OwnerA, new float2(5.5f, 5.5f));
        sim.Spawn(SimHarness.OwnerA, new float2(40.5f, 40.5f));
        sim.Spawn(SimHarness.OwnerB, new float2(6.5f, 6.5f), UnitType.Builder);
        sim.Tick(); sim.Tick();
        var snap = new SettledSnapshot();
        SimContext.RunningOverride = false; sim.Tick(); // settle the jobs before reading the SoA
        snap.Capture(sim.Context.Data, sim.Context.Clock.Tick, sim.UnitCount);

        JObject all = JObject.FromObject(ReadCommands.Units(snap, new JObject()));
        Assert.AreEqual(3, (int)all["count"]);
        JObject a = JObject.FromObject(ReadCommands.Units(snap, JObject.Parse($"{{\"owner\":{SimHarness.OwnerA}}}")));
        Assert.AreEqual(2, (int)a["count"]);
        JObject box = JObject.FromObject(ReadCommands.Units(snap, JObject.Parse("{\"box\":[0,0,10,10]}")));
        Assert.AreEqual(2, (int)box["count"]);
        JObject builders = JObject.FromObject(ReadCommands.Units(snap, JObject.Parse("{\"type\":\"Builder\"}")));
        Assert.AreEqual(1, (int)builders["count"]);
    }

    [Test]
    public void LimitCapsTheListButNotTheCount()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        for (int i = 0; i < 10; i++) sim.Spawn(SimHarness.OwnerA, new float2(5.5f + i, 5.5f));
        sim.Tick();
        SimContext.RunningOverride = false; sim.Tick();
        var snap = new SettledSnapshot();
        snap.Capture(sim.Context.Data, sim.Context.Clock.Tick, sim.UnitCount);
        JObject r = JObject.FromObject(ReadCommands.Units(snap, JObject.Parse("{\"limit\":3}")));
        Assert.AreEqual(10, (int)r["count"]);
        Assert.AreEqual(3, ((JArray)r["units"]).Count);
    }

    [Test]
    public void AsciiTilesDrawsTheMap()
    {
        var map = WAR2D.World.MapStore.FromAscii(new[] { "BBBB", "B.#B", "B*.B", "BBBB" });
        string text = ReadCommands.AsciiTiles(map.Grid, new int2(0, 0), new int2(3, 3));
        StringAssert.Contains("B.#B", text);
        map.Dispose();
    }
}

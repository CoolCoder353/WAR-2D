using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client;

/// <summary>Roam (playtest 2026-11-02): a large roaming group never floods the order command's rate limit.</summary>
public class RoamTests
{
    [Test]
    public void ALargeRoamingGroupSendsAFewOrdersPerCheckAndServesEveryUnit()
    {
        var roam = new Roam();
        var ids = new List<int>();
        for (int i = 1; i <= 100; i++) ids.Add(i);
        roam.Start(ids);
        var served = new HashSet<int>();
        float now = 0f;
        for (int check = 0; check < 25; check++)
        {
            int sent = 0;
            roam.Update(now, new int2(0, 0), new int2(256, 256), (list, goal) => { sent++; served.UnionWith(list); },
                (int id, out float2 position) => { position = new float2(128, 128); return true; });
            Assert.LessOrEqual(sent, Roam.MaxOrdersPerCheck, $"check {check}");
            now += Roam.CheckSeconds;
        }
        Assert.AreEqual(100, served.Count, "round robin: every unit gets a goal");
    }

    [Test]
    public void ThreeThousandRoamersInOneAreaAllGetGoalsWithinAFewSeconds()
    {
        // Playtest 2026-11-05: 3,000 roamers waited minutes for a goal (one unit per order, 8 orders a second).
        var roam = new Roam();
        var ids = new List<int>();
        for (int i = 1; i <= 3000; i++) ids.Add(i);
        roam.Start(ids);
        var served = new HashSet<int>();
        float now = 0f;
        for (int check = 0; check < 40; check++) // 20 seconds
        {
            roam.Update(now, new int2(0, 0), new int2(512, 512), (list, goal) => served.UnionWith(list),
                (int id, out float2 position) => { position = new float2(100 + id % 64, 100 + id / 64 % 64); return true; });
            now += Roam.CheckSeconds;
        }
        Assert.AreEqual(3000, served.Count);
    }

    [Test]
    public void UnitsTheClientNoLongerKnowsStopRoaming()
    {
        var roam = new Roam();
        roam.Start(new[] { 1, 2 });
        roam.Update(0f, new int2(0, 0), new int2(256, 256), (list, goal) => { },
            (int id, out float2 position) => { position = new float2(128, 128); return id == 1; });
        Assert.AreEqual(1, roam.Count);
    }
}

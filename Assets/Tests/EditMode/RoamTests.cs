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

    /// <summary>
    /// 2026-11-11: 180 roamers in 60 clusters each drew their own goal and held 28 to 31 of the server's 32 order slots, so two
    /// thirds of them were refused an order and stood still, and Builders' orders were refused too.
    /// </summary>
    [Test]
    public void RoamersInManyClustersFollowNoMoreThanMaxGoalsAtOnce()
    {
        var roam = new Roam();
        var position = new Dictionary<int, float2>();
        var goal = new Dictionary<int, int2>();
        var ids = new List<int>();
        int id = 1;
        for (int i = 0; i < 10; i++)
        for (int j = 0; j < 6; j++)
            for (int k = 0; k < 3; k++) { position[id] = new float2(20 + 20 * i + k, 20 + 20 * j); ids.Add(id++); } // 60 clusters, 180 units
        roam.Start(ids);
        var served = new HashSet<int>();
        int mostGoals = 0;
        float now = 0f;
        for (int check = 0; check < 240; check++) // two minutes
        {
            roam.Update(now, new int2(0, 0), new int2(400, 400), (list, to) =>
            {
                foreach (int unit in list) { goal[unit] = to; served.Add(unit); }
            }, (int unit, out float2 at) => { at = position[unit]; return true; });
            var following = new HashSet<int2>();
            foreach (var pair in goal)
            {
                float2 target = (float2)pair.Value + 0.5f, here = position[pair.Key];
                float far = math.distance(here, target);
                if (far > Roam.ArriveTiles) following.Add(pair.Value); // still on its way: the server holds that order
                position[pair.Key] = far <= 2f ? target : here + (target - here) / far * 2f; // 4 tiles a second
            }
            mostGoals = math.max(mostGoals, following.Count);
            now += Roam.CheckSeconds;
        }
        Assert.LessOrEqual(mostGoals, Roam.MaxGoals, "goals followed at once");
        Assert.AreEqual(180, served.Count, "and every roamer still got an order");
    }

    [Test]
    public void AGroupOverTheBudgetJoinsAGoalAlreadyHeld()
    {
        var roam = new Roam();
        var ids = new List<int>();
        for (int i = 1; i <= 40; i++) ids.Add(i);
        roam.Start(ids);
        var goals = new HashSet<int2>();
        var served = new HashSet<int>();
        float now = 0f;
        for (int check = 0; check < 10; check++) // 5 seconds: nobody arrives, nobody is stuck yet
        {
            roam.Update(now, new int2(0, 0), new int2(512, 512), (list, to) => { goals.Add(to); served.UnionWith(list); },
                (int unit, out float2 at) => { at = new float2(40 + 20 * (unit % 20), 40 + 40 * (unit / 20)); return true; }); // 40 cells
            now += Roam.CheckSeconds;
        }
        Assert.AreEqual(40, served.Count, "every roamer has a goal");
        Assert.LessOrEqual(goals.Count, Roam.MaxGoals, "from no more than MaxGoals goals");
    }

    [Test]
    public void AStuckGroupIsNotSentBackToTheGoalItIsStuckOn()
    {
        var roam = new Roam();
        roam.Start(new[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var last = new Dictionary<int, int2>();
        var at = new Dictionary<int, float2>();
        for (int unit = 1; unit <= 8; unit++) at[unit] = new float2(40 + 24 * unit, 40); // a cell each, none moving
        float now = 0f;
        for (int check = 0; check < 40; check++) // 20 seconds: past several stuck timeouts, with the budget spent
        {
            roam.Update(now, new int2(0, 0), new int2(512, 512), (list, to) =>
            {
                foreach (int unit in list)
                {
                    if (last.TryGetValue(unit, out int2 before)) Assert.AreNotEqual(before, to, $"unit {unit} sent back to the goal it is stuck on");
                    last[unit] = to;
                }
            }, (int unit, out float2 p) => { p = at[unit]; return true; });
            now += Roam.CheckSeconds;
        }
        Assert.AreEqual(8, last.Count);
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

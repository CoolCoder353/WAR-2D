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

    /// <summary>
    /// Player reports of roamers standing about: the server ends a group's Move 0.5 + 0.4 × √followers tiles from the goal,
    /// further out than Roam's 2 tiles, so most of a group counted as still travelling and stood for the whole stuck
    /// timeout at every goal. Here the server is modelled with that rule.
    /// </summary>
    [Test]
    public void AGroupTheServerStopsShortOfItsGoalMovesOnWithoutTheStuckWait()
    {
        var roam = new Roam();
        var at = new Dictionary<int, float2>();
        var target = new Dictionary<int, float2>(); // the server's live order per unit
        var orders = new Dictionary<int, int>();
        var stoodSince = new Dictionary<int, float>();
        var ids = new List<int>();
        for (int unit = 1; unit <= 40; unit++) { at[unit] = new float2(200 + unit % 8, 200 + unit / 8); ids.Add(unit); orders[unit] = 0; }
        roam.Start(ids);
        float now = 0f, longestStand = 0f;
        for (int check = 0; check < 160; check++) // 80 seconds
        {
            roam.Update(now, new int2(0, 0), new int2(512, 512), (list, to) =>
            {
                foreach (int unit in list) { target[unit] = (float2)to + 0.5f; orders[unit]++; stoodSince.Remove(unit); }
            }, (int unit, out float2 p) => { p = at[unit]; return true; });

            // The server: 2 tiles a check towards the goal, and a unit stops inside its order's stop radius.
            var followers = new Dictionary<float2, int>();
            foreach (float2 goal in target.Values) followers[goal] = followers.TryGetValue(goal, out int n) ? n + 1 : 1;
            foreach (int unit in ids)
            {
                if (!target.TryGetValue(unit, out float2 goal))
                {
                    if (orders[unit] == 0) continue; // not started yet
                    if (!stoodSince.ContainsKey(unit)) stoodSince[unit] = now;
                    longestStand = math.max(longestStand, now - stoodSince[unit]);
                    continue;
                }
                float far = math.distance(at[unit], goal);
                if (far <= 0.5f + 0.4f * math.sqrt(followers[goal])) { target.Remove(unit); continue; }
                at[unit] += (goal - at[unit]) / far * math.min(2f, far);
            }
            now += Roam.CheckSeconds;
        }
        Assert.Less(longestStand, Roam.StuckSeconds / 2f, "no roamer stands for anything like the stuck timeout after the server stops it");
        foreach (int unit in ids) Assert.GreaterOrEqual(orders[unit], 4, $"unit {unit} kept roaming from goal to goal");
    }

    [Test]
    public void AUnitThatHasNotSetOffYetKeepsItsGoalUntilTheStuckTimeout()
    {
        // A route can take a moment to build, and the unit waits for it: standing before it has moved is not "stopped".
        var roam = new Roam();
        roam.Start(new[] { 1 });
        int sent = 0;
        float now = 0f;
        for (int check = 0; check < 10; check++) // 5 seconds, under StuckSeconds
        {
            roam.Update(now, new int2(0, 0), new int2(512, 512), (list, to) => sent++,
                (int unit, out float2 p) => { p = new float2(100, 100); return true; });
            now += Roam.CheckSeconds;
        }
        Assert.AreEqual(1, sent, "one order, and no second one while it waits to set off");
    }

    /// <summary>
    /// Player report (2026-11-13), reproducible with 500 units told to roam: they circle between two points. With every goal
    /// slot taken, a group that arrived joined the nearest goal still followed, whose arrivals joined the first one back,
    /// and with that many units both always had followers, so no new goal was ever drawn.
    /// </summary>
    [Test]
    public void FiveHundredRoamersAreNeverSentBackToAGoalTheyHaveBeenTo()
    {
        var roam = new Roam();
        var at = new Dictionary<int, float2>();
        var target = new Dictionary<int, float2>();
        var been = new Dictionary<int, HashSet<int2>>();
        var ids = new List<int>();
        for (int unit = 1; unit <= 500; unit++) { at[unit] = new float2(240 + unit % 25, 240 + unit / 25); ids.Add(unit); been[unit] = new HashSet<int2>(); }
        roam.Start(ids);
        int returns = 0, orders = 0, mostGoals = 0;
        var allGoals = new HashSet<int2>();
        float now = 0f;
        for (int check = 0; check < 360; check++) // three minutes
        {
            roam.Update(now, new int2(0, 0), new int2(512, 512), (list, to) =>
            {
                orders++;
                allGoals.Add(to);
                foreach (int unit in list)
                {
                    if (!been[unit].Add(to)) returns++;
                    target[unit] = (float2)to + 0.5f;
                }
            }, (int unit, out float2 p) => { p = at[unit]; return true; });

            // The server: 2 tiles a check towards the goal; a unit stops inside its order's stop radius (0.5 + 0.4 × √followers).
            var followers = new Dictionary<float2, int>();
            foreach (float2 goal in target.Values) followers[goal] = followers.TryGetValue(goal, out int n) ? n + 1 : 1;
            mostGoals = math.max(mostGoals, followers.Count);
            foreach (int unit in ids)
            {
                if (!target.TryGetValue(unit, out float2 goal)) continue;
                float far = math.distance(at[unit], goal);
                if (far <= 0.5f + 0.4f * math.sqrt(followers[goal])) { target.Remove(unit); continue; }
                at[unit] += (goal - at[unit]) / far * math.min(2f, far);
            }
            now += Roam.CheckSeconds;
        }
        Assert.AreEqual(0, returns, $"units sent back to a goal they had already been to ({orders} orders, {allGoals.Count} goals)");
        Assert.Greater(allGoals.Count, 20, "and the army keeps finding new ground");
        Assert.LessOrEqual(mostGoals, Roam.MaxGoals + 1, "within the server's order budget (one over while a goal is replaced)");
    }
}

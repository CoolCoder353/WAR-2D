using System.Linq;
using Config;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Ai;
using WAR2D.AiView;

/// <summary>
/// v0.21 Warpath (spec 2026-11-22): reach-aware targets and breaches (§3, §4.1). Later phases add their manager tests here.
/// </summary>
public class WarpathTests
{
    [TearDown]
    public void TearDown() => ConfigLoader.ResetForTests();

    private static AiSandbox Playing(AiDifficulty difficulty, AiStyle style)
    {
        var world = new AiSandbox(256, 5);
        world.MakeBrain(difficulty, style);
        world.ThinkNow();
        world.Run(2.0);
        Assert.That(world.Count(world.Ai, BuildingType.Base), Is.EqualTo(1), "the HQ is placed while HQs are placed");
        world.State = GameState.Playing;
        world.ThinkNow();
        return world;
    }

    private static AiBuilding HqOf(AiSandbox world, int owner) => world.Buildings.First(b => b.OwnerId == owner && b.Type == BuildingType.Base);

    // ---------- §3, §4.1: targets it can't reach, and walled ones ----------

    /// <summary>
    /// A Hard (or <paramref name="difficulty"/>) AI with eight Tanks at home and open ground round its HQ, so targets can be
    /// put at known distances; <paramref name="home"/> is its HQ's tile.
    /// </summary>
    private static AiSandbox Flat(AiDifficulty difficulty, out int2 home)
    {
        AiSandbox world = Playing(difficulty, AiStyle.Balanced);
        world.RevealAll();
        home = (int2)math.floor(world.Brain.Context.State.Home);
        for (int y = -40; y <= 40; y++)
            for (int x = -40; x <= 40; x++)
            {
                int2 t = home + new int2(x, y);
                if (t.x >= 8 && t.y >= 8 && t.x < world.Width - 8 && t.y < world.Height - 8) world.SetTile(t, TileType.Ground);
            }
        for (int i = 0; i < 8; i++) world.AddUnit(world.Ai, UnitType.Tank, (float2)home + new float2(-4 + i, -4));
        world.ThinkNow(); // relabels its reach map
        return world;
    }

    /// <summary>An enemy Miner its side knows of at <paramref name="at"/> (put straight on its blackboard).</summary>
    private static int KnownMiner(AiSandbox world, int2 at, int id)
    {
        world.Brain.Board.EnemyBuildings[id] = new AiBuilding { Id = id, OwnerId = world.Enemy, Type = BuildingType.Miner, Anchor = at, Health = 50, MaxHealth = 50, Progress = 100, Ghost = true };
        return id;
    }

    private static bool Choose(AiSandbox world, out int id)
    {
        AiContext ai = world.Brain.Context;
        return ArmyManager.ChooseTarget(ai, ai.State.Home, -1, ai.Difficulty.AttackAdvantage, out id, out _, out _);
    }

    [Test]
    public void ACutOffTargetIsNeverChosen()
    {
        AiSandbox world = Flat(AiDifficulty.Hard, out int2 home);
        int2 at = home + new int2(25, 0);
        world.Ring(at, 5, TileType.Wall);
        world.ThinkNow();
        Assert.That(world.Brain.Context.Reach.Query(home, at, world.View.Tiles, world.Brain.Context.WallCostFn, null, out _), Is.EqualTo(Reach.Cut));
        KnownMiner(world, at, 9001);
        Assert.That(Choose(world, out _), Is.False, "rock all round: nothing to attack");
    }

    [Test]
    public void AWalledTargetIsChosenWhenItIsTheOnlyOne()
    {
        AiSandbox world = Flat(AiDifficulty.Hard, out int2 home);
        int2 at = home + new int2(25, 0);
        world.Ring(at, 5, TileType.PlayerWall, world.Enemy);
        world.ThinkNow();
        Assert.That(world.Brain.Context.Reach.Query(home, at, world.View.Tiles, world.Brain.Context.WallCostFn, null, out _), Is.EqualTo(Reach.Walled));
        int miner = KnownMiner(world, at, 9001);
        Assert.That(Choose(world, out int id), Is.True);
        Assert.That(id, Is.EqualTo(miner), "Hard breaches");
    }

    [Test]
    public void AnOpenTargetBeatsAWalledOneOfTheSameWorthAndDistance()
    {
        AiSandbox world = Flat(AiDifficulty.Hard, out int2 home);
        int2 walledAt = home + new int2(25, 0), openAt = home + new int2(-25, 0);
        world.Ring(walledAt, 5, TileType.PlayerWall, world.Enemy);
        world.ThinkNow();
        KnownMiner(world, walledAt, 9001);
        int open = KnownMiner(world, openAt, 9002);
        Assert.That(Choose(world, out int id), Is.True);
        Assert.That(id, Is.EqualTo(open), "the breach costs: the open Miner first");
    }

    [Test]
    public void EasySkipsWalledTargets()
    {
        AiSandbox world = Flat(AiDifficulty.Easy, out int2 home);
        int2 at = home + new int2(25, 0);
        world.Ring(at, 5, TileType.PlayerWall, world.Enemy);
        world.ThinkNow();
        KnownMiner(world, at, 9001);
        Assert.That(world.Brain.Context.Difficulty.Breaches, Is.False);
        Assert.That(Choose(world, out _), Is.False, "Easy doesn't breach");
    }

    // ---------- §4.1: the breach plan ----------

    [Category("Slow")]
    [Test]
    public void AnAttackOnAWalledHqGoesForTheBreachThenTheHq()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Rusher);
        world.RevealAll();
        AiBuilding hq = HqOf(world, world.Ai);
        int2 enemyHq = world.Sites.OrderByDescending(s => math.distance(s, hq.Anchor)).First();
        world.AddBuilding(world.Enemy, BuildingType.Base, enemyHq);
        int2 centre = enemyHq + 2;
        world.Ring(centre, 9, TileType.PlayerWall, world.Enemy);
        for (int i = 0; i < 14; i++) world.AddUnit(world.Ai, UnitType.Tank, (float2)hq.Anchor + new float2(-6 + i % 4, -6 - i / 4));
        world.Run(world.Game.Ai.GetStyle(AiStyle.Rusher).FirstAttackSeconds + 5.0);
        // Its side sees the enemy HQ (a ghost after).
        int spotter = world.AddUnit(world.Ai, UnitType.Scout, (float2)enemyHq + new float2(0, 3));
        world.Run(2.0);
        world.Units.RemoveAll(u => u.Id == spotter);
        ArmyManager army = world.Brain.Manager<ArmyManager>();
        for (int t = 0; t < 30 && army.Attacks == 0; t++) world.Run(1.0);
        Assert.That(army.Attacks, Is.GreaterThanOrEqualTo(1), $"it attacks the walled HQ (mode {army.Mode}, known {world.Brain.Board.EnemyBuildings.Count})");
        Assert.That(army.BreachesStarted, Is.GreaterThanOrEqualTo(1), "through a breach");
        Assert.That(army.BreachStatus, Is.Not.Empty);
        int before = world.Actions.Count;
        for (int t = 0; t < world.Game.Ai.Play.StageSeconds + 30 && !world.Actions.Skip(before).Any(a => a.Kind == AiActionKind.Order && a.Order == OrderKind.AttackMove && Chebyshev(a.Tile, centre) is 8 or 10); t++) world.Run(1.0);
        Assert.That(world.Actions.Skip(before).Any(a => a.Kind == AiActionKind.Order && a.Order == OrderKind.AttackMove && Chebyshev(a.Tile, centre) is 8 or 10), Is.True,
            $"attack-moving to the wall ({army.BreachStatus}; orders {string.Join(" ", world.Actions.Skip(before).Where(a => a.Kind == AiActionKind.Order).Select(a => $"{a.Order}@{a.Tile}"))})");

        // The wall falls: on to the HQ.
        for (int k = 0; k < 8 * 9; k++)
        {
            int2 t = centre + Placement.RingOffset(9, k);
            if (world.TileAt(t) == TileType.PlayerWall) world.SetTile(t, TileType.Ground);
        }
        before = world.Actions.Count;
        float near = world.Game.Ai.Play.StageDistance + world.Game.Ai.Play.DefenceSpread + 2f;
        bool AtHq(AiAction a) => a.Kind == AiActionKind.Order && a.Order == OrderKind.AttackMove && math.distance(a.Tile, enemyHq) <= near;
        for (int t = 0; t < 20 && !world.Actions.Skip(before).Any(AtHq); t++) world.Run(1.0);
        Assert.That(army.BreachStatus, Is.Empty, $"the breach is open; enemy HQ {enemyHq}, mode {army.Mode}, engaged {army.Engaged}, reach {world.Brain.Status()["reach"]}, log: {string.Join(" | ", army.Log.Skip(System.Math.Max(0, army.Log.Count - 8)))}");
        Assert.That(world.Actions.Skip(before).Any(AtHq), Is.True, "on to the HQ (staging short of it, or at it)");
    }

    private static int Chebyshev(int2 a, int2 b) => math.cmax(math.abs(a - b));

    // ---------- §6.1: the bar falls while its army idles ----------

    [Category("Slow")]
    [Test]
    public void TheBarFallsWhileItsArmyIdlesAndAnAttackResetsIt()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        world.RevealAll();
        AiBuilding hq = HqOf(world, world.Ai);
        for (int i = 0; i < 16; i++) world.AddUnit(world.Ai, UnitType.Tank, (float2)hq.Anchor + new float2(-6 + i % 4, -6 - i / 4));
        AiStyleConfig style = world.Brain.Context.Style;
        world.Run(style.FirstAttackSeconds + 5.0);
        ArmyManager army = world.Brain.Manager<ArmyManager>();
        AiContext ai = world.Brain.Context;
        Assert.That(army.Attacks, Is.Zero, "nothing it knows to attack");
        float full = ai.Difficulty.AttackAdvantage * style.AttackRatioScale * army.Caution;
        Assert.That(army.Idle, Is.GreaterThan(0.0), "the idle clock runs");
        world.Run(style.IdlePatienceSeconds);
        Assert.That(army.IdleFactor(ai), Is.EqualTo(style.IdleFloor).Within(1e-4f), "after its patience: the floor, no lower");
        Assert.That(army.Need(ai), Is.EqualTo(full * style.IdleFloor).Within(1e-3f));

        int2 enemyHq = world.Sites.OrderByDescending(s => math.distance(s, hq.Anchor)).First();
        world.AddBuilding(world.Enemy, BuildingType.Base, enemyHq);
        int spotter = world.AddUnit(world.Ai, UnitType.Scout, (float2)enemyHq + new float2(0, 3));
        world.Run(2.0);
        world.Units.RemoveAll(u => u.Id == spotter);
        for (int t = 0; t < 20 && army.Attacks == 0; t++) world.Run(1.0);
        Assert.That(army.Attacks, Is.GreaterThanOrEqualTo(1), "it attacks once it knows a target");
        Assert.That(army.Idle, Is.Zero, "an attack resets the idle clock");
    }

    // ---------- §5: threat axes ----------

    [Category("Slow")]
    [Test]
    public void ALaneItWasAttackedThroughBecomesItsTopAxis()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        world.RevealAll();
        world.Run(31.0); // a replan: axes from what it predicts
        ThreatAxes axes = world.Brain.Board.Axes;
        AiState state = world.Brain.Context.State;
        int home = state.RegionAt(state.Home);
        var mine = axes.Axes.Where(a => a.Region == home).ToList();
        Assert.That(mine.Count, Is.GreaterThanOrEqualTo(2), "its start region has lanes out");
        ThreatAxes.Axis quiet = mine.OrderBy(a => a.Score).First();
        AiBuilding hq = HqOf(world, world.Ai);
        float2 at = state.CentreOf(hq) + quiet.Dir * 9f;
        for (int i = 0; i < 6; i++) world.AddUnit(world.Enemy, UnitType.Tank, at + new float2(i % 3, i / 3));
        world.Run(2.0);
        world.Units.RemoveAll(u => u.OwnerId == world.Enemy);
        world.Run(31.0);
        ThreatAxes.Axis top = axes.Axes.First(a => a.Region == home);
        Assert.That(top.Lane, Is.EqualTo(quiet.Lane), $"the lane it was attacked through leads now ({string.Join(", ", axes.Axes.Select(a => $"{a.Lane}:{a.Score:F2}/{a.Observed:F1}"))})");
        Assert.That(math.distance(state.DefenceSpots[0], top.Spot), Is.LessThan(1f), "and its first defended spot is there");
    }

    // ---------- §6.2, §6.3, §7.2: how many, by difficulty ----------

    [Test]
    public void PostsRaidsAndForwardBasesScaleByDifficulty()
    {
        AiSandbox world = new AiSandbox(256, 5);
        int Posts(AiDifficulty d) => world.MakeBrain(d, AiStyle.Balanced).Context.MaxPosts;
        int Raids(AiDifficulty d) => world.MakeBrain(d, AiStyle.Balanced).Context.MaxRaids;
        int Forward(AiDifficulty d) => world.MakeBrain(d, AiStyle.Balanced).Context.MaxForwardBases;
        Assert.That((Posts(AiDifficulty.Easy), Posts(AiDifficulty.Normal), Posts(AiDifficulty.Hard)), Is.EqualTo((0, 1, 3)));
        Assert.That((Raids(AiDifficulty.Easy), Raids(AiDifficulty.Normal), Raids(AiDifficulty.Hard)), Is.EqualTo((1, 1, 3)));
        Assert.That(Forward(AiDifficulty.Easy), Is.Zero);
        Assert.That(Forward(AiDifficulty.Hard), Is.EqualTo(2));
    }

    // ---------- §6.2: posts ----------

    [Category("Slow")]
    [Test]
    public void ABigIdleArmyHoldsAPostAwayFromItsBase()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Expander);
        world.RevealAll();
        AiBuilding hq = HqOf(world, world.Ai);
        for (int i = 0; i < 40; i++) world.AddUnit(world.Ai, UnitType.Tank, (float2)hq.Anchor + new float2(-8 + i % 8, -6 - i / 8));
        PostManager posts = world.Brain.Manager<PostManager>();
        for (int t = 0; t < 400 && posts.Taken == 0; t += 5) world.Run(5.0);
        AiState state = world.Brain.Context.State;
        Assert.That(posts.Taken, Is.GreaterThanOrEqualTo(1), $"a post (opening done {state.OpeningDone}, gather {state.HasGatherPoint}, combat {state.Combat.Count})");
        Assert.That(state.Posted.Count, Is.EqualTo(world.Game.Ai.Warpath.PostSize));
        Assert.That(world.Brain.Manager<ArmyManager>().Group.Intersect(state.Posted), Is.Empty, "posted units aren't in the army's group");
    }

    // ---------- §7: spending ----------

    [Category("Slow")]
    [Test]
    public void MoneyPilingUpFortifiesAndGrowsAForwardBase()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        world.RevealAll();
        world.Resources = 1_000_000f;
        AiState state = world.Brain.Context.State;
        for (int t = 0; t < 600 && state.ForwardRegions.Count == 0; t += 5) { world.Resources = 1_000_000f; world.Run(5.0); }
        Assert.That(state.Banked, Is.True);
        Assert.That(state.Fortify, Is.True, "four times its bank limit in hand: it fortifies");
        Assert.That(state.ForwardRegions.Count, Is.GreaterThanOrEqualTo(1), $"a forward base (held {state.HeldExpansions.Count})");
        AiRegion region = world.Regions[state.ForwardRegions[0]];
        bool producer = false;
        for (int t = 0; t < 120 && !producer; t += 5)
        {
            world.Resources = 1_000_000f;
            world.Run(5.0);
            producer = world.Buildings.Any(b => b.OwnerId == world.Ai && (b.Type == BuildingType.Barracks || b.Type == BuildingType.HeavyFactory || b.Type == BuildingType.SiegeWorkshop)
                && math.distance(b.Anchor, region.Centre) <= region.Radius + 8f);
        }
        Assert.That(producer, Is.True, "a producer at its forward base");
    }

    // ---------- §4.2: its own walls ----------

    [Category("Slow")]
    [Test]
    public void ADestroyedWallTileIsPutBack()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Turtle);
        world.RevealAll();
        AiAction wall = default;
        bool found = false;
        for (int t = 0; t < 900 && !found; t += 5)
        {
            // Enough for its defences, not a bottomless bank (that floods its actions with production).
            world.Resources = math.max(world.Resources, 3000f);
            world.Run(5.0);
            foreach (AiAction a in world.Actions) if (a.Kind == AiActionKind.PlaceWall) { wall = a; found = true; break; }
        }
        AiState st = world.Brain.Context.State;
        Assert.That(found, Is.True, $"a Turtle walls a lane (spots {st.DefenceSpots.Count}, turrets {st.BuildingCount[(int)BuildingType.GunTurret]}, opening {st.OpeningDone}, playing {st.PlayingSeconds:F0}, requests {string.Join(",", st.BuildRequests.Select(r => r.Type))}, breaches {world.Brain.Context.Difficulty.Breaches}, wallLanes {world.Brain.Context.Style.WallLanes}, refused {world.IllegalPlacements.Count}, per spot {string.Join(" ", Enumerable.Range(0, st.DefenceSpots.Count).Select(i => $"{st.CountNear(BuildingType.GunTurret, st.DefenceSpots[i], world.Game.Ai.Play.DefenceSpread + 2f)}/{(i < st.DefenceWeights.Count ? st.DefenceWeights[i] : -1):F2}"))}, turrets wanted {world.Brain.Context.Style.Turrets}, blueprints {st.Blueprints.Count}, pending {st.Pending.Count}, home turrets {st.CountNear(BuildingType.GunTurret, st.Home, world.Game.Ai.Play.BaseRadius)}, producers done {st.CompleteCount[(int)BuildingType.Barracks] + st.CompleteCount[(int)BuildingType.HeavyFactory]}, waiting {world.Brain.Budget.Pending}, last actions {string.Join(",", world.Actions.Skip(System.Math.Max(0, world.Actions.Count - 200)).GroupBy(a => a.Kind + "/" + a.Order + (a.Kind == AiActionKind.PlaceBuilding ? "/" + (BuildingType)a.Value : "")).Select(g => g.Key + ":" + g.Count()))}, pending types {string.Join(",", st.Pending.GroupBy(p => p.Type).Select(g => g.Key + ":" + g.Count()))}, refused {world.Refused.Count})");
        // The line is built ...
        var line = new System.Collections.Generic.List<int2>();
        WallRules.Line(wall.Tile, wall.To, line);
        int2 tile = line.First(t => world.TileAt(t) == TileType.Ground);
        foreach (int2 t in line) if (world.TileAt(t) == TileType.Ground) world.SetTile(t, TileType.PlayerWall, world.Ai);
        world.Run(2.0);
        // ... then a tile of it is destroyed.
        world.SetTile(tile, TileType.Ground);
        int before = world.Actions.Count;
        world.Resources = math.max(world.Resources, 3000f);
        world.Run(world.Game.Ai.Warpath.WallCheckSeconds + 2.0);
        Assert.That(world.Actions.Skip(before).Any(a => a.Kind == AiActionKind.PlaceWall && a.Tile.Equals(tile) && a.To.Equals(tile)), Is.True, $"it puts the tile back {tile} (planned {world.Brain.Manager<BuilderManager>().PlannedWalls}, stood {world.Brain.Manager<BuilderManager>().StoodWalls}, rebuilt {world.Brain.Manager<BuilderManager>().RebuiltWalls}, waiting {world.Brain.Budget.Pending}, walls placed since {string.Join(" ", world.Actions.Skip(before).Where(a => a.Kind == AiActionKind.PlaceWall).Select(a => $"{a.Tile}-{a.To}"))}, line {string.Join(" ", line)})");
    }
}

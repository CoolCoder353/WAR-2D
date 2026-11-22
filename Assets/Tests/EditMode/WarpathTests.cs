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
}

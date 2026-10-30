using System.Linq;
using Config;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Ai;
using WAR2D.AiView;
using WAR2D.World;

/// <summary>v0.14 plan Task 7: the managers that play the game, in a scripted world (<see cref="AiSandbox"/>).</summary>
public class AiManagerTests
{
    [TearDown]
    public void TearDown() => ConfigLoader.ResetForTests();

    /// <summary>A sandbox with the AI's HQ placed and the match playing.</summary>
    private static AiSandbox Playing(AiDifficulty difficulty, AiStyle style, uint seed = 5)
    {
        var world = new AiSandbox(256, seed);
        world.MakeBrain(difficulty, style);
        world.ThinkNow();
        world.Run(2.0);
        Assert.That(world.Count(world.Ai, BuildingType.Base), Is.EqualTo(1), "the HQ is placed while HQs are placed");
        world.State = GameState.Playing;
        return world;
    }

    private static AiBuilding HqOf(AiSandbox world, int owner) => world.Buildings.First(b => b.OwnerId == owner && b.Type == BuildingType.Base);

    [Test]
    public void TheHqGoesInsideItsBestOption()
    {
        var world = new AiSandbox(256, 5, new[] { 0, 3, 6 });
        AiBrain brain = world.MakeBrain(AiDifficulty.Normal, AiStyle.Balanced);
        world.ThinkNow();
        world.Run(2.0);
        Assert.That(world.Actions.Count(a => a.Kind == AiActionKind.PlaceHq), Is.GreaterThanOrEqualTo(1), "it placed its HQ through the HQ action");
        Assert.That(world.Refused, Is.Empty, "and the server accepted it");
        AiBuilding hq = HqOf(world, world.Ai);
        foreach (int2 tile in Footprint.Tiles(hq.Anchor, world.Size(BuildingType.Base)))
            Assert.That(world.InOwnOptions(tile), Is.True, $"footprint tile {tile} lies inside one of its options");
        int best = brain.Manager<OpeningManager>().BestOption(brain.Context, world.View);
        Assert.That(PlacementRules.InClearing(hq.Anchor, world.Sites[best], MapGenerator.HqClearRadius), Is.True, "in the option it scored best");
        foreach (int o in world.Options)
            Assert.That(OpeningManager.Score(brain.Context, world.View, world.Sites[best]), Is.GreaterThanOrEqualTo(OpeningManager.Score(brain.Context, world.View, world.Sites[o])));
        world.Run(5.0);
        Assert.That(world.Count(world.Ai, BuildingType.Base), Is.EqualTo(1), "one HQ only");
    }

    [Test]
    public void AnOptionWithMoreGemsScoresHigher()
    {
        var world = new AiSandbox(256, 5);
        AiBrain brain = world.MakeBrain(AiDifficulty.Normal, AiStyle.Balanced);
        world.BuildView();
        int2 site = world.Sites[0];
        float before = OpeningManager.Score(brain.Context, world.View, site);
        for (int x = -3; x <= 3; x++) world.View.Tiles[(site.y + 6) * world.Width + site.x + x] = (byte)TileType.Gem;
        Assert.That(OpeningManager.Score(brain.Context, world.View, site), Is.GreaterThan(before), "gems near an option raise its score");
    }

    [TestCase(AiDifficulty.Normal, AiStyle.Expander, 5u)]
    [TestCase(AiDifficulty.Hard, AiStyle.Turtle, 9u)]
    [TestCase(AiDifficulty.Easy, AiStyle.Rusher, 13u)]
    public void PlacementIsAlwaysLegal(AiDifficulty difficulty, AiStyle style, uint seed)
    {
        AiSandbox world = Playing(difficulty, style, seed);
        world.Run(360.0);
        var placed = world.Actions.Where(a => a.Kind == AiActionKind.PlaceBuilding).ToList();
        Assert.That(placed.Count, Is.GreaterThanOrEqualTo(4), "it builds");
        Assert.That(world.IllegalPlacements, Is.Empty,
            "every placement it proposed passed the server's rules: " + string.Join(", ", world.IllegalPlacements));
        Assert.That(world.Count(world.Ai, BuildingType.Miner), Is.GreaterThanOrEqualTo(2), "Miners, each facing a gem");
        foreach (AiBuilding b in world.Buildings.Where(b => b.OwnerId == world.Ai && b.Type == BuildingType.Miner))
            Assert.That(world.TileAt(b.Anchor + MinerRules.FacingOffset(b.Rotation * 90f)), Is.EqualTo(TileType.Gem));
    }

    [Test]
    public void ASearchReturnsALegalSpotOrNone()
    {
        AiSandbox world = Playing(AiDifficulty.Normal, AiStyle.Balanced);
        world.RevealAll();
        world.BuildView();
        AiBrain brain = world.Brain;
        brain.Context.State.Update(world.View);
        Placement placement = brain.Context.State.Placement;
        AiBuilding hq = HqOf(world, world.Ai);
        foreach (BuildingType type in new[] { BuildingType.Barracks, BuildingType.HeavyFactory, BuildingType.Miner, BuildingType.GunTurret })
        {
            PlacementSearch search = PlacementSearch.Around(type, hq.Anchor, 30, 1);
            SearchResult result = SearchResult.Searching;
            int2 anchor = default;
            float rotation = 0f;
            for (int think = 0; think < 100 && result == SearchResult.Searching; think++)
            {
                int tries = 50; // a small budget: the search goes on over thinks
                result = placement.Search(ref search, ref tries, out anchor, out rotation);
            }
            Assert.That(result, Is.EqualTo(SearchResult.Found), $"{type}: a spot near its base");
            Assert.That(world.CheckPlacement(type, anchor, rotation), Is.EqualTo(PlacementResult.Ok), $"{type} at {anchor}: legal on the true map");
        }
        // Deep in the rock there is nothing: the search says so instead of returning a bad spot.
        int2 rock = default;
        for (int i = 0; i < world.Tiles.Length && rock.Equals(default(int2)); i++)
        {
            int2 t = new int2(i % world.Width, i / world.Width);
            bool solid = true;
            for (int y = -4; y <= 4 && solid; y++) for (int x = -4; x <= 4 && solid; x++) solid = world.TileAt(t + new int2(x, y)) == TileType.Wall;
            if (solid) rock = t;
        }
        PlacementSearch none = PlacementSearch.Around(BuildingType.Barracks, rock, 2, 0);
        int budget = 1000;
        Assert.That(placement.Search(ref none, ref budget, out _, out _), Is.EqualTo(SearchResult.NotFound));
    }

    [TestCase(AiStyle.Balanced, 5u)]
    [TestCase(AiStyle.Rusher, 9u)]
    [TestCase(AiStyle.Expander, 13u)]
    public void NormalNeverGoesUnpaidOverFiveMinutes(AiStyle style, uint seed)
    {
        AiSandbox world = Playing(AiDifficulty.Normal, style, seed);
        world.Run(300.0);
        Assert.That(world.UnpaidSeconds, Is.Zero, $"never unpaid (income {world.LastIncome}, upkeep {world.LastUpkeep})");
        Assert.That(world.Count(world.Ai, BuildingType.Miner), Is.GreaterThanOrEqualTo(2), "it mined");
        int producers = world.Count(world.Ai, BuildingType.Barracks, true) + world.Count(world.Ai, BuildingType.HeavyFactory, true) + world.Count(world.Ai, BuildingType.SiegeWorkshop, true);
        Assert.That(producers, Is.GreaterThanOrEqualTo(1), "built a producer");
        int army = world.Units.Count(u => u.OwnerId == world.Ai && u.Type != UnitType.Builder);
        Assert.That(army, Is.GreaterThanOrEqualTo(3), "and an army");
    }

    [Test]
    public void CountersComeFromSeenUnitsAndByDifficulty()
    {
        float SniperAndArtillery(float[] mix) => mix[(int)UnitType.Sniper] + mix[(int)UnitType.Artillery];
        float[] MixAfterSeeing(AiDifficulty difficulty, bool heavies, bool factory)
        {
            AiSandbox world = Playing(difficulty, AiStyle.Balanced);
            world.RevealAll();
            AiBuilding hq = HqOf(world, world.Ai);
            if (heavies) for (int i = 0; i < 10; i++) world.AddUnit(world.Enemy, UnitType.Heavy, (float2)hq.Anchor + new float2(4 + i % 3, 4 + i / 3));
            if (factory) world.AddBuilding(world.Enemy, BuildingType.HeavyFactory, hq.Anchor + new int2(9, 0));
            world.ThinkNow();
            ProductionManager production = world.Brain.Manager<ProductionManager>();
            production.BuildMix(world.Brain.Context, world.Brain.Context.State);
            return (float[])production.Mix.Clone();
        }
        float[] normalNone = MixAfterSeeing(AiDifficulty.Normal, false, false);
        float[] normalHeavies = MixAfterSeeing(AiDifficulty.Normal, true, false);
        float[] easyNone = MixAfterSeeing(AiDifficulty.Easy, false, false);
        float[] easyHeavies = MixAfterSeeing(AiDifficulty.Easy, true, false);
        float[] normalFactory = MixAfterSeeing(AiDifficulty.Normal, false, true);
        float[] hardFactory = MixAfterSeeing(AiDifficulty.Hard, false, true);
        Assert.That(SniperAndArtillery(normalHeavies), Is.GreaterThan(SniperAndArtillery(normalNone) + 0.1f), "Normal: Heavies seen, more Snipers and Artillery");
        Assert.That(easyHeavies, Is.EqualTo(easyNone).Within(1e-5f), "Easy keeps its style's mix");
        Assert.That(normalFactory, Is.EqualTo(normalNone).Within(1e-5f), "Normal doesn't counter buildings");
        Assert.That(SniperAndArtillery(hardFactory), Is.GreaterThan(SniperAndArtillery(normalNone) + 0.1f), "Hard: a Heavy Factory seen, anti-Heavy");
        Assert.That(normalHeavies.Sum(), Is.EqualTo(1f).Within(1e-4f), "shares sum to one");
    }

    /// <summary>A playing sandbox with an army of Tanks at its gathering point and the enemy's HQ known, past the first attack time.</summary>
    private static AiSandbox ReadyToAttack(AiDifficulty difficulty, out int2 enemyHq, int tanks = 14)
    {
        AiSandbox world = Playing(difficulty, AiStyle.Rusher);
        world.RevealAll();
        AiBuilding hq = HqOf(world, world.Ai);
        int far = 0;
        float farthest = 0f;
        for (int i = 0; i < world.Sites.Length; i++)
        {
            float d = math.distance(world.Sites[i], hq.Anchor);
            if (d > farthest) { farthest = d; far = i; }
        }
        enemyHq = world.Sites[far];
        world.AddBuilding(world.Enemy, BuildingType.Base, enemyHq);
        for (int i = 0; i < tanks; i++) world.AddUnit(world.Ai, UnitType.Tank, (float2)hq.Anchor + new float2(-6 + i % 4, -6 - i / 4));
        world.Run(world.Game.Ai.GetStyle(AiStyle.Rusher).FirstAttackSeconds + 5.0);
        return world;
    }

    [Test]
    public void AnAttackTargetsOnlyBuildingsItKnows()
    {
        AiSandbox world = Playing(AiDifficulty.Normal, AiStyle.Rusher);
        AiBuilding hq = HqOf(world, world.Ai);
        int2 enemyHq = world.Sites.OrderByDescending(s => math.distance(s, hq.Anchor)).First();
        world.AddBuilding(world.Enemy, BuildingType.Base, enemyHq);
        for (int i = 0; i < 14; i++) world.AddUnit(world.Ai, UnitType.Tank, (float2)hq.Anchor + new float2(-6 + i % 4, -6 - i / 4));
        int enemyId = world.Buildings.First(b => b.OwnerId == world.Enemy).Id;
        ArmyManager army = world.Brain.Manager<ArmyManager>();
        // Past its first attack time with a big army, it attacks only once its side has seen the enemy HQ (its scout finds it).
        for (int step = 0; step < world.Game.Ai.GetStyle(AiStyle.Rusher).FirstAttackSeconds + 60 && army.Attacks == 0; step++)
        {
            world.Run(1.0);
            if (army.Attacks == 0) continue;
            Assert.That(world.FirstSeen.ContainsKey(enemyId), Is.True, "it attacked: so it had seen the enemy HQ");
        }
        if (army.Attacks == 0)
        {
            Assert.That(world.FirstSeen.ContainsKey(enemyId), Is.False, "a big army, but no target it knows: no attack");
            // Someone of its side sees the enemy HQ once: now it knows it (a ghost after), and attacks it.
            int spotter = world.AddUnit(world.Ai, UnitType.Scout, (float2)enemyHq + new float2(0, 9));
            world.Run(2.0);
            world.Units.RemoveAll(u => u.Id == spotter);
            world.Run(10.0);
        }
        // It stages short of the target first, then engages (at most StageSeconds later).
        for (int t = 0; t < world.Game.Ai.Play.StageSeconds + 30 && !world.Actions.Any(a => a.Kind == AiActionKind.Order && a.Order == OrderKind.AttackMove && a.Tile.Equals(enemyHq)); t++) world.Run(1.0);
        Assert.That(army.Attacks, Is.GreaterThanOrEqualTo(1), $"it attacks the building it has seen (seen {world.FirstSeen.ContainsKey(enemyId)}, known {world.Brain.Board.EnemyBuildings.Count}, mode {army.Mode}, playing {world.Brain.Context.State.PlayingSeconds}, combat {world.Brain.Context.State.Combat.Count}, scouts {world.Brain.Context.State.Scouts.Count})");
        Assert.That(army.TargetId, Is.EqualTo(enemyId), "the enemy HQ");
        double seen = world.FirstSeen[enemyId];
        for (int i = 0; i < world.Actions.Count; i++)
            if (world.Actions[i].Kind == AiActionKind.Order && world.Actions[i].Order == OrderKind.AttackMove && math.distance(world.Actions[i].Tile, enemyHq) < 8f)
                Assert.That(world.ActionTimes[i], Is.GreaterThanOrEqualTo(seen), "never sent at the HQ before its side saw it");
        Assert.That(world.Actions.Any(a => a.Kind == AiActionKind.Order && a.Order == OrderKind.AttackMove && a.Tile.Equals(enemyHq)), Is.True, "attack-moving at its anchor");
        foreach (AiAction a in world.Actions.Where(a => a.Kind == AiActionKind.Order && a.Order == OrderKind.AttackMove))
            Assert.That(world.View.Buildings.Any(b => b.OwnerId == world.Enemy && b.Anchor.Equals(a.Tile)) || math.distance(a.Tile, world.Brain.Context.State.GatherPoint) < 20f || math.distance(a.Tile, enemyHq) < world.Game.Ai.Play.StageDistance + world.Game.Ai.Play.DefenceSpread + 2f,
                Is.True, $"every attack-move goes at what it knows ({a.Tile})");
    }

    [TestCase(AiDifficulty.Normal, true)]
    [TestCase(AiDifficulty.Hard, true)]
    [TestCase(AiDifficulty.Easy, false)]
    public void ItRetreatsWhenOutnumbered(AiDifficulty difficulty, bool retreats)
    {
        AiSandbox world = ReadyToAttack(difficulty, out int2 enemyHq);
        world.AddUnit(world.Ai, UnitType.Scout, (float2)enemyHq + new float2(0, 9)); // it knows the enemy HQ
        world.Run(3.0);
        ArmyManager army = world.Brain.Manager<ArmyManager>();
        Assert.That(army.Mode, Is.EqualTo(ArmyMode.Attack), "it attacks with its Tanks");
        // Halfway there it meets a much bigger enemy force.
        float2 centre = 0;
        int n = 0;
        // Around the attack group's front unit (the group may be strung out between home and the target).
        AiUnit front = world.Units.Where(u => army.Group.Contains(u.Id)).OrderBy(u => math.distance(u.Position, enemyHq)).First();
        foreach (AiUnit u in world.Units.Where(u => army.Group.Contains(u.Id) && math.distance(u.Position, front.Position) < 10f)) { centre += u.Position; n++; }
        centre /= n;
        // Its whole side's worth twice over, in Heavies, right on its army.
        int heavies = math.max(30, world.Units.Count(u => u.OwnerId == world.Ai) * 2);
        for (int i = 0; i < heavies; i++) world.AddUnit(world.Enemy, UnitType.Heavy, centre + new float2(-5 + i % 10, -5 + (i / 10) % 10));
        int before = world.Actions.Count;
        world.Run(2.0);
        string why = $"mode {army.Mode}, group {army.Group.Count}, centre {centre}, seen {world.View.OtherUnits.Count}, own {world.View.OwnUnits.Count}, attacks {army.Attacks}, target {army.Target}";
        if (retreats)
        {
            Assert.That(army.Retreats, Is.EqualTo(1), "outnumbered: it pulls back; " + why);
            Assert.That(army.Mode, Is.Not.EqualTo(ArmyMode.Attack), "and stops attacking");
            int2 gather = world.Brain.Context.State.GatherPoint;
            Assert.That(world.Actions.Skip(before).Any(a => a.Kind == AiActionKind.Order && a.Order == OrderKind.Move && a.Tile.Equals(gather)), Is.True, "a Move back to its gathering point");
        }
        else Assert.That((army.Mode, army.Retreats), Is.EqualTo((ArmyMode.Attack, 0)), "Easy fights on");
    }
}

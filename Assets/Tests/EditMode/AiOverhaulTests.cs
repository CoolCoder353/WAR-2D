using System.Collections.Generic;
using System.Linq;
using Config;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Ai;
using WAR2D.AiView;

/// <summary>
/// The AI overhaul (spec 2026-10-30): one policy plus handicaps, the brain's dice and variation, the two budget pools,
/// the fight model, the kill ledger and enemy models, scored targets, raids and the strategy's reactions.
/// </summary>
public class AiOverhaulTests
{
    [TearDown]
    public void TearDown() => ConfigLoader.ResetForTests();

    private static GameConfigData Game()
    {
        ConfigLoader.ResetForTests();
        return ConfigLoader.LoadConfig();
    }

    // ---------- §2: one policy plus handicaps ----------

    [Test]
    public void HardIsThePolicy()
    {
        AiConfig ai = Game().Ai;
        AiPolicyConfig p = ai.Policy;
        AiDifficultyConfig hard = ai.GetDifficulty(AiDifficulty.Hard);
        Assert.AreEqual((p.Builders, p.BuildersPerExpansion, p.MaxExpansions, p.Scouts, p.ExtraProducers), (hard.Builders, hard.BuildersPerExpansion, hard.MaxExpansions, hard.Scouts, hard.ExtraProducers));
        Assert.AreEqual((p.ExpandAfterSeconds, p.RescoutSeconds, p.AttackAdvantage, p.RetreatAdvantage), (hard.ExpandAfterSeconds, hard.RescoutSeconds, hard.AttackAdvantage, hard.RetreatAdvantage));
        Assert.AreEqual((1f, 1f, 1f, 0f), (hard.CounterWeight, hard.BuildingCounterWeight, hard.SpecialUse, hard.Mistakes));
        Assert.That(hard.Scouting, Is.EqualTo(AiScouting.RegularWithRadar));
    }

    [Test]
    public void EveryResolvedNumberIsMonotoneFromEasyToHard()
    {
        AiConfig ai = Game().Ai;
        AiDifficultyConfig easy = ai.GetDifficulty(AiDifficulty.Easy), normal = ai.GetDifficulty(AiDifficulty.Normal), hard = ai.GetDifficulty(AiDifficulty.Hard);
        void Better(string name, System.Func<AiDifficultyConfig, float> f)
        {
            Assert.That(f(easy), Is.LessThanOrEqualTo(f(normal)), $"{name}: Easy ≤ Normal");
            Assert.That(f(normal), Is.LessThanOrEqualTo(f(hard)), $"{name}: Normal ≤ Hard");
        }
        Better("Builders", d => d.Builders);
        Better("BuildersPerExpansion", d => d.BuildersPerExpansion);
        Better("MaxExpansions", d => d.MaxExpansions);
        Better("Scouts", d => d.Scouts);
        Better("CounterWeight", d => d.CounterWeight);
        Better("BuildingCounterWeight", d => d.BuildingCounterWeight);
        Better("SpecialUse", d => d.SpecialUse);
        Better("ExtraProducers", d => d.ExtraProducers);
        Better("ArmyMemoryScale", d => d.ArmyMemoryScale);
        Better("ActionsPerMinute", d => d.ActionsPerMinute);
        Better("Scouting", d => (float)d.Scouting);
        Better("SaturatesMiners", d => d.SaturatesMiners ? 1f : 0f);
        Better("-ExpandAfterSeconds", d => -d.ExpandAfterSeconds);
        Better("-RescoutSeconds", d => -d.RescoutSeconds);
        Better("-Mistakes", d => -d.Mistakes);
        Better("-ReactionSeconds", d => -d.ReactionSeconds);
        Better("-ThinkSeconds", d => -d.ThinkSeconds);
    }

    [Test]
    public void ABetterHandicapNeverPlaysWorse()
    {
        AiPolicyConfig p = Game().Ai.Policy;
        AiHandicapConfig H(float v) => new AiHandicapConfig { ThinkSeconds = 1f, ActionsPerMinute = 60, ReactionSeconds = 1f, Insight = v, Attention = v, Ambition = v, Mistakes = 1f - v };
        AiDifficultyConfig last = AiPolicy.Resolve(p, H(0f));
        for (float v = 0.05f; v <= 1.0001f; v += 0.05f)
        {
            AiDifficultyConfig d = AiPolicy.Resolve(p, H(v));
            Assert.That(d.Builders, Is.GreaterThanOrEqualTo(last.Builders), $"at {v}");
            Assert.That(d.MaxExpansions, Is.GreaterThanOrEqualTo(last.MaxExpansions), $"at {v}");
            Assert.That(d.Scouts, Is.GreaterThanOrEqualTo(last.Scouts), $"at {v}");
            Assert.That(d.ExpandAfterSeconds, Is.LessThanOrEqualTo(last.ExpandAfterSeconds), $"at {v}");
            Assert.That(d.RescoutSeconds, Is.LessThanOrEqualTo(last.RescoutSeconds), $"at {v}");
            Assert.That((int)d.Scouting, Is.GreaterThanOrEqualTo((int)last.Scouting), $"at {v}");
            last = d;
        }
    }

    // ---------- §2.3, §6: dice and variation ----------

    [Test]
    public void MistakesZeroNeverSlips()
    {
        var dice = new AiDice(7, 0f, 10f);
        for (int i = 0; i < 1000; i++) Assert.That(dice.Slip(AiSlip.Production, i * 11.0), Is.False);
    }

    [Test]
    public void ASlipIsRolledAtMostOncePerWindowAtTheMistakeRate()
    {
        var once = new AiDice(3, 1f, 10f);
        Assert.That(once.Slip(AiSlip.Retreat, 0), Is.True, "the first ask in a window rolls");
        Assert.That(once.Slip(AiSlip.Retreat, 5), Is.False, "later asks in it don't");
        Assert.That(once.Slip(AiSlip.Target, 5), Is.True, "each kind has its own window");
        var dice = new AiDice(11, 0.25f, 10f);
        int slips = 0;
        for (int i = 0; i < 10000; i++) if (dice.Slip(AiSlip.Upkeep, i * 10.0)) slips++;
        Assert.That(slips / 10000f, Is.EqualTo(0.25f).Within(0.03f));
    }

    [Test]
    public void NoiseIsOneAtFullInsightAndBoundedBelowIt()
    {
        var dice = new AiDice(5, 0f, 10f);
        Assert.That(dice.Noise(1f, 0.4f), Is.EqualTo(1f));
        for (int i = 0; i < 1000; i++) Assert.That(dice.Noise(0.5f, 0.4f), Is.InRange(0.8f, 1.2f));
    }

    [Test]
    public void SeedZeroDoesNotVary()
    {
        AiConfig ai = Game().Ai;
        var variation = new AiVariation(new AiDice(0, 0f, 10f), ai.Policy);
        AiStyleConfig style = ai.GetStyle(AiStyle.Balanced), varied = variation.Apply(style);
        Assert.That(variation.Varies, Is.False);
        Assert.AreEqual((style.FirstAttackSeconds, style.AttackArmySize, style.AttackIntervalSeconds), (varied.FirstAttackSeconds, varied.AttackArmySize, varied.AttackIntervalSeconds));
        Assert.That(varied.BuildOrder, Is.EqualTo(style.BuildOrders[0]));
        Assert.That(varied.ArmyMix, Is.EqualTo(style.ArmyMix));
        Assert.That(variation.GatherAngle, Is.Zero);
    }

    [Test]
    public void VariedStylesStayInBoundsAndPickEveryOpening()
    {
        AiConfig ai = Game().Ai;
        AiStyleConfig style = ai.GetStyle(AiStyle.Balanced);
        float v = ai.Policy.Variation;
        var openings = new HashSet<int>();
        for (uint seed = 1; seed <= 200; seed++)
        {
            AiStyleConfig s = new AiVariation(new AiDice(seed, 0f, 10f), ai.Policy).Apply(style);
            Assert.That(s.FirstAttackSeconds, Is.InRange(style.FirstAttackSeconds * (1f - v) - 1e-3f, style.FirstAttackSeconds * (1f + v) + 1e-3f));
            Assert.That(s.AttackArmySize, Is.InRange((int)math.floor(style.AttackArmySize * (1f - v)), (int)math.ceil(style.AttackArmySize * (1f + v))));
            foreach (KeyValuePair<UnitType, float> w in style.ArmyMix) Assert.That(s.ArmyMix[w.Key], Is.InRange(w.Value * (1f - v) - 1e-4f, w.Value * (1f + v) + 1e-4f));
            openings.Add(System.Array.IndexOf(style.BuildOrders, s.BuildOrder));
        }
        Assert.That(openings.Count, Is.EqualTo(style.BuildOrders.Length), "every opening gets played");
        AiStyleConfig a = new AiVariation(new AiDice(42, 0f, 10f), ai.Policy).Apply(style), b = new AiVariation(new AiDice(42, 0f, 10f), ai.Policy).Apply(style);
        Assert.AreEqual((a.FirstAttackSeconds, a.AttackArmySize), (b.FirstAttackSeconds, b.AttackArmySize), "same seed, same AI");
        Assert.That(style.ArmyMix, Is.Not.SameAs(a.ArmyMix), "the config's style is never changed");
    }

    // ---------- §5.1: two budget pools ----------

    [Test]
    public void EconomyIsTakenWhenArmyOrdersFlood()
    {
        var budget = new ActionBudget(30, burstSeconds: 5f, waitSeconds: 10f, economyShare: 0.4f);
        budget.Refill(0);
        var taken = new List<AiActionKind>();
        for (int i = 0; i < 20; i++) budget.Propose(AiPriority.High, AiAction.OrderUnits(new[] { i + 1 }, OrderKind.Move, new int2(i, i)), i + 1, 0, army: true);
        budget.Propose(AiPriority.Low, AiAction.ChangeQueues(new[] { 99 }, 1), 100, 0);
        budget.Spend(0, a => { taken.Add(a.Kind); return true; });
        Assert.That(taken, Does.Contain(AiActionKind.ChangeQueues), "production isn't starved by a flood of orders");
    }

    [Test]
    public void APoolWithNothingWaitingLendsItsTokens()
    {
        var budget = new ActionBudget(60, burstSeconds: 10f, waitSeconds: 10f, economyShare: 0.4f);
        budget.Refill(0);
        for (int i = 0; i < 20; i++) budget.Propose(AiPriority.Normal, AiAction.OrderUnits(new[] { i + 1 }, OrderKind.Move, default), i + 1, 0, army: true);
        Assert.That(budget.Spend(0, _ => true), Is.EqualTo(10), "all ten saved-up tokens go to the army");
    }

    // ---------- §3.1: the fight model ----------

    private static Force Of(GameConfigData g, UnitType type, int count)
    {
        var f = new Force();
        for (int i = 0; i < count; i++) FightModel.AddUnit(ref f, g, type, 1f, g.Ai.Policy);
        return f;
    }

    private static int SameCost(GameConfigData g, UnitType type, float budget) => (int)math.round(budget / g.Units[type].UpfrontCost);

    [Test]
    public void EqualForcesAreEvenAndDoubleIsFourTimes()
    {
        GameConfigData g = Game();
        float w = g.Ai.Policy.RangeWeight;
        Assert.That(FightModel.Advantage(Of(g, UnitType.Tank, 10), Of(g, UnitType.Tank, 10), w), Is.EqualTo(1f).Within(1e-3f));
        Assert.That(FightModel.Advantage(Of(g, UnitType.Tank, 20), Of(g, UnitType.Tank, 10), w), Is.EqualTo(4f).Within(1e-2f), "Lanchester's square law");
    }

    [Test]
    public void TheDamageTableDecidesEqualCostFights()
    {
        GameConfigData g = Game();
        float w = g.Ai.Policy.RangeWeight;
        float flames = FightModel.Advantage(Of(g, UnitType.Flamethrower, SameCost(g, UnitType.Flamethrower, 1400)), Of(g, UnitType.Scout, SameCost(g, UnitType.Scout, 1400)), w);
        Assert.That(flames, Is.GreaterThan(1f), "Flamethrowers' splash beats massed Scouts at equal cost");
    }

    [Test]
    public void TurretsFightForTheDefendersAndNothingIsInfinite()
    {
        GameConfigData g = Game();
        Force attackers = Of(g, UnitType.Scout, 4);
        var turrets = new Force();
        for (int i = 0; i < 3; i++) FightModel.AddTurret(ref turrets, g, BuildingType.GunTurret, 1f);
        Assert.That(turrets.Empty, Is.False);
        Assert.That(FightModel.Advantage(attackers, turrets, g.Ai.Policy.RangeWeight), Is.LessThan(1f), "four Scouts lose to three turrets");
        Assert.That(FightModel.Advantage(attackers, new Force(), 0f), Is.EqualTo(float.PositiveInfinity));
        Assert.That(FightModel.Advantage(new Force(), attackers, 0f), Is.Zero);
        Assert.That(FightModel.Survivors(4f), Is.EqualTo(math.sqrt(0.75f)).Within(1e-4f));
    }

    [Test]
    public void RangeHelpsTheLongerSide()
    {
        GameConfigData g = Game();
        Force artillery = Of(g, UnitType.Artillery, 6), heavies = Of(g, UnitType.Heavy, 6);
        Assert.That(FightModel.Advantage(artillery, heavies, 0.5f), Is.GreaterThan(FightModel.Advantage(artillery, heavies, 0f)));
    }

    // ---------- the sandbox ----------

    private static AiSandbox Playing(AiDifficulty difficulty, AiStyle style, uint seed = 5)
    {
        var world = new AiSandbox(256, seed);
        world.MakeBrain(difficulty, style);
        world.ThinkNow();
        world.Run(2.0);
        Assert.That(world.Count(world.Ai, BuildingType.Base), Is.EqualTo(1), "the HQ is placed while HQs are placed");
        world.State = GameState.Playing;
        world.ThinkNow();
        return world;
    }

    private static AiBuilding HqOf(AiSandbox world, int owner) => world.Buildings.First(b => b.OwnerId == owner && b.Type == BuildingType.Base);

    // ---------- §4.1, §4.2: the kill ledger and enemy models ----------

    [Test]
    public void AUnitThatVanishesInSightDiedAndOwnLossesCount()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        AiBuilding hq = HqOf(world, world.Ai);
        float2 near = (float2)hq.Anchor + new float2(2f, 6f);
        for (int i = 0; i < 4; i++) world.AddUnit(world.Ai, UnitType.Tank, near + new float2(i, 0f));
        int enemy = world.AddUnit(world.Enemy, UnitType.Tank, near + new float2(1f, 1f));
        world.ThinkNow();
        float before = world.Brain.Board.EnemyLost;
        world.Units.RemoveAll(u => u.Id == enemy);
        world.ThinkNow();
        Assert.That(world.Brain.Board.EnemyLost - before, Is.EqualTo(world.Game.Units[UnitType.Tank].UpfrontCost), "it saw the Tank die");
        int own = world.Units.First(u => u.OwnerId == world.Ai && u.Type == UnitType.Tank).Id;
        float lost = world.Brain.Board.OwnLost;
        world.Units.RemoveAll(u => u.Id == own);
        world.ThinkNow();
        Assert.That(world.Brain.Board.OwnLost - lost, Is.EqualTo(world.Game.Units[UnitType.Tank].UpfrontCost));
    }

    [Test]
    public void EnemyModelsRememberAndGuess()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        AiBuilding hq = HqOf(world, world.Ai);
        float2 near = (float2)hq.Anchor + new float2(2f, 6f);
        var tanks = new List<int>();
        for (int i = 0; i < world.Game.Ai.Policy.RushUnits + 1; i++) tanks.Add(world.AddUnit(world.Enemy, UnitType.Tank, near + new float2(i, 0f)));
        world.ThinkNow();
        EnemyModel model = world.Brain.Board.Models[world.Enemy];
        Assert.That(model.Peak[(int)UnitType.Tank], Is.EqualTo(tanks.Count));
        Assert.That(model.Guess, Is.EqualTo(EnemyStyleGuess.Rusher), "an early army");
        world.Units.RemoveAll(u => u.OwnerId == world.Enemy);
        world.Run(world.Game.Ai.Policy.ModelHalfLife);
        world.ThinkNow();
        Assert.That(model.Peak[(int)UnitType.Tank], Is.EqualTo(tanks.Count * 0.5f).Within(tanks.Count * 0.1f), "remembered, halving each half-life");
    }

    [Test]
    public void MoreTurretsThanTheEarlyOnesGuessTurtle()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        world.RevealAll();
        AiBuilding hq = HqOf(world, world.Ai);
        int early = world.Game.Ai.Policy.EarlyTurrets + 1;
        for (int i = 0; i < early; i++) world.AddBuilding(world.Enemy, BuildingType.GunTurret, hq.Anchor + new int2(10 + 2 * i, 0));
        world.AddUnit(world.Ai, UnitType.Tank, (float2)hq.Anchor + new float2(12f, 3f)); // sees them
        world.Run(1.0);
        world.ThinkNow();
        Assert.That(world.Brain.Board.Models[world.Enemy].Guess, Is.Not.EqualTo(EnemyStyleGuess.Turtle), "the early turrets every AI builds are no sign of a Turtle");
        world.AddBuilding(world.Enemy, BuildingType.GunTurret, hq.Anchor + new int2(10 + 2 * early, 0));
        world.AddUnit(world.Ai, UnitType.Tank, (float2)hq.Anchor + new float2(10f + 2 * early, 3f)); // sees it
        world.Run(1.0);
        world.ThinkNow();
        Assert.That(world.Brain.Board.Models[world.Enemy].Guess, Is.EqualTo(EnemyStyleGuess.Turtle), "more than that is");
    }

    // ---------- §3.4: scored targets ----------

    [Test]
    public void ASoftMinerBeatsATurretGuardedHq()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        AiBuilding hq = HqOf(world, world.Ai);
        int2 enemyHq = world.Sites.OrderByDescending(s => math.distance(s, hq.Anchor)).First();
        world.AddBuilding(world.Enemy, BuildingType.Base, enemyHq);
        for (int i = 0; i < 8; i++) world.AddBuilding(world.Enemy, BuildingType.GunTurret, enemyHq + new int2(-4 + (i % 4) * 2, i < 4 ? -3 : 6));
        int2 minerAt = world.Sites.Where(s => !s.Equals(enemyHq)).OrderBy(s => math.distance(s, hq.Anchor)).Skip(1).First();
        int miner = world.AddBuilding(world.Enemy, BuildingType.Miner, minerAt);
        for (int i = 0; i < 6; i++) world.AddUnit(world.Ai, UnitType.Tank, (float2)hq.Anchor + new float2(-6 + i, -6));
        // Its side has seen all of it (each building a ghost now).
        foreach (AiBuilding b in world.Buildings.Where(b => b.OwnerId == world.Enemy).ToList())
        {
            int spotter = world.AddUnit(world.Ai, UnitType.Scout, (float2)b.Anchor + new float2(0, 4));
            world.Run(0.5);
            world.Units.RemoveAll(u => u.Id == spotter);
        }
        world.ThinkNow();
        AiContext ai = world.Brain.Context;
        Assert.That(ai.Board.EnemyBuildings.ContainsKey(miner), Is.True, "it knows the Miner");
        Assert.That(ArmyManager.ChooseTarget(ai, ai.State.Home, -1, ai.Difficulty.AttackAdvantage, out int id, out _, out _), Is.True);
        Assert.That(id, Is.EqualTo(miner), "six Tanks don't take on eight turrets: the Miner instead");
    }

    [Test]
    public void AnAvoidedTargetScoresLess()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        world.RevealAll();
        AiBuilding hq = HqOf(world, world.Ai);
        int2 at = hq.Anchor + new int2(20, 0);
        world.AddBuilding(world.Enemy, BuildingType.Barracks, at);
        for (int i = 0; i < 4; i++) world.AddUnit(world.Ai, UnitType.Tank, (float2)at + new float2(-3 + i, -4));
        world.ThinkNow();
        AiContext ai = world.Brain.Context;
        Force ours = AiForces.Own(ai);
        float plain = ArmyManager.ScoreTarget(ai, ours, ai.State.Home, world.Enemy, at, 200f, 1f, 1f);
        ai.Board.Avoid(at, ai.Now + 100);
        float avoided = ArmyManager.ScoreTarget(ai, ours, ai.State.Home, world.Enemy, at, 200f, 1f, 1f);
        Assert.That(avoided, Is.EqualTo(plain * ai.Policy.AvoidFactor).Within(1e-3f));
    }

    // ---------- §3.2: the upkeep planner ----------

    [Test]
    public void ProducersLeaveRoomForAnArmy()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        AiState state = world.Brain.Context.State;
        state.OpeningDone = true;
        float share = world.Game.Ai.Policy.BuildingUpkeepShare;
        float roomLeft = state.IncomeSoon * share - state.BuildingUpkeep;
        Assert.That(state.MayAddBuildingUpkeep(roomLeft + 1f), Is.False, "past its share of income");
        Assert.That(state.MayAddBuildingUpkeep(math.max(0f, roomLeft - 1f)), Is.True);
        state.OpeningDone = false;
        Assert.That(state.MayAddBuildingUpkeep(roomLeft + 1000f), Is.True, "the opening is exempt");
    }

    // ---------- §3.5, §4.3: raids and the strategy ----------

    [Test]
    public void ARaidGoesForAnUndefendedMiner()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Rusher);
        world.RevealAll();
        AiContext ai = world.Brain.Context;
        world.Run(3.0);
        Assert.That(ai.State.HasGatherPoint, Is.True);
        ai.State.OpeningDone = true;
        float2 gather = (float2)ai.State.GatherPoint + 0.5f;
        for (int i = 0; i < 4; i++) world.AddUnit(world.Ai, UnitType.Scout, gather + new float2(i * 0.5f, 0f));
        AiBuilding hq = HqOf(world, world.Ai);
        int2 minerAt = world.Sites.OrderByDescending(s => math.distance(s, hq.Anchor)).First();
        int miner = world.AddBuilding(world.Enemy, BuildingType.Miner, minerAt);
        int spotter = world.AddUnit(world.Ai, UnitType.Scout, (float2)minerAt + new float2(0, 4));
        world.Run(0.5);
        world.Units.RemoveAll(u => u.Id == spotter);
        RaidManager raid = world.Brain.Manager<RaidManager>();
        for (int i = 0; i < 40 && !raid.Active; i++) world.Run(0.25);
        Assert.That(raid.Active, Is.True, "four Scouts at home and an undefended Miner it knows: a raid");
        Assert.That(raid.TargetId, Is.EqualTo(miner));
        Assert.That(ai.State.Raiders.Count, Is.InRange(2, ai.Policy.RaidSize), "the raiders are out of the army");
    }

    [Test]
    public void TurtleNeverRaids()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Turtle);
        Assert.That(world.Game.Ai.GetStyle(AiStyle.Turtle).Raids, Is.False);
        world.Run(5.0);
        Assert.That(world.Brain.Manager<RaidManager>().Raids, Is.Zero);
    }

    [Test]
    public void ARushExpectedAddsATurretAndHoldsTheFirstAttack()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Balanced);
        AiBuilding hq = HqOf(world, world.Ai);
        float2 near = (float2)hq.Anchor + new float2(2f, 6f);
        for (int i = 0; i < world.Game.Ai.Policy.RushUnits; i++) world.AddUnit(world.Enemy, UnitType.Tank, near + new float2(i, 0f));
        world.ThinkNow();
        AiState state = world.Brain.Context.State;
        Assert.That(state.RushExpected, Is.True);
        Assert.That(state.ExtraTurrets, Is.EqualTo(1));
        Assert.That(state.HoldFirstAttack, Is.True);
        Assert.That(state.MixBias[(int)UnitType.Flamethrower], Is.GreaterThan(1f));
    }

    [Test]
    public void TwoBadTradesTurnARusherIntoAnExpander()
    {
        AiSandbox world = Playing(AiDifficulty.Hard, AiStyle.Rusher);
        AiContext ai = world.Brain.Context;
        ai.State.BadTradeStreak = 2;
        // The Army manager writes its streak each think; a strategy-only brain shows the switch on its own.
        var strategy = new StrategyManager();
        strategy.Think(ai);
        Assert.That(ai.StyleName, Is.EqualTo(AiStyle.Expander));
        Assert.That(strategy.Switches, Has.Count.EqualTo(1));
        ai.State.BadTradeStreak = 2;
        strategy.Think(ai);
        Assert.That(strategy.Switches, Has.Count.EqualTo(1), "at most one switch per StyleSwitchSeconds");
    }
}

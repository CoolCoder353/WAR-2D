using System.Collections.Generic;
using System.Linq;
using Config;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Ai;
using WAR2D.AiView;

/// <summary>v0.14 plan Task 6: the AI director's schedule, the action budget, the reaction delay and the blackboard.</summary>
public class AiDirectorTests
{
    private sealed class CountingBrain : IAiBrain
    {
        public float ThinkSeconds { get; set; } = 0.25f;
        public int Thinks;
        public void Think(TeamView view) => Thinks++;
    }

    private sealed class FakeHost : IAiHost
    {
        public GameState State { get; set; } = GameState.Playing;
        public bool Paused { get; set; }
        public double Now { get; set; }
        public readonly List<AiSeat> List = new List<AiSeat>();
        public IReadOnlyList<AiSeat> Seats => List;
        public int Views;
        public TeamView BuildView(AiSeat seat) { Views++; return new TeamView { Owner = seat.OwnerId, Time = Now }; }
    }

    private static FakeHost HostWith(int seats, float thinkSeconds)
    {
        var host = new FakeHost();
        for (int i = 0; i < seats; i++)
            host.List.Add(new AiSeat(AiOwnerIds.For(i), new ServerPlayer(null, 0f)) { Difficulty = AiDifficulty.Hard, Brain = new CountingBrain { ThinkSeconds = thinkSeconds } });
        return host;
    }

    private static int ThinksOf(AiSeat seat) => ((CountingBrain)seat.Brain).Thinks;

    [Test]
    public void AtMostOneAiThinksPerTickAcrossSeven()
    {
        FakeHost host = HostWith(7, 0.25f); // Hard: due every 5 ticks, so 7 seats always have one due
        var director = new AiDirector();
        const int steps = 140;
        for (int t = 0; t < steps; t++)
        {
            host.Now += 0.05;
            int before = host.List.Sum(ThinksOf);
            director.Step(host);
            Assert.That(host.List.Sum(ThinksOf) - before, Is.LessThanOrEqualTo(1), $"step {t}");
            Assert.That(director.LastThinker, Is.Not.Null, "a seat is always due");
        }
        Assert.That(host.List.Sum(ThinksOf), Is.EqualTo(steps), "one think every step");
        foreach (AiSeat seat in host.List) Assert.That(ThinksOf(seat), Is.EqualTo(steps / 7), "round robin: an even share each");
        Assert.That(host.Views, Is.EqualTo(steps), "a view is built only for the seat that thinks");
    }

    [Test]
    public void ASeatThinksNoMoreOftenThanItsThinkSeconds()
    {
        FakeHost host = HostWith(2, 1f); // Easy
        var director = new AiDirector();
        for (int t = 0; t < 200; t++) { host.Now += 0.05; director.Step(host); } // 10 s
        foreach (AiSeat seat in host.List) Assert.That(ThinksOf(seat), Is.InRange(10, 11));
    }

    [Test]
    public void TheDirectorIdlesOutsidePlacingHqAndPlayingAndWhilePaused()
    {
        FakeHost host = HostWith(3, 0.25f);
        var director = new AiDirector();
        foreach (GameState state in new[] { GameState.Lobby, GameState.Countdown, GameState.GameOver })
        {
            host.State = state;
            for (int t = 0; t < 20; t++) { host.Now += 0.05; director.Step(host); }
        }
        host.State = GameState.Playing;
        host.Paused = true;
        for (int t = 0; t < 20; t++) { host.Now += 0.05; director.Step(host); }
        Assert.That(host.List.Sum(ThinksOf), Is.Zero, "no thinks");
        Assert.That(host.Views, Is.Zero, "no views built");
        Assert.That(director.LastStepMs, Is.Zero);

        host.Paused = false;
        foreach (GameState state in new[] { GameState.PlacingHQ, GameState.Playing })
        {
            host.State = state;
            int before = host.List.Sum(ThinksOf);
            director.Step(host);
            Assert.That(host.List.Sum(ThinksOf), Is.EqualTo(before + 1), $"thinks in {state}");
        }
    }

    [Test]
    public void ABrainThatThrowsNeverStopsTheOthers()
    {
        FakeHost host = HostWith(2, 0.25f);
        var thrower = new AiSeat(AiOwnerIds.For(9), new ServerPlayer(null, 0f)) { Callsign = "X", Brain = new ThrowingBrain() };
        host.List.Insert(0, thrower);
        var director = new AiDirector();
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true; // the failed think is logged as an error
        for (int t = 0; t < 12; t++) { host.Now += 0.05; director.Step(host); }
        Assert.That(host.List.Skip(1).All(s => ThinksOf(s) > 0), Is.True);
    }

    private sealed class ThrowingBrain : IAiBrain
    {
        public float ThinkSeconds => 0.25f;
        public void Think(TeamView view) => throw new System.InvalidOperationException("a bug");
    }

    // ---- the action budget ----

    [Test]
    public void TheBudgetLimitsActionsPerMinute([Values(30, 80, 200)] int apm)
    {
        var budget = new ActionBudget(apm, burstSeconds: 5f, waitSeconds: 10f);
        int taken = 0;
        double t = 0;
        for (int think = 0; think < 240; think++, t += 0.5) // two minutes of thinks, each wanting far more than it may
        {
            budget.Refill(t);
            for (int i = 0; i < 50; i++) budget.Propose(AiPriority.Normal, AiAction.Surrender(), 0, t);
            taken += budget.Spend(t, _ => true);
        }
        float burst = budget.Capacity;
        Assert.That(taken, Is.LessThanOrEqualTo(apm * 2 + burst + 1), "two minutes' worth, plus what it saved up at the start");
        Assert.That(taken, Is.GreaterThanOrEqualTo(apm * 2 - 1), "and it spends all it may");
    }

    [Test]
    public void TheBudgetSpendsTheHighestPriorityFirstAndLeftoversWait()
    {
        var budget = new ActionBudget(60, burstSeconds: 1f, waitSeconds: 10f); // one action a second, one saved
        var order = new List<int>();
        budget.Refill(0);
        budget.Propose(AiPriority.Low, AiAction.Gift(1, 1f), 0, 0);
        budget.Propose(AiPriority.Critical, AiAction.Gift(2, 1f), 0, 0);
        budget.Propose(AiPriority.Normal, AiAction.Gift(3, 1f), 0, 0);
        Assert.That(budget.Spend(0, a => { order.Add(a.Value); return true; }), Is.EqualTo(1));
        Assert.That(order, Is.EqualTo(new[] { 2 }), "the critical one first");
        Assert.That(budget.Pending, Is.EqualTo(2), "the rest wait");

        budget.Refill(1);
        budget.Spend(1, a => { order.Add(a.Value); return true; });
        Assert.That(order, Is.EqualTo(new[] { 2, 3 }), "next think: the next highest");

        // A re-proposal under the same key replaces the waiting one (keeping its place); a stale one is dropped.
        budget.Propose(AiPriority.Low, AiAction.Gift(4, 1f), 7, 1);
        budget.Propose(AiPriority.Low, AiAction.Gift(5, 1f), 7, 1);
        Assert.That(budget.Pending, Is.EqualTo(2));
        budget.Refill(30);
        Assert.That(budget.Spend(30, a => { order.Add(a.Value); return true; }), Is.Zero, "waited past ProposalWaitSeconds: dropped");
        Assert.That(budget.Pending, Is.Zero);
    }

    private sealed class Spammer : IAiManager
    {
        public void Think(AiContext ai) { for (int i = 0; i < 100; i++) ai.Propose(AiPriority.Normal, AiAction.Surrender()); }
    }

    [Test]
    public void ABrainTakesNoMoreActionsThanItsDifficultyAllows([Values(AiDifficulty.Easy, AiDifficulty.Normal, AiDifficulty.Hard)] AiDifficulty difficulty)
    {
        ConfigLoader.ResetForTests();
        AiConfig config = ConfigLoader.LoadConfig().Ai;
        var brain = new AiBrain(AiOwnerIds.For(0), difficulty, AiStyle.Balanced, config, new IAiManager[] { new Spammer() });
        int actions = 0;
        brain.Execute = _ => { actions++; return true; };
        var view = new TeamView();
        for (double t = 0; t <= 60.0001; t += brain.ThinkSeconds)
        {
            view.Time = t;
            brain.Think(view);
        }
        AiDifficultyConfig d = config.GetDifficulty(difficulty);
        Assert.That(actions, Is.LessThanOrEqualTo(d.ActionsPerMinute + brain.Budget.Capacity + 1), "one minute: its APM, plus the saved-up burst");
        Assert.That(actions, Is.GreaterThanOrEqualTo(d.ActionsPerMinute - 1));
        Assert.That(brain.Budget.Spent, Is.EqualTo(actions), "every action goes through the budget");
    }

    // ---- the reaction delay and the blackboard ----

    private static AiBrainConfig BrainConfig()
    {
        ConfigLoader.ResetForTests();
        return ConfigLoader.LoadConfig().Ai.Brain;
    }

    [Test]
    public void AnEventIsActionableOnlyAfterTheReactionDelay([Values(6f, 2f, 0.5f)] float reaction)
    {
        var board = new Blackboard(AiOwnerIds.For(0), reaction, BrainConfig());
        var view = new TeamView { Owner = AiOwnerIds.For(0), Time = 10 };
        view.Alerts.Add(new AiAlert { Kind = AlertKind.UnderAttack, Tile = new int2(5, 5), Time = 10 });
        board.Observe(view);
        var ready = new List<AiAlert>();
        Assert.That(board.Alerts.Ready(10, ready), Is.Zero, "not at once");
        Assert.That(board.Alerts.AnyReady(10 + reaction - 0.01), Is.False, "nor just before the delay");
        Assert.That(board.Alerts.AnyReady(10 + reaction), Is.True, "after it");
        board.Alerts.Ready(10 + reaction, ready);
        Assert.That(ready.Single().Tile, Is.EqualTo(new int2(5, 5)));

        // A threat near its building, likewise.
        int enemy = 202;
        view.Alerts.Clear();
        view.Attacking.Add(enemy);
        view.Buildings.Add(new AiBuilding { Id = 1, OwnerId = AiOwnerIds.For(0), Type = BuildingType.Base, Anchor = new int2(20, 20) });
        view.OtherUnits.Add(new AiUnit { Id = 7, OwnerId = enemy, Type = UnitType.Tank, Position = new float2(23, 20) });
        view.Time = 20;
        board.Observe(view);
        Assert.That(board.Threats.TryGetValue(1, out Threat threat), Is.True, "a threat near its HQ");
        Assert.That(board.IsActionable(threat), Is.False);
        view.Time = 20 + reaction;
        board.Observe(view);
        Assert.That(board.IsActionable(board.Threats[1]), Is.True);
        Assert.That(board.NewArmies.AnyReady(20 + reaction), Is.True, "a newly seen army, after the delay");
    }

    [Test]
    public void TheBlackboardRemembersBasesArmiesAndForgets()
    {
        AiBrainConfig brain = BrainConfig();
        int me = AiOwnerIds.For(0), enemy = 202, friend = 303;
        var board = new Blackboard(me, 1f, brain);
        var view = new TeamView { Owner = me, Time = 5 };
        view.Attacking.Add(enemy);
        view.Buildings.Add(new AiBuilding { Id = 10, OwnerId = enemy, Type = BuildingType.Barracks, Anchor = new int2(50, 50), Ghost = true });
        view.Buildings.Add(new AiBuilding { Id = 11, OwnerId = enemy, Type = BuildingType.Base, Anchor = new int2(60, 60), Ghost = true });
        view.Buildings.Add(new AiBuilding { Id = 12, OwnerId = friend, Type = BuildingType.Base, Anchor = new int2(10, 60) });
        view.OtherUnits.Add(new AiUnit { Id = 1, OwnerId = enemy, Type = UnitType.Tank, Position = new float2(40, 40) });
        view.OtherUnits.Add(new AiUnit { Id = 2, OwnerId = enemy, Type = UnitType.Heavy, Position = new float2(42, 40) });
        view.OtherUnits.Add(new AiUnit { Id = 3, OwnerId = friend, Type = UnitType.Tank, Position = new float2(10, 10) });
        board.Observe(view);
        Assert.That(board.EnemyBases[enemy].Anchor, Is.EqualTo(new int2(60, 60)), "its HQ, known from a ghost");
        Assert.That(board.EnemyBases[enemy].Ghost && board.EnemyBases[enemy].IsHq, Is.True);
        Assert.That(board.EnemyBases.ContainsKey(friend), Is.False, "only owners it attacks are enemies");
        ArmySighting army = board.Armies[enemy];
        Assert.That((army.Count, army.ByType[(int)UnitType.Tank], army.ByType[(int)UnitType.Heavy]), Is.EqualTo((2, 1, 1)));
        Assert.That(army.Centre, Is.EqualTo(new float2(41, 40)));

        // Out of sight: the army is remembered for ArmyMemorySeconds, the ghosts until the view drops them.
        view.OtherUnits.Clear();
        view.Time = 5 + brain.ArmyMemorySeconds - 1;
        board.Observe(view);
        Assert.That(board.Armies.ContainsKey(enemy), Is.True, "still remembered");
        view.Buildings.Clear();
        view.Time = 5 + brain.ArmyMemorySeconds + 1;
        board.Observe(view);
        Assert.That(board.Armies.ContainsKey(enemy), Is.False, "forgotten");
        Assert.That(board.EnemyBases, Is.Empty, "its spot seen again: the base is gone");
    }
}

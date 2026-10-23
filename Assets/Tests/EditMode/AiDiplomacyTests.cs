using System.Collections.Generic;
using System.Linq;
using Config;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Ai;
using WAR2D.AiView;

/// <summary>
/// v0.14 plan Task 8: the AI's diplomacy (spec §7, O2, O5, O6) and conceding (spec §8), in a scripted world
/// (<see cref="AiSandbox"/>) whose server keeps the AI's own diplomacy row and checks its actions.
/// </summary>
public class AiDiplomacyTests
{
    private const int Third = 8, Mate = 9, OtherMate = 10;

    [TearDown]
    public void TearDown() => ConfigLoader.ResetForTests();

    /// <summary>A playing match with Diplomacy on: the AI's HQ at the first site, a brain with only the given managers.</summary>
    private static AiSandbox World(AiDifficulty difficulty, params IAiManager[] managers)
    {
        var world = new AiSandbox(256, 5);
        world.AddBuilding(world.Ai, BuildingType.Base, world.Sites[0]);
        world.State = GameState.Playing;
        world.DiplomacyOn = true;
        world.MakeBrain(difficulty, AiStyle.Balanced, managers);
        return world;
    }

    /// <summary>A spot next to the AI's HQ (in its sight).</summary>
    private static float2 Near(AiSandbox world, float dx = 3f, float dy = 3f) => (float2)world.Sites[0] + new float2(dx, dy);

    private static void Army(AiSandbox world, int owner, int tanks, float dy)
    {
        for (int i = 0; i < tanks; i++) world.AddUnit(owner, UnitType.Tank, Near(world, -2f + i % 4, dy + i / 4));
    }

    private static float PeaceSeconds(AiSandbox world) => world.Game.Ai.Diplomacy.PeaceSeconds;

    private static bool AtPeace(AiSandbox world, int other) => !world.AiAttacks.Contains(other) && world.AiShares.Contains(other);

    // ---- teammates ----

    [Test]
    public void TeammatesAreNeverAttackedOrUnshared()
    {
        AiSandbox world = World(AiDifficulty.Hard, new DiplomacyManager());
        world.AddPlayer(Mate, 0);
        // Something changed its row towards the teammate: it puts it back.
        world.AiAttacks.Add(Mate);
        world.AiShares.Remove(Mate);
        world.Run(3.0);
        Assert.That(world.AiAttacks, Has.No.Member(Mate), "it stops attacking its teammate");
        Assert.That(world.AiShares, Has.Member(Mate), "and shares vision with them again");

        // Friendly fire is never a betrayal.
        int mine = world.AddUnit(world.Ai, UnitType.Tank, Near(world));
        int theirs = world.AddUnit(Mate, UnitType.Tank, Near(world, 4f, 3f));
        for (int i = 0; i < 20; i++) { world.ReportAttack(theirs, mine); world.Run(1.0); }
        Assert.That(world.Actions.Any(a => a.Kind == AiActionKind.SetAttack && a.Value == Mate && a.Flag), Is.False, "it never starts attacking a teammate");
        Assert.That(world.Actions.Any(a => a.Kind == AiActionKind.SetShareVision && a.Value == Mate && !a.Flag), Is.False, "nor stops sharing with one");
    }

    // ---- peace ----

    [Test]
    public void PeaceComesAfterPeaceSecondsOfSharedVisionWithoutAHit()
    {
        AiSandbox world = World(AiDifficulty.Easy, new DiplomacyManager());
        world.Run(2.0);
        world.SharesWithAi.Add(world.Enemy);
        world.Run(PeaceSeconds(world) - 5.0);
        Assert.That(world.AiAttacks, Has.Member(world.Enemy), "not before PeaceSeconds");
        world.Run(10.0);
        Assert.That(AtPeace(world, world.Enemy), Is.True, "Easy takes peace: it stops attacking and shares vision back");
    }

    [Test]
    public void NoPeaceWithoutSharedVisionOrWithDiplomacyOff()
    {
        AiSandbox world = World(AiDifficulty.Easy, new DiplomacyManager());
        world.Run(PeaceSeconds(world) * 2);
        Assert.That(world.AiAttacks, Has.Member(world.Enemy), "an enemy who doesn't share vision is never offered peace");

        AiSandbox off = World(AiDifficulty.Easy, new DiplomacyManager());
        off.DiplomacyOn = false;
        off.SharesWithAi.Add(off.Enemy);
        off.Run(PeaceSeconds(off) * 2);
        Assert.That(off.Actions, Is.Empty, "nothing at all when the match has Diplomacy off");
    }

    [Test]
    public void AHitRestartsThePeaceWait()
    {
        AiSandbox world = World(AiDifficulty.Easy, new DiplomacyManager());
        int mine = world.AddUnit(world.Ai, UnitType.Tank, Near(world));
        int theirs = world.AddUnit(world.Enemy, UnitType.Tank, Near(world, 5f, 3f));
        world.SharesWithAi.Add(world.Enemy);
        world.Run(40.0);
        world.ReportAttack(theirs, mine);
        world.Run(40.0);
        Assert.That(world.AiAttacks, Has.Member(world.Enemy), "40 s after their hit is not enough");
        world.Run(PeaceSeconds(world) - 40.0 + 5.0);
        Assert.That(AtPeace(world, world.Enemy), Is.True, "PeaceSeconds after their last hit");
    }

    [Test]
    public void NormalRefusesPeaceWhenClearlyWinningAndTakesItOtherwise()
    {
        AiSandbox winning = World(AiDifficulty.Normal, new DiplomacyManager());
        Army(winning, winning.Ai, 20, -8f);
        Army(winning, winning.Enemy, 2, 4f);
        winning.SharesWithAi.Add(winning.Enemy);
        winning.Run(PeaceSeconds(winning) + 10.0);
        Assert.That(winning.AiAttacks, Has.Member(winning.Enemy), "Normal won't take peace from someone it is clearly beating");

        AiSandbox even = World(AiDifficulty.Normal, new DiplomacyManager());
        Army(even, even.Ai, 10, -6f);
        Army(even, even.Enemy, 10, 3f);
        even.SharesWithAi.Add(even.Enemy);
        even.Run(PeaceSeconds(even) + 10.0);
        Assert.That(AtPeace(even, even.Enemy), Is.True, "but takes it from an even enemy");
    }

    [Test]
    public void HardTakesPeaceOnlyWhenLosingOrBothFightAThird()
    {
        AiSandbox even = World(AiDifficulty.Hard, new DiplomacyManager());
        Army(even, even.Ai, 10, -6f);
        Army(even, even.Enemy, 10, 3f);
        even.SharesWithAi.Add(even.Enemy);
        even.Run(PeaceSeconds(even) + 10.0);
        Assert.That(even.AiAttacks, Has.Member(even.Enemy), "Hard refuses peace from an even enemy (O2: and from one it beats)");

        AiSandbox losing = World(AiDifficulty.Hard, new DiplomacyManager());
        Army(losing, losing.Ai, 2, -6f);
        Army(losing, losing.Enemy, 20, 3f);
        losing.SharesWithAi.Add(losing.Enemy);
        losing.Run(PeaceSeconds(losing) + 10.0);
        Assert.That(AtPeace(losing, losing.Enemy), Is.True, "Hard takes peace when it is losing to them");

        AiSandbox third = World(AiDifficulty.Hard, new DiplomacyManager());
        third.AddPlayer(Third, 2);
        Army(third, third.Ai, 10, -6f);
        Army(third, third.Enemy, 10, 3f);
        int enemyTank = third.Units.First(u => u.OwnerId == third.Enemy).Id;
        int thirdTank = third.AddUnit(Third, UnitType.Tank, Near(third, 3f, 0f));
        third.SharesWithAi.Add(third.Enemy);
        for (int i = 0; i < 7; i++) { third.ReportAttack(enemyTank, thirdTank); third.Run(10.0); }
        Assert.That(AtPeace(third, third.Enemy), Is.True, "Hard takes peace with an even enemy when both fight a third player");
        Assert.That(third.AiAttacks, Has.Member(Third), "and keeps fighting the third");
    }

    [Test]
    public void ABetrayalMeansWarAtOnceAndAGrudge()
    {
        var manager = new DiplomacyManager();
        AiSandbox world = World(AiDifficulty.Easy, manager);
        int mine = world.AddUnit(world.Ai, UnitType.Tank, Near(world));
        int theirs = world.AddUnit(world.Enemy, UnitType.Tank, Near(world, 5f, 3f));
        world.SharesWithAi.Add(world.Enemy);
        world.Run(PeaceSeconds(world) + 5.0);
        Assert.That(AtPeace(world, world.Enemy), Is.True);

        world.ReportAttack(theirs, mine);
        world.Run(1.5);
        Assert.That(world.AiAttacks, Has.Member(world.Enemy), "a hit from someone at peace: it attacks them again at once");
        Assert.That(world.AiShares, Has.No.Member(world.Enemy), "and stops sharing vision");
        Assert.That(manager.Betrayals, Is.EqualTo(1));

        float grudge = world.Game.Ai.Diplomacy.GrudgeSeconds;
        world.Run(grudge - 10.0);
        Assert.That(world.AiAttacks, Has.Member(world.Enemy), "it refuses their peace for GrudgeSeconds, though they still share and haven't hit again");
        world.Run(15.0);
        Assert.That(AtPeace(world, world.Enemy), Is.True, "then peace is possible again");
    }

    [Test]
    public void AGiftFromANonTeammateHalvesThePeaceWaitOnce()
    {
        AiSandbox world = World(AiDifficulty.Easy, new DiplomacyManager());
        world.SharesWithAi.Add(world.Enemy);
        world.PendingAlerts.Add(new AiAlert { Kind = AlertKind.GiftReceived, OtherOwnerId = world.Enemy, Amount = 100f, Time = world.Time });
        world.Run(PeaceSeconds(world) * 0.5 + 3.0);
        Assert.That(AtPeace(world, world.Enemy), Is.True, "half the wait after a gift (O6)");

        var twice = new DiplomacyManager();
        AiSandbox again = World(AiDifficulty.Easy, twice);
        again.Run(1.0);
        again.PendingAlerts.Add(new AiAlert { Kind = AlertKind.GiftReceived, OtherOwnerId = again.Enemy, Amount = 100f, Time = again.Time });
        again.Run(1.0);
        again.PendingAlerts.Add(new AiAlert { Kind = AlertKind.GiftReceived, OtherOwnerId = again.Enemy, Amount = 100f, Time = again.Time });
        again.Run(1.0);
        Assert.That(twice.PeaceWait(again.Brain.Context, again.Enemy), Is.EqualTo(PeaceSeconds(again) * again.Game.Ai.Diplomacy.GiftPeaceFactor).Within(1e-3),
            "only the first gift counts");
    }

    // ---- gifts (O5) ----

    [Test]
    public void SurplusGoesToTheTeammateSeenFightingMostRecently()
    {
        AiSandbox world = World(AiDifficulty.Normal, new DiplomacyManager());
        world.AddPlayer(Mate, 0);
        world.AddPlayer(OtherMate, 0);
        int enemyTank = world.AddUnit(world.Enemy, UnitType.Tank, Near(world, 0f, 4f));
        int mateTank = world.AddUnit(Mate, UnitType.Tank, Near(world, 2f, 4f));
        int otherTank = world.AddUnit(OtherMate, UnitType.Tank, Near(world, 4f, 4f));
        world.ReportAttack(mateTank, enemyTank);
        world.Run(2.0);
        world.ReportAttack(otherTank, enemyTank);
        world.Resources = 6000f;
        AiDiplomacyConfig d = world.Game.Ai.Diplomacy;
        world.Run(d.GiftSurplusSeconds - 5.0);
        Assert.That(world.Gifts, Is.Empty, "not before the surplus lasted GiftSurplusSeconds");
        float before = world.Resources;
        world.Run(10.0);
        Assert.That(world.Gifts.Count, Is.EqualTo(1), "one gift");
        Assert.That(world.Gifts[0].to, Is.EqualTo(OtherMate), "to the teammate it saw fighting last");
        Assert.That(world.Gifts[0].amount, Is.EqualTo(before * d.GiftShare).Within(before * d.GiftShare * 0.1f), "a GiftShare of its balance");
    }

    [Test]
    public void EasyNeverGiftsAndNobodyGiftsBelowTheSurplus()
    {
        AiSandbox easy = World(AiDifficulty.Easy, new DiplomacyManager());
        easy.AddPlayer(Mate, 0);
        easy.Resources = 9000f;
        easy.Run(150.0);
        Assert.That(easy.Gifts, Is.Empty, "Easy never gifts (O5: Normal and Hard only)");

        AiSandbox poor = World(AiDifficulty.Hard, new DiplomacyManager());
        poor.AddPlayer(Mate, 0);
        poor.Resources = 1000f;
        poor.Run(150.0);
        Assert.That(poor.Gifts, Is.Empty, "no surplus, no gift");
    }

    // ---- its own row only, and the win rule ----

    [Test]
    public void TheViewHoldsOnlyItsOwnDiplomacyRow()
    {
        // The AI's diplomacy inputs are its own row and who shares with it: the view has no other player's row to read.
        var rows = typeof(TeamView).GetFields().Where(f => f.FieldType == typeof(ViewList<int>)).Select(f => f.Name).ToArray();
        Assert.That(rows, Is.EquivalentTo(new[] { nameof(TeamView.Attacking), nameof(TeamView.SharingWith), nameof(TeamView.SharedWithMe), nameof(TeamView.TerrainChanged) }));

        // An enemy who quietly stopped attacking it (their row, which it can't see) but doesn't share vision gets no peace.
        AiSandbox world = World(AiDifficulty.Easy, new DiplomacyManager());
        world.Run(PeaceSeconds(world) * 2);
        Assert.That(world.AiAttacks, Has.Member(world.Enemy));
    }

    [Test]
    public void APeaceBetweenAllSurvivorsEndsTheMatchAsAWin()
    {
        AiSandbox world = World(AiDifficulty.Easy, new DiplomacyManager());
        // The enemy (a person) has stopped attacking the AI and shares vision with it.
        var enemyAttacks = new HashSet<int>();
        world.SharesWithAi.Add(world.Enemy);
        world.Run(PeaceSeconds(world) + 5.0);
        Assert.That(AtPeace(world, world.Enemy), Is.True);

        var players = new List<PlayerStatus>
        {
            new PlayerStatus(world.Ai, true, false, 0),
            new PlayerStatus(world.Enemy, true, false, 1),
        };
        MatchOutcome outcome = WinLossRules.Evaluate(players,
            (a, b) => a == world.Ai ? world.AiAttacks.Contains(b) : enemyAttacks.Contains(b), startedHostile: true);
        Assert.That(outcome.Kind, Is.EqualTo(OutcomeKind.Winner), "every survivor at peace: they win together");
        Assert.That(outcome.Winners, Is.EquivalentTo(new[] { world.Ai, world.Enemy }));
    }

    // ---- conceding (spec §8) ----

    /// <summary>
    /// A hopeless AI: no HQ, producer or Builder, one Miner, no money, and a big enemy army in sight. Its money is
    /// zeroed every second so the sandbox's income never buys it a way back.
    /// </summary>
    private static AiSandbox Hopeless(params IAiManager[] managers)
    {
        var world = new AiSandbox(256, 5);
        world.AddBuilding(world.Ai, BuildingType.Miner, world.Sites[0]);
        world.State = GameState.Playing;
        world.MakeBrain(AiDifficulty.Normal, AiStyle.Balanced, managers);
        Army(world, world.Enemy, 20, -2f);
        return world;
    }

    private static void RunBroke(AiSandbox world, double seconds)
    {
        for (double t = 0; t < seconds; t += 1.0) { world.Resources = 0f; world.Run(1.0); }
    }

    [Test]
    public void ItConcedesWhenHopelessAfterMinMinutesForConcedeSeconds()
    {
        var concede = new ConcedeManager();
        AiSandbox world = Hopeless(concede);
        AiConcedeConfig c = world.Game.Ai.Concede;
        RunBroke(world, c.MinMinutes * 60.0 - 5.0);
        Assert.That(world.Surrendered, Is.False, "never before MinMinutes");
        Assert.That(concede.CanRebuild, Is.False);
        Assert.That(concede.OwnStrength, Is.LessThan(c.Ratio * concede.StrongestSeen), "it is hopeless already");
        RunBroke(world, c.Seconds - 5.0);
        Assert.That(world.Surrendered, Is.False, "both must hold for Seconds after MinMinutes");
        RunBroke(world, 15.0);
        Assert.That(world.Surrendered, Is.True, "then it surrenders");
        Assert.That(world.Actions.Count(a => a.Kind == AiActionKind.Surrender) - world.Refused.Count(a => a.Kind == AiActionKind.Surrender), Is.EqualTo(1),
            "through the Surrender action, accepted once");
    }

    [Test]
    public void ItNeverConcedesWhileATeammateHolds()
    {
        AiSandbox world = Hopeless(new ConcedeManager());
        world.AddPlayer(Mate, 0);
        AiConcedeConfig c = world.Game.Ai.Concede;
        RunBroke(world, c.MinMinutes * 60.0 + c.Seconds + 30.0);
        Assert.That(world.Surrendered, Is.False, "it fights on beside its teammate");
        world.Eliminate(Mate);
        RunBroke(world, c.Seconds + 5.0);
        Assert.That(world.Surrendered, Is.True, "alone, it concedes");
    }

    [Test]
    public void ItDoesntConcedeWhileItCanRebuildOrIsStrongEnough()
    {
        AiConcedeConfig c = new AiSandbox(256, 5).Game.Ai.Concede;

        AiSandbox rich = Hopeless(new ConcedeManager());
        rich.AddBuilding(rich.Ai, BuildingType.Barracks, rich.Sites[0] + new int2(6, 0));
        for (double t = 0; t < c.MinMinutes * 60.0 + c.Seconds + 30.0; t += 1.0) { rich.Resources = 5000f; rich.Run(1.0); }
        Assert.That(rich.Surrendered, Is.False, "a producer and the money for a unit: not hopeless");

        AiSandbox strong = Hopeless(new ConcedeManager());
        Army(strong, strong.Ai, 10, -8f);
        RunBroke(strong, c.MinMinutes * 60.0 + c.Seconds + 30.0);
        Assert.That(strong.Surrendered, Is.False, "an army worth more than Ratio of the enemy's: not hopeless");
    }
}

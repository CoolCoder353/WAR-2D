using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Net.Rejoin;
using WAR2D.Sim;
using WAR2D.UI;

/// <summary>The rules behind the fixes from the second agent-played session of 2026-11-04 (known-issues.md).</summary>
public class PlaytestFix2Tests
{
    [Test]
    public void DeathsNobodyCausedAreNoFight()
    {
        var f = new FightDetector(32, 20f, 600f, 30);
        for (int i = 0; i < 30; i++) f.Death(i * 0.1f, 1, 0, 100f, 10f, 10f); // decay: no killer
        Assert.AreEqual(0, f.Finish().Length);
    }

    [Test]
    public void DecayAmongRealDeathsAddsNothingToTheFight()
    {
        var f = new FightDetector(32, 20f, 600f, 30);
        for (int i = 0; i < 8; i++) f.Death(i, 1, 2, 100f, 10f, 10f);
        for (int i = 8; i < 16; i++) f.Death(i, 1, 0, 100f, 10f, 10f);
        SummaryFight[] fights = f.Finish();
        Assert.AreEqual(1, fights.Length);
        Assert.AreEqual(7f, fights[0].End, "the last death with a killer");
    }

    [Test]
    public void UnpaidUpkeepReadsDecayingEvenWhenIncomeCoversWhatWasPaid()
    {
        var e = new EconomyReadout();
        e.Feed(1f, 55f, 55f, 0f, true);
        Assert.AreEqual(EconomyState.Decaying, e.State);
        Assert.AreEqual("Decaying", e.Text());
        e.Feed(500f, 55f, 55f, 1f, false);
        Assert.AreEqual(EconomyState.Healthy, e.State);
    }

    [Test]
    public void AServerPlayerPublishesWhatIsOwedApartFromWhatWasPaid()
    {
        var p = new ServerPlayer(null, 10f);
        p.AddOwed(90f);
        p.AddUpkeep(55f);
        p.MarkUnpaid();
        p.RollSecond();
        Assert.AreEqual(90f, p.UpkeepOwedLastSecond);
        Assert.AreEqual(55f, p.UpkeepLastSecond, "what the AI and the statistics read");
        Assert.IsTrue(p.UnpaidLastSecond);
        p.RollSecond();
        Assert.AreEqual(0f, p.UpkeepOwedLastSecond);
        Assert.IsFalse(p.UnpaidLastSecond);
    }

    [Test]
    public void TheSimReportsTheUpkeepOwedNotJustWhatWasAfforded()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.GetUnit(UnitType.Tank).RunningCost = 2f); // pinned: 2/s (the harness spawns Tanks)
        float wallet = 5f, owed = 0f;
        sim.Context.BudgetOf = _ => wallet;
        sim.Context.Spend = (_, amount) => wallet -= amount;
        sim.Context.Owe = (_, amount) => owed += amount;
        for (int i = 0; i < 4; i++) sim.Spawn(SimHarness.OwnerA, new float2(10 + i * 2, 10));
        for (int i = 0; i < 21; i++) sim.Tick();
        Assert.AreEqual(1f, wallet, 1e-4f, "two units at 2/s were paid");
        Assert.AreEqual(8f, owed, 1e-4f, "four units at 2/s were owed");
    }

    [Test]
    public void ControlCharactersInAKeyNameReadAsTheKey()
    {
        Assert.AreEqual("Del", HoverText.KeyChip(Rebinding.CleanDisplay("\u007f")));
        Assert.AreEqual("Delete", Rebinding.CleanDisplay("\u007f"));
        Assert.AreEqual("Backspace", Rebinding.CleanDisplay("\b"));
        Assert.AreEqual("Tab", Rebinding.CleanDisplay("\t"));
        Assert.AreEqual("Enter", Rebinding.CleanDisplay("\r"));
        Assert.AreEqual("Escape", Rebinding.CleanDisplay("\u001b"));
        Assert.AreEqual("A", Rebinding.CleanDisplay("A"));
        Assert.AreEqual("", Rebinding.CleanDisplay(""));
    }

    [Test]
    public void APointIsInViewOnlyInsideTheCamerasBox()
    {
        Assert.IsTrue(CameraZoom.InView(new float2(100f, 100f), 10f, 2f, new float2(115f, 105f)));
        Assert.IsFalse(CameraZoom.InView(new float2(100f, 100f), 10f, 2f, new float2(125f, 100f)), "past half the width");
        Assert.IsFalse(CameraZoom.InView(new float2(100f, 100f), 10f, 2f, new float2(100f, 111f)), "past the size");
    }

    [Test]
    public void APlayerWhoLeftKeepsTheNameAndColourLastSeen()
    {
        var names = new PlayerNameMemory();
        Assert.AreEqual(("A", 5), names.Resolve(7, "A", 5, true));
        Assert.AreEqual(("A", 5), names.Resolve(7, null, 0, false), "gone: as last seen");
        Assert.AreEqual(("B", 2), names.Resolve(7, "B", 2, true), "a live player's own values win");
        Assert.AreEqual(("Player", 3), names.Resolve(9, null, 3, false), "never seen: the fallback");
    }

    [Test]
    public void AHostLeavingEndsTheRejoinOfferWithNoRefusalToShow()
    {
        RejoinTicket.Clear();
        RejoinTicket.DismissRefusal();
        RejoinTicket.Set("token", 60f);
        RejoinTicket.HostLeft();
        RejoinTicket.MarkDropped(0f);
        Assert.IsFalse(RejoinTicket.Available(1f));
        Assert.IsNull(RejoinTicket.Refusal);
    }

    [Test]
    public void AConnectionWithNoTokenIsAStrangerNotARejoin()
    {
        Assert.IsTrue(RejoinRules.IsStranger(null));
        Assert.IsTrue(RejoinRules.IsStranger(""));
        Assert.IsFalse(RejoinRules.IsStranger("token"));
    }

    [Test]
    public void AStrangerIsToldTheMatchStartedOnThePlayScreen()
    {
        WAR2D.Net.LobbyRemoval.Take();
        WAR2D.Net.LobbyRemoval.MatchStarted();
        Assert.AreEqual("That match has already started.", WAR2D.Net.LobbyRemoval.Take());
        Assert.IsNull(WAR2D.Net.LobbyRemoval.Take());
    }

    [Test]
    public void AClientThatAlreadyAskedForItsPlayerDoesNotAskAgainAfterASceneLoad()
    {
        Assert.IsFalse(RejoinRules.AsksForPlayerAfterScene(askedOnConnect: true, rejoining: false));
        Assert.IsFalse(RejoinRules.AsksForPlayerAfterScene(askedOnConnect: false, rejoining: true));
        Assert.IsTrue(RejoinRules.AsksForPlayerAfterScene(askedOnConnect: false, rejoining: false));
    }
}

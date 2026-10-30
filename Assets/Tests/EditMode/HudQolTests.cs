using NUnit.Framework;
using WAR2D.UI;

/// <summary>v0.15 HUD words and data: hover-card text, key chips, tooltip delay, and the selection model's quick-select data.</summary>
public class HudQolTests
{
    [Test]
    public void TooltipDelayFollowsTheSetting()
    {
        Assert.AreEqual(0.25f, HoverText.DelaySeconds(TooltipDelayMode.Fast));
        Assert.AreEqual(0.6f, HoverText.DelaySeconds(TooltipDelayMode.Normal));
        Assert.IsNull(HoverText.DelaySeconds(TooltipDelayMode.Off), "Off shows no card");
    }

    [Test]
    public void KeyChipsShortenBindingNames()
    {
        Assert.AreEqual("RMB", HoverText.KeyChip("Right Button"));
        Assert.AreEqual("Del", HoverText.KeyChip("Delete"));
        Assert.AreEqual(".", HoverText.KeyChip("Period"));
        Assert.AreEqual("A", HoverText.KeyChip("A"));
        Assert.AreEqual("", HoverText.KeyChip(null));
    }

    [Test]
    public void KeyChipsReadTheCurrentBinding()
    {
        string before = Rebinding.PathOf("AttackMove");
        try
        {
            Rebinding.Override("AttackMove", "<Keyboard>/q");
            Assert.AreEqual("Q", HoverCard.KeyOf(GameInput.AttackMove), "a rebind shows on the card");
        }
        finally { Rebinding.Override("AttackMove", before); }
    }

    [Test]
    public void OrderCardsCarryTheKeyAndAReasonWhenGreyedOut()
    {
        HoverCardContent attack = HoverText.Order(OrderKind.AttackMove, "A", "Shift", 0.2f);
        Assert.AreEqual("Attack-move", attack.Title);
        Assert.AreEqual("A", attack.Key);
        CollectionAssert.Contains(attack.Lines, "Shift + A: add to the queue");
        HoverCardContent repair = HoverText.Order(OrderKind.Repair, "F", "Shift", 0.2f);
        Assert.AreEqual(("Cost", "0.2 per health", HoverTone.Cost), repair.Pairs[0]);
        Assert.AreEqual("Needs a Builder selected", HoverText.OrderReason(OrderKind.Repair, false));
        Assert.IsNull(HoverText.OrderReason(OrderKind.Stop, true));
    }

    [Test]
    public void AffordReasonNamesWhatIsMissing()
    {
        Assert.AreEqual("Need 150 (you have 90)", HoverText.Afford(150f, 90.7f));
        Assert.IsNull(HoverText.Afford(150f, 150f));
    }

    [Test]
    public void UnitCardShowsCostInRedWhenUnaffordable()
    {
        var heavy = new Config.UnitConfig { UpfrontCost = 200f, BuildSeconds = 5f, Health = 400, Damage = 30, Range = 5f };
        HoverCardContent card = HoverText.Unit(UnitType.Heavy, heavy, 90f);
        Assert.AreEqual(("Cost", "200", HoverTone.Danger), card.Pairs[0]);
        Assert.AreEqual(HoverTone.Cost, HoverText.Unit(UnitType.Heavy, heavy, 500f).Pairs[0].tone);
    }

    [Test]
    public void SlotCardTellsBuildingGroupsFromSquads()
    {
        Assert.AreEqual("Group 2: 3 Barracks", HoverText.Slot(1, 0, 3, "Barracks", "Ctrl").Title);
        Assert.AreEqual("Squad 1: 12 units", HoverText.Slot(0, 12, 0, null, "Ctrl").Title);
        CollectionAssert.Contains(HoverText.Slot(9, 0, 0, null, "Ctrl").Lines, "Ctrl + 0: save the selection here");
    }

    [Test]
    public void SelectionModelRaisesChangedOnlyWhenQuickDataChanges()
    {
        var model = new SelectionModel();
        int changes = 0;
        model.Changed += () => changes++;
        var totals = new System.Collections.Generic.List<(UnitType, int)> { (UnitType.Tank, 31) };
        model.SetQuick(false, totals);
        model.SetQuick(false, totals);
        Assert.AreEqual(1, changes);
        Assert.AreEqual(31, model.TotalOf(UnitType.Tank));
        model.SetQuick(true, totals);
        Assert.IsTrue(model.MapWide);

        var counts = new int[WAR2D.Sim.Squads.Count];
        var types = new BuildingType[WAR2D.Sim.Squads.Count];
        counts[1] = 3;
        types[1] = BuildingType.Barracks;
        model.SetSlotBuildings(counts, types);
        model.SetSlotBuildings(counts, types);
        Assert.AreEqual(3, changes);
        Assert.AreEqual(3, model.SlotBuildings[1]);
        Assert.AreEqual(BuildingType.Barracks, model.SlotBuildingTypes[1]);
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using WAR2D.Client;
using WAR2D.UI;

public class SelectionModelTests
{
    private static ClientUnitView Unit(int id, UnitType type, float health01) =>
        new ClientUnitView { Id = id, OwnerId = 1, Type = type, Health01 = health01 };

    [Test]
    public void CountsPerType()
    {
        var model = new SelectionModel();
        model.SetUnits(new List<ClientUnitView> { Unit(1, UnitType.Tank, 1f), Unit(2, UnitType.Tank, 1f), Unit(3, UnitType.None, 1f) }, -1);
        Assert.That(model.Count, Is.EqualTo(3));
        CollectionAssert.AreEqual(new[] { (UnitType.None, 1), (UnitType.Tank, 2) }, model.ByType);
    }

    [Test]
    public void CombinedHealthIsTheMean()
    {
        var model = new SelectionModel();
        model.SetUnits(new List<ClientUnitView> { Unit(1, UnitType.Tank, 1f), Unit(2, UnitType.Tank, 0.5f), Unit(3, UnitType.Tank, 0f) }, 2);
        Assert.That(model.CombinedHealth01, Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(model.Squad, Is.EqualTo(2));
        model.SetUnits(new List<ClientUnitView>(), -1);
        Assert.That(model.CombinedHealth01, Is.EqualTo(0f), "no units, no health");
    }

    [Test]
    public void FilterToDropsOtherTypes()
    {
        var model = new SelectionModel();
        UnitType requested = UnitType.None;
        model.FilterRequested += type => requested = type;
        model.SetUnits(new List<ClientUnitView> { Unit(1, UnitType.Tank, 1f), Unit(2, UnitType.None, 0f) }, 4);
        model.FilterTo(UnitType.Tank);
        Assert.That(model.Count, Is.EqualTo(1));
        CollectionAssert.AreEqual(new[] { (UnitType.Tank, 1) }, model.ByType);
        Assert.That(model.CombinedHealth01, Is.EqualTo(1f));
        Assert.That(model.Squad, Is.EqualTo(-1), "a filtered selection is no longer the squad");
        Assert.That(requested, Is.EqualTo(UnitType.Tank));
    }

    [Test]
    public void ChangedOnlyWhenSomethingShownChanges()
    {
        var model = new SelectionModel();
        int raised = 0;
        model.Changed += () => raised++;
        var units = new List<ClientUnitView> { Unit(1, UnitType.Tank, 1f) };
        model.SetUnits(units, -1);
        model.SetUnits(units, -1);
        Assert.That(raised, Is.EqualTo(1));
        model.SetBuilding(7, BuildingType.SmallUnitSpawner, 3);
        model.SetBuilding(7, BuildingType.SmallUnitSpawner, 3);
        model.SetArmed(OrderKind.AttackMove);
        model.SetArmed(OrderKind.AttackMove);
        model.SetSquadCounts(new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        model.SetSquadCounts(new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        model.SetSquadCounts(new[] { 1, 2 }); // wrong length: ignored
        Assert.That(raised, Is.EqualTo(4));
        Assert.That(model.SquadCounts[0], Is.EqualTo(1));
    }

    [Test]
    public void KnowsWhenBuildersOrDiggersAreSelected()
    {
        var model = new SelectionModel();
        model.SetUnits(new List<ClientUnitView> { Unit(1, UnitType.Tank, 1f) }, -1);
        Assert.That(model.HasBuilders, Is.False);
        Assert.That(model.HasDiggers, Is.False);
        model.SetUnits(new List<ClientUnitView> { Unit(1, UnitType.Tank, 1f), Unit(2, UnitType.Builder, 1f) }, -1);
        Assert.That(model.HasBuilders, Is.True);
        Assert.That(model.HasDiggers, Is.False);
        model.SetUnits(new List<ClientUnitView> { Unit(3, UnitType.Digger, 1f) }, -1);
        Assert.That(model.HasBuilders, Is.False);
        Assert.That(model.HasDiggers, Is.True);
    }

    [Test]
    public void QueueOfFollowsThePerTypeCounts()
    {
        var model = new SelectionModel();
        var counts = new int[(int)UnitType.Digger + 1];
        counts[(int)UnitType.Tank] = 4;
        counts[(int)UnitType.Digger] = 2;
        model.SetBuildings(new[] { 7 }, BuildingType.SmallUnitSpawner, 6, counts, 0);
        Assert.That(model.QueueOf(UnitType.Tank), Is.EqualTo(4));
        Assert.That(model.QueueOf(UnitType.Digger), Is.EqualTo(2));
        Assert.That(model.QueueOf(UnitType.Builder), Is.EqualTo(0));

        int raised = 0;
        model.Changed += () => raised++;
        var more = (int[])counts.Clone();
        more[(int)UnitType.Digger] = 3;
        model.SetBuildings(new[] { 7 }, BuildingType.SmallUnitSpawner, 7, more, 0);
        Assert.That(raised, Is.EqualTo(1));
        Assert.That(model.QueueOf(UnitType.Digger), Is.EqualTo(3));
    }

    [Test]
    public void RefundCountsBlueprintsInFullAndFinishedBuildingsByShare()
    {
        var model = new SelectionModel();
        model.SetBuildings(new[] { 1, 2, 3 }, BuildingType.SmallUnitSpawner, 0, null, 1);
        Assert.That(model.BlueprintCount, Is.EqualTo(1));
        Assert.That(model.AllBlueprints, Is.False);
        Assert.That(model.RefundFor(200f, 0.5f), Is.EqualTo(200f + 2 * 100f));
        model.SetBuildings(new[] { 4 }, BuildingType.Miner, 0, null, 1);
        Assert.That(model.AllBlueprints, Is.True);
        Assert.That(model.RefundFor(50f, 0.5f), Is.EqualTo(50f));
    }
}

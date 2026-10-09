using NUnit.Framework;
using WAR2D.Sim;

public class SquadsTests
{
    [Test]
    public void AssignReplacesTheSquad()
    {
        var squads = new Squads();
        squads.Assign(1, 3, new[] { 10, 11 });
        squads.Assign(1, 3, new[] { 12 });
        CollectionAssert.AreEqual(new[] { 12 }, squads.Members(1, 3));
        CollectionAssert.IsEmpty(squads.Members(2, 3), "squads are per player");
    }

    [Test]
    public void PruneDropsDeadMembers()
    {
        var ids = new NetIdAllocator(8);
        int a = ids.Allocate(), b = ids.Allocate();
        var squads = new Squads();
        squads.Assign(1, 0, new[] { a, b });
        ids.Free(a);
        squads.Prune(1, 0, ids);
        CollectionAssert.AreEqual(new[] { b }, squads.Members(1, 0));
    }

    [Test]
    public void ForgetDropsEverySquad()
    {
        var squads = new Squads();
        squads.Assign(1, 0, new[] { 5 });
        squads.Forget(1);
        CollectionAssert.IsEmpty(squads.Members(1, 0));
    }

    [TestCase(-1, false)]
    [TestCase(0, true)]
    [TestCase(9, true)]
    [TestCase(10, false)]
    public void SquadIndexIsValidated(int squad, bool valid) => Assert.AreEqual(valid, CommandValidator.IsSquadIndexValid(squad));

    [Test]
    public void PruneToOwnedCountsOnlyTheOwnersLiveUnits()
    {
        var squads = new Squads();
        squads.Assign(1, 2, new[] { 10, 11, 12 });
        int count = squads.PruneToOwned(1, 2, id => id != 11); // 11 is dead or someone else's
        Assert.That(count, Is.EqualTo(2));
        CollectionAssert.AreEqual(new[] { 10, 12 }, squads.Members(1, 2));
        Assert.That(squads.PruneToOwned(2, 2, id => true), Is.EqualTo(0), "another player has no such squad");
        CollectionAssert.Contains(squads.Owners, 1);
    }
}

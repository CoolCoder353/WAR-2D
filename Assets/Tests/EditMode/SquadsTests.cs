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
}

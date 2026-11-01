using NUnit.Framework;
using WAR2D.Client;

/// <summary>Squads and building groups survive a rejoin (client-local state the server never had).</summary>
public class SelectionRejoinTests
{
    [Test]
    public void BuildingGroupsComeBackOnlyForTheTokenTheyWereKeptUnder()
    {
        var before = new Selection();
        before.SelectBuildings(new[] { 7, 9 }, false);
        Assert.IsTrue(before.AssignBuildingGroup(2));
        before.RememberForRejoin("ticket-a");

        var other = new Selection();
        Selection.ArmRejoin("ticket-b");
        Assert.IsFalse(other.RestoreAfterRejoin(), "another match's ticket restores nothing");

        var after = new Selection();
        Selection.ArmRejoin("ticket-a");
        Assert.IsTrue(after.RestoreAfterRejoin());
        Assert.IsTrue(after.IsBuildingGroup(2));
        Assert.IsFalse(after.IsBuildingGroup(1));

        var again = new Selection();
        Selection.ArmRejoin("ticket-a");
        Assert.IsFalse(again.RestoreAfterRejoin(), "restored once");
    }
}

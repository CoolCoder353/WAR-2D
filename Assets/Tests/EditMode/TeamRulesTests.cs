using NUnit.Framework;

public class TeamRulesTests
{
    [Test]
    public void SameChoiceSharesATeamAndSoloPlayersGetTheirOwn()
    {
        var teams = TeamRules.Assign(new[] { (10, 3), (20, TeamRules.NoTeam), (30, 3), (40, 1), (50, TeamRules.NoTeam) });
        Assert.That(teams[40], Is.EqualTo(0));
        Assert.That(teams[10], Is.EqualTo(1));
        Assert.That(teams[30], Is.EqualTo(1));
        Assert.That(teams[20], Is.EqualTo(2));
        Assert.That(teams[50], Is.EqualTo(3));
        Assert.That(TeamRules.CountTeams(teams.Values), Is.EqualTo(4));
    }

    [Test]
    public void NoChoicesIsFreeForAll()
    {
        var teams = TeamRules.Assign(new[] { (1, TeamRules.NoTeam), (2, TeamRules.NoTeam) });
        Assert.That(teams[1], Is.Not.EqualTo(teams[2]));
    }
}

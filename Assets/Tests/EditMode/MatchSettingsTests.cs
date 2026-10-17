using System.Linq;
using Config;
using NUnit.Framework;

public class MatchSettingsTests
{
    private static readonly LobbyConfig Lobby = new LobbyConfig { MapSizes = new[] { 256, 512, 1024 }, StartingResources = new[] { 500f, 1000f, 2500f, 5000f }, StartOptionsMin = 1, StartOptionsMax = 4, StartOptionsDefault = 3, StartPickSeconds = new[] { 0, 15, 45 }, StartPickDefault = 45 };
    private static MatchSettings Valid => new MatchSettings { Mode = MatchMode.Teams, Diplomacy = true, MapSize = 512, Seed = 42, StartingResources = 1000f, StartOptions = 3, StartPickSeconds = 45 };

    [Test]
    public void ValidSettingsPass() => Assert.IsTrue(MatchSettingsRules.IsValid(Valid, Lobby));

    [Test]
    public void SizeNotInTheListIsRejected()
    {
        MatchSettings s = Valid;
        s.MapSize = 300;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
        s.MapSize = 0;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby), "the scene's tilemaps are not a lobby choice");
    }

    [Test]
    public void ResourcesNotInTheListAreRejected()
    {
        MatchSettings s = Valid;
        s.StartingResources = 999f;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
        s.StartingResources = float.NaN;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
    }

    [Test]
    public void OutOfRangeModeOrZeroSeedIsRejected()
    {
        MatchSettings s = Valid;
        s.Mode = (MatchMode)7;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
        s = Valid;
        s.Seed = 0;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
    }

    [Test]
    public void ColoursAndTeams()
    {
        Assert.AreEqual(2, LobbyRules.FirstFreeColour(new[] { 0, 1, 3 }));
        Assert.AreEqual(-1, LobbyRules.FirstFreeColour(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
        Assert.IsFalse(LobbyRules.IsColourFree(3, new[] { 3 }));
        Assert.IsFalse(LobbyRules.IsColourFree(8, new int[0]));
        Assert.IsTrue(LobbyRules.IsColourFree(4, new[] { 3 }));
        Assert.IsTrue(LobbyRules.IsTeamChoiceValid(MatchMode.FreeForAll, TeamRules.NoTeam));
        Assert.IsFalse(LobbyRules.IsTeamChoiceValid(MatchMode.FreeForAll, 0), "free-for-all has no teams");
        Assert.IsTrue(LobbyRules.IsTeamChoiceValid(MatchMode.Teams, 3));
        Assert.IsFalse(LobbyRules.IsTeamChoiceValid(MatchMode.Teams, 4));
    }

    [Test]
    public void OnlyTheLobbyTakesNewPlayersUpToTheColours()
    {
        Assert.IsTrue(LobbyRules.MayJoin(GameState.Lobby, 1), "the host");
        Assert.IsTrue(LobbyRules.MayJoin(GameState.Lobby, LobbyRules.MaxPlayers));
        Assert.IsFalse(LobbyRules.MayJoin(GameState.Lobby, LobbyRules.MaxPlayers + 1), "a ninth player would share a colour and have no HQ site");
        Assert.AreEqual(LobbyRules.Colours, LobbyRules.MaxPlayers);
        foreach (GameState state in System.Enum.GetValues(typeof(GameState)))
            if (state != GameState.Lobby) Assert.IsFalse(LobbyRules.MayJoin(state, 2), $"no joining in {state}");
    }

    [Test]
    public void StartOptionsAndPickTime_AcceptOnlyOfferedValues()
    {
        MatchSettings s = Valid;
        s.StartOptions = 3; s.StartPickSeconds = 45;
        Assert.IsTrue(MatchSettingsRules.IsValid(s, Lobby));
        foreach (int pick in new[] { 0, 15, StartPickRules.Unlimited }) { s.StartPickSeconds = pick; Assert.IsTrue(MatchSettingsRules.IsValid(s, Lobby), $"pick {pick}"); }
        foreach (int pick in new[] { 1, 16, 91, -2, int.MinValue, int.MaxValue }) { s.StartPickSeconds = pick; Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby), $"pick {pick}"); }
        s.StartPickSeconds = 45;
        foreach (int options in new[] { 0, 5, -1, int.MaxValue }) { s.StartOptions = options; Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby), $"options {options}"); }
    }

    [Test]
    public void Fits_NeedsOptionsForEveryoneAndTheSpareSites()
    {
        Assert.IsTrue(StartPickRules.Fits(8, 3, 28, 2));
        Assert.IsFalse(StartPickRules.Fits(8, 4, 28, 2), "8 x 4 + 2 > 28 is exactly 34");
        Assert.IsTrue(StartPickRules.Fits(8, 3, 26, 2));
        Assert.IsFalse(StartPickRules.Fits(8, 3, 25, 2));
    }

    [Test]
    public void Deal_GivesDistinctSitesUniqueAcrossPlayers()
    {
        var rng = new System.Random(5);
        for (int round = 0; round < 50; round++)
        {
            var deal = StartPickRules.Deal(16, new[] { 10, 20, 30, 40 }, 3, rng.Next);
            Assert.AreEqual(4, deal.Count);
            var seen = new System.Collections.Generic.HashSet<int>();
            foreach (var pair in deal)
            {
                Assert.AreEqual(3, pair.Value.Length);
                foreach (int site in pair.Value) { Assert.That(site, Is.InRange(0, 15)); Assert.IsTrue(seen.Add(site), "a site was dealt twice"); }
            }
        }
        Assert.IsEmpty(StartPickRules.Deal(5, new[] { 1, 2 }, 3, rng.Next), "too few sites");
    }

    [Test]
    public void Deal_DependsOnItsRandomnessOnly()
    {
        var a = StartPickRules.Deal(40, new[] { 1, 2, 3 }, 3, new System.Random(1).Next);
        var a2 = StartPickRules.Deal(40, new[] { 1, 2, 3 }, 3, new System.Random(1).Next);
        var b = StartPickRules.Deal(40, new[] { 1, 2, 3 }, 3, new System.Random(2).Next);
        CollectionAssert.AreEqual(a[1], a2[1]);
        Assert.IsFalse(a[1].SequenceEqual(b[1]) && a[2].SequenceEqual(b[2]) && a[3].SequenceEqual(b[3]), "a different source gives a different deal");
    }
}

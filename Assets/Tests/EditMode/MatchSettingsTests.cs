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

    // ---- v0.14: Visibility and lobby AIs ----

    [Test]
    public void VisibilityIsValidatedAndFriendsIsTheDefault()
    {
        Assert.That(default(MatchSettings).Visibility, Is.EqualTo(LobbyVisibility.Friends), "a default settings struct keeps today's behaviour");
        foreach (LobbyVisibility v in new[] { LobbyVisibility.Public, LobbyVisibility.Friends, LobbyVisibility.Private })
        {
            MatchSettings ok = Valid;
            ok.Visibility = v;
            Assert.IsTrue(MatchSettingsRules.IsValid(ok, Lobby), v.ToString());
        }
        MatchSettings s = Valid;
        s.Visibility = (LobbyVisibility)9;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
        MatchSettings other = Valid;
        other.Visibility = LobbyVisibility.Private;
        Assert.IsFalse(Valid.Equals(other), "a Visibility change is a settings change");
    }

    [Test]
    public void PrivateTakesOnlyTheHostsOwnConnection()
    {
        Assert.IsTrue(LobbyRules.MayConnect(LobbyVisibility.Private, true, GameState.Lobby, 1, out _), "the host's own client");
        Assert.IsFalse(LobbyRules.MayConnect(LobbyVisibility.Private, false, GameState.Lobby, 2, out string why), "anyone else");
        StringAssert.Contains("Private match", why);
        Assert.IsTrue(LobbyRules.MayConnect(LobbyVisibility.Friends, false, GameState.Lobby, 2, out why));
        Assert.IsNull(why);
        Assert.IsTrue(LobbyRules.MayConnect(LobbyVisibility.Public, false, GameState.Lobby, LobbyRules.MaxPlayers, out _));
        Assert.IsFalse(LobbyRules.MayConnect(LobbyVisibility.Public, false, GameState.Lobby, LobbyRules.MaxPlayers + 1, out why), "AIs fill seats too");
        Assert.IsNull(why, "a full lobby keeps the old refusal");
        Assert.IsFalse(LobbyRules.MayConnect(LobbyVisibility.Friends, false, GameState.Playing, 2, out _));
    }

    [Test]
    public void GoingPrivateIsRefusedWithAnotherHumanOnly()
    {
        Assert.IsTrue(LobbyRules.MayChangeVisibility(LobbyVisibility.Friends, LobbyVisibility.Private, 1), "AIs never block Private");
        Assert.IsFalse(LobbyRules.MayChangeVisibility(LobbyVisibility.Friends, LobbyVisibility.Private, 2));
        Assert.IsFalse(LobbyRules.MayChangeVisibility(LobbyVisibility.Public, LobbyVisibility.Private, 3));
        Assert.IsTrue(LobbyRules.MayChangeVisibility(LobbyVisibility.Private, LobbyVisibility.Private, 2), "staying Private (other settings change)");
        Assert.IsTrue(LobbyRules.MayChangeVisibility(LobbyVisibility.Private, LobbyVisibility.Friends, 1));
        Assert.IsTrue(LobbyRules.MayChangeVisibility(LobbyVisibility.Friends, LobbyVisibility.Public, 5));
    }

    [Test]
    public void AddedAisJoinTheTeamWithFewestPlayers()
    {
        Assert.AreEqual(TeamRules.NoTeam, LobbyRules.FewestPlayersTeam(MatchMode.FreeForAll, new[] { 0, 0 }));
        Assert.AreEqual(0, LobbyRules.FewestPlayersTeam(MatchMode.Teams, new int[0]));
        Assert.AreEqual(1, LobbyRules.FewestPlayersTeam(MatchMode.Teams, new[] { 0, TeamRules.NoTeam }), "the lowest of the empty teams");
        Assert.AreEqual(3, LobbyRules.FewestPlayersTeam(MatchMode.Teams, new[] { 0, 1, 2, 0, 1, 2 }));
        Assert.AreEqual(0, LobbyRules.FewestPlayersTeam(MatchMode.Teams, null));
    }

    [Test]
    public void TheHostPicksOnlyRealDifficultiesAndStyles()
    {
        Assert.IsTrue(AiLobbyRules.IsValidChoice(AiDifficulty.Easy, AiStyle.Random));
        Assert.IsTrue(AiLobbyRules.IsValidChoice(AiDifficulty.Hard, AiStyle.Balanced));
        Assert.IsFalse(AiLobbyRules.IsValidChoice(AiDifficulty.Scripted, AiStyle.Rusher), "Scripted is dev only");
        Assert.IsFalse(AiLobbyRules.IsValidChoice((AiDifficulty)40, AiStyle.Rusher));
        Assert.IsFalse(AiLobbyRules.IsValidChoice(AiDifficulty.Normal, (AiStyle)40));
        Assert.IsTrue(AiLobbyRules.HasFreeSeat(LobbyRules.MaxPlayers - 1));
        Assert.IsFalse(AiLobbyRules.HasFreeSeat(LobbyRules.MaxPlayers));
    }
}

using System.Collections;
using System.Linq;
using System.Reflection;
using Config;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.UI;

/// <summary>
/// v0.14 plan Task 3: the lobby's AI seats (the host's Commands and the approved rows) and Visibility (Private takes
/// nobody but the host, works with Steam off, and hides what a joiner would need).
/// </summary>
public class LobbyAiPlayModeTests
{
    private GameConfigData config;
    private WAR2D.Net.Steam.ISteamBackend steamBefore;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        steamBefore = WAR2D.Net.Steam.SteamServices.Backend;
        GameManager.LastRefusedConnection = -1;
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        WAR2D.Net.Steam.SteamServices.Backend = steamBefore;
        yield return PlayModeMatch.TearDown();
    }

    private static MenuController Menu() => Object.FindAnyObjectByType<MenuController>();
    private static VisualElement Root() => Menu().GetComponent<UIDocument>().rootVisualElement;
    private static ClientPlayer Local => NetworkClient.localPlayer.GetComponent<ClientPlayer>();

    private static IEnumerator HostFromMenu()
    {
        VisualElement root = Root();
        HudTests.Click(root.Q<Button>("play-button"));
        yield return null;
        HudTests.Click(root.Q<Button>("host-button"));
        yield return PlayModeMatch.WaitUntil(() => root.Q("lobby").ClassListContains("menu-screen--visible"), 15f);
    }

    private static AiSeat OnlyAi() => GameCore.Instance.AiPlayers.Values.Single();

    /// <summary>Runs a Command's server body as <paramref name="sender"/> (Mirror's weaver keeps it as <c>UserCode_…</c>).</summary>
    private static void InvokeAs(NetworkConnectionToClient sender, string command, params object[] args)
    {
        MethodInfo body = typeof(GameCore).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(m => m.Name.StartsWith("UserCode_" + command + "__"));
        body.Invoke(GameCore.Instance, args.Append(sender).ToArray());
    }

    /// <summary>A connection that is not the server owner but has a player object (so the rate gate lets it through).</summary>
    private static NetworkConnectionToClient Stranger(int id, NetworkIdentity identity)
    {
        var conn = new NetworkConnectionToClient(id);
        typeof(NetworkConnection).GetProperty("identity").GetSetMethod(true).Invoke(conn, new object[] { identity });
        return conn;
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator TheHostAddsChangesAndRemovesAis()
    {
        yield return HostFromMenu();
        VisualElement root = Root();
        GameCore core = GameCore.Instance;
        HudTests.Click(root.Q<Button>("mode-teams"));
        yield return PlayModeMatch.WaitUntil(() => core.Settings.Mode == MatchMode.Teams, 5f);
        core.Cmd_SetOwnTeam(0);
        core.Cmd_SetReady(true);
        yield return PlayModeMatch.WaitUntil(() => Local.lobbyTeam == 0 && Local.ready, 5f);

        // Add AI: Normal, Random, the first unused colour, the team with the fewest players; everyone's ready clears.
        HudTests.Click(root.Q<Button>("add-ai-button"));
        yield return PlayModeMatch.WaitUntil(() => core.AiPlayers.Count == 1, 5f);
        AiSeat ai = OnlyAi();
        int owner = ai.OwnerId;
        Assert.That((ai.Difficulty, ai.Style, ai.Colour, ai.LobbyTeam), Is.EqualTo((AiDifficulty.Normal, AiStyle.Random, 1, 1)));
        yield return PlayModeMatch.WaitUntil(() => !Local.ready && root.Q("lobby-row-" + owner) != null, 5f);
        Assert.That(root.Q("lobby-row-" + owner).Q<Label>("ai-tag-text").text, Is.EqualTo("AI"), "the host's row: the tag, then the dropdowns");
        Assert.That(root.Q<Button>("ai-difficulty-" + owner).text, Does.StartWith("Normal"));
        Assert.That(root.Q<Button>("ai-style-" + owner).text, Does.StartWith("Random"));
        Assert.That(root.Q<Label>("players-title").text, Is.EqualTo("Players (2 of 8)"));

        // Change: every field, and it clears ready again.
        core.Cmd_SetReady(true);
        yield return PlayModeMatch.WaitUntil(() => Local.ready, 5f);
        core.Cmd_SetAi(owner, AiDifficulty.Hard, AiStyle.Turtle, 5, 2);
        yield return PlayModeMatch.WaitUntil(() => ai.Difficulty == AiDifficulty.Hard, 5f);
        Assert.That((ai.Style, ai.Colour, ai.LobbyTeam), Is.EqualTo((AiStyle.Turtle, 5, 2)));
        yield return PlayModeMatch.WaitUntil(() => !Local.ready, 5f);
        ClientPlayer seen = null;
        yield return PlayModeMatch.WaitUntil(() => ClientPlayer.TryFind(owner, out seen) && seen.aiDifficulty == AiDifficulty.Hard && seen.colourIndex == 5, 5f);
        Assert.That((seen.aiStyle, seen.lobbyTeam), Is.EqualTo((AiStyle.Turtle, 2)), "clients see an explicit style");

        // Refused: Scripted, unknown enums, a taken colour, a team the mode doesn't offer, an owner that isn't an AI.
        core.Cmd_SetAi(owner, AiDifficulty.Scripted, AiStyle.Turtle, 5, 2);
        core.Cmd_SetAi(owner, AiDifficulty.Hard, (AiStyle)77, 5, 2);
        core.Cmd_SetAi(owner, AiDifficulty.Hard, AiStyle.Turtle, Local.colourIndex, 2);
        core.Cmd_SetAi(owner, AiDifficulty.Hard, AiStyle.Turtle, 5, 9);
        core.Cmd_SetAi(owner, AiDifficulty.Hard, AiStyle.Turtle, 99, 2);
        core.Cmd_SetAi(PlayModeMatch.LocalOwner, AiDifficulty.Easy, AiStyle.Rusher, 6, 0);
        core.Cmd_SetAi(AiOwnerIds.First + 77, AiDifficulty.Easy, AiStyle.Rusher, 6, 0);
        core.Cmd_AddAi(AiDifficulty.Scripted, AiStyle.Random);
        core.Cmd_AddAi((AiDifficulty)9, AiStyle.Random);
        core.Cmd_RemoveAi(PlayModeMatch.LocalOwner);
        yield return new WaitForSeconds(0.4f);
        Assert.That((ai.Difficulty, ai.Style, ai.Colour, ai.LobbyTeam), Is.EqualTo((AiDifficulty.Hard, AiStyle.Turtle, 5, 2)), "every bad change is refused");
        Assert.That(core.AiPlayers.Count, Is.EqualTo(1));

        // The swatch takes the next free colour; the team pill cycles like a human's.
        HudTests.Click(root.Q<Button>("ai-colour-" + owner));
        yield return PlayModeMatch.WaitUntil(() => ai.Colour == 6, 5f);
        yield return PlayModeMatch.WaitUntil(() => root.Q<Button>("team-" + owner) != null && root.Q<Button>("team-" + owner).text.StartsWith("Team 3"), 5f);
        HudTests.Click(root.Q<Button>("team-" + owner));
        yield return PlayModeMatch.WaitUntil(() => ai.LobbyTeam == 3, 5f);

        // Free-for-all forces every AI Solo.
        HudTests.Click(root.Q<Button>("mode-ffa"));
        yield return PlayModeMatch.WaitUntil(() => core.Settings.Mode == MatchMode.FreeForAll && ai.LobbyTeam == TeamRules.NoTeam, 5f);
        core.Cmd_SetAi(owner, ai.Difficulty, ai.Style, ai.Colour, 1);
        yield return new WaitForSeconds(0.3f);
        Assert.That(ai.LobbyTeam, Is.EqualTo(TeamRules.NoTeam), "no teams in Free-for-all");

        // Seat limit: humans and AIs together at most 8; Add AI hides when the lobby is full.
        for (int i = 0; i < LobbyRules.MaxPlayers - 2; i++)
        {
            int before = core.AiPlayers.Count;
            core.Cmd_AddAi(AiDifficulty.Easy, AiStyle.Rusher);
            yield return PlayModeMatch.WaitUntil(() => core.AiPlayers.Count == before + 1, 5f);
        }
        Assert.That(core.SeatCount, Is.EqualTo(LobbyRules.MaxPlayers));
        yield return PlayModeMatch.WaitUntil(() => root.Q("add-ai-row").ClassListContains("hidden"), 5f);
        core.Cmd_AddAi(AiDifficulty.Easy, AiStyle.Rusher);
        yield return new WaitForSeconds(0.3f);
        Assert.That(core.SeatCount, Is.EqualTo(LobbyRules.MaxPlayers), "no ninth seat");
        Assert.That(core.AiPlayers.Values.Select(s => s.Colour).Append(Local.colourIndex).Distinct().Count(), Is.EqualTo(LobbyRules.MaxPlayers), "colours stay unique");

        // A reroll draws the callsigns again from the new seed, without repeats.
        uint seed = core.Settings.Seed;
        HudTests.Click(root.Q<Button>("reroll-button"));
        yield return PlayModeMatch.WaitUntil(() => core.Settings.Seed != seed, 5f);
        var taken = new System.Collections.Generic.HashSet<string>();
        foreach (int id in core.AiPlayers.Keys.OrderBy(k => k))
        {
            string expected = AiCallsigns.Draw(config.Ai.Callsigns, core.Settings.Seed, taken);
            taken.Add(expected);
            Assert.That(core.AiPlayers[id].Callsign, Is.EqualTo(expected), "drawn in owner order from the new seed");
        }
        yield return PlayModeMatch.WaitUntil(() => ClientPlayer.TryFind(owner, out seen) && seen.nickname == core.AiPlayers[owner].Callsign, 5f);

        // Remove (its ×).
        HudTests.Click(root.Q<Button>("ai-remove-" + owner));
        yield return PlayModeMatch.WaitUntil(() => !core.AiPlayers.ContainsKey(owner), 5f);
        yield return PlayModeMatch.WaitUntil(() => root.Q("lobby-row-" + owner) == null && !root.Q("add-ai-row").ClassListContains("hidden"), 5f);
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator OnlyTheHostMayAddChangeOrRemoveAis()
    {
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        GameCore core = GameCore.Instance;
        AiSeat ai = core.AddAi(AiDifficulty.Normal, AiStyle.Rusher);
        Assert.That(ai, Is.Not.Null);
        yield return PlayModeMatch.WaitUntil(() => ai.Client != null && ai.Client.netIdentity != null, 5f);

        // A connection with a player object that isn't the server owner: every AI Command is refused.
        NetworkConnectionToClient stranger = Stranger(4242, ai.Client.netIdentity);
        InvokeAs(stranger, "Cmd_AddAi", AiDifficulty.Hard, AiStyle.Turtle);
        InvokeAs(stranger, "Cmd_SetAi", ai.OwnerId, AiDifficulty.Hard, AiStyle.Turtle, 6, TeamRules.NoTeam);
        InvokeAs(stranger, "Cmd_RemoveAi", ai.OwnerId);
        Assert.That(core.AiPlayers.Count, Is.EqualTo(1));
        Assert.That((ai.Difficulty, ai.Style, ai.Colour), Is.EqualTo((AiDifficulty.Normal, AiStyle.Rusher, 1)), "a non-host changes nothing");

        // The host, through the same bodies (positive control).
        InvokeAs(NetworkServer.localConnection, "Cmd_SetAi", ai.OwnerId, AiDifficulty.Hard, AiStyle.Turtle, 6, TeamRules.NoTeam);
        Assert.That(ai.Difficulty, Is.EqualTo(AiDifficulty.Hard));
        InvokeAs(NetworkServer.localConnection, "Cmd_RemoveAi", ai.OwnerId);
        Assert.That(core.AiPlayers.Count, Is.EqualTo(0));

        // Outside the lobby nothing changes either.
        AiSeat other = core.AddAi(AiDifficulty.Easy, AiStyle.Balanced);
        DevApi.AllowForTests = true;
        core.Cmd_StartGame();
        yield return PlayModeMatch.WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        DevApi.AllowForTests = false;
        InvokeAs(NetworkServer.localConnection, "Cmd_AddAi", AiDifficulty.Hard, AiStyle.Turtle);
        InvokeAs(NetworkServer.localConnection, "Cmd_SetAi", other.OwnerId, AiDifficulty.Hard, AiStyle.Turtle, other.Colour, TeamRules.NoTeam);
        InvokeAs(NetworkServer.localConnection, "Cmd_RemoveAi", other.OwnerId);
        Assert.That(core.AiPlayers.Count, Is.EqualTo(1));
        Assert.That(other.Difficulty, Is.EqualTo(AiDifficulty.Easy), "only in the lobby");
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator PrivateRefusesEveryConnectionButTheHosts()
    {
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        GameCore core = GameCore.Instance;
        Assert.That(core.Settings.Visibility, Is.EqualTo(config.Lobby.Visibility), "the default comes from <Lobby><Visibility>");

        // Friends: a second connection is let in (the lobby has seats).
        GameManager.Instance.OnServerConnect(new NetworkConnectionToClient(7001));
        Assert.That(GameManager.LastRefusedConnection, Is.EqualTo(-1));

        MatchSettings s = core.Settings;
        s.MapSize = 256;
        s.StartingResources = 1000f; // the test config's 10000 isn't a lobby choice
        s.Visibility = LobbyVisibility.Private;
        core.Cmd_SetMatchSettings(s);
        yield return PlayModeMatch.WaitUntil(() => core.Settings.Visibility == LobbyVisibility.Private, 5f);
        GameManager.Instance.OnServerConnect(new NetworkConnectionToClient(7002));
        Assert.That(GameManager.LastRefusedConnection, Is.EqualTo(7002), "Private: nobody but the host");
        GameManager.LastRefusedConnection = -1;
        GameManager.Instance.OnServerConnect(NetworkServer.localConnection);
        Assert.That(GameManager.LastRefusedConnection, Is.EqualTo(-1), "the host's own connection stays");

        // Going Private with another human connected is refused; AIs never block it.
        s = core.Settings;
        s.Visibility = LobbyVisibility.Friends;
        core.Cmd_SetMatchSettings(s);
        yield return PlayModeMatch.WaitUntil(() => core.Settings.Visibility == LobbyVisibility.Friends, 5f);
        Assert.That(core.AddAi(AiDifficulty.Easy, AiStyle.Random), Is.Not.Null);
        var other = new GameObject("other human", typeof(NetworkIdentity)).GetComponent<NetworkIdentity>();
        core.ServerPlayers[other] = new ServerPlayer(null, 0f);
        try
        {
            // Run the Command's body at once: the stand-in human has no player object for the server's per-frame work.
            s.Visibility = LobbyVisibility.Private;
            InvokeAs(NetworkServer.localConnection, "Cmd_SetMatchSettings", s);
            Assert.That(core.Settings.Visibility, Is.EqualTo(LobbyVisibility.Friends), "refused with another human in the lobby");
        }
        finally
        {
            core.ServerPlayers.Remove(other);
            Object.Destroy(other.gameObject);
        }
        core.Cmd_SetMatchSettings(s);
        yield return PlayModeMatch.WaitUntil(() => core.Settings.Visibility == LobbyVisibility.Private, 5f);
    }

    /// <summary>
    /// The plan's lobby UI test: with Steam off, the host goes Private, adds an AI (Random style) and starts a 1v1. The
    /// resolved style never reaches the client before GameOver; the end screen then shows it with "(random)".
    /// </summary>
    [UnityTest, Timeout(120000)]
    public IEnumerator TheHostAddsAnAiAndStartsAPrivateOneVsOne()
    {
        WAR2D.Net.Steam.SteamServices.Backend = null; // Steam off (-noSteam)
        yield return HostFromMenu();
        VisualElement root = Root();
        GameCore core = GameCore.Instance;
        HudTests.Click(root.Q<Button>("size-256"));
        yield return PlayModeMatch.WaitUntil(() => core.Settings.MapSize == 256, 5f);
        HudTests.Click(root.Q<Button>("visibility-private"));
        yield return PlayModeMatch.WaitUntil(() => core.Settings.Visibility == LobbyVisibility.Private, 5f);
        yield return PlayModeMatch.WaitUntil(() => root.Q<Label>("lobby-subtitle").text.Contains("private match"), 5f);
        Assert.That(root.Q<Button>("invite-button").ClassListContains("hidden"), Is.True, "no Invite when Private");
        Assert.That(root.Q<Label>("visibility-hint").ClassListContains("lobby__visibility-hint--warning"), Is.True, "Private's hint in the warning colour");
        Assert.That(WAR2D.Net.Steam.SteamHostLobby.Current, Is.Null, "no Steam lobby");

        HudTests.Click(root.Q<Button>("pick-time-0")); // HQs at random: straight to the countdown
        yield return PlayModeMatch.WaitUntil(() => core.Settings.StartPickSeconds == 0, 5f);
        HudTests.Click(root.Q<Button>("add-ai-button"));
        yield return PlayModeMatch.WaitUntil(() => core.AiPlayers.Count == 1, 5f);
        AiSeat ai = OnlyAi();
        HudTests.Click(root.Q<Button>("ready-button"));
        yield return PlayModeMatch.WaitUntil(() => root.Q<Button>("start-button").enabledSelf, 5f);
        Assert.That(root.Q<Label>("lobby-status").text, Is.EqualTo("Single-player: start when you like"));
        HudTests.Click(root.Q<Button>("start-button"));
        yield return PlayModeMatch.WaitUntil(() => core.CurrentState == GameState.Playing, 60f);

        // Random is resolved on the server and stays Random on the wire until GameOver.
        Assert.That(ai.ResolvedStyle, Is.Not.EqualTo(AiStyle.Random));
        Assert.That(ClientPlayer.TryFind(ai.OwnerId, out ClientPlayer seen), Is.True);
        Assert.That(seen.aiStyle, Is.EqualTo(AiStyle.Random), "the client knows only the picked style");
        Assert.That(GameCore.LastMatchStats, Is.Null, "no result before GameOver");
        HudController hud = Object.FindAnyObjectByType<HudController>();
        yield return PlayModeMatch.WaitUntil(() => hud.Model.Players.Any(p => p.OwnerId == ai.OwnerId && p.IsAi && p.AiTag == "AI · Normal"), 5f);
        VisualElement hudRoot = HudTests.HudRoot();
        yield return PlayModeMatch.WaitUntil(() => hudRoot.Q("player-" + ai.OwnerId)?.Q<Label>("ai-tag-text")?.text == "AI · Normal", 5f);

        core.Cmd_Surrender();
        yield return PlayModeMatch.WaitUntil(() => core.CurrentState == GameState.GameOver && GameCore.LastMatchStats != null, 15f);
        Assert.That(GameCore.LastMatchStats.PlayedStyle(ai.OwnerId), Is.EqualTo(ai.ResolvedStyle), "revealed at GameOver");
        yield return PlayModeMatch.WaitUntil(() => hudRoot.Q("stats-" + ai.OwnerId) != null, 10f);
        Assert.That(hudRoot.Q("stats-" + ai.OwnerId).Q<Label>("ai-tag-text").text, Is.EqualTo($"AI · Normal · {ai.ResolvedStyle} (random)"));
    }
}

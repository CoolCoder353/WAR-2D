using System.Collections;
using System.Linq;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using WAR2D.UI;

/// <summary>v0.14 AI seats in a hosted match: lobby seats with their own player objects, and AIs counting like humans.</summary>
public class AiSeatPlayModeTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        return PlayModeMatch.TearDown();
    }

    private static IEnumerator Host()
    {
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator LobbyAisGetSeatsColoursCallsignsAndPlayerObjects()
    {
        yield return Host();
        GameCore core = GameCore.Instance;
        ClientPlayer host = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        host.ready = true;

        AiSeat first = core.AddAi(AiDifficulty.Hard, AiStyle.Random);
        Assert.That(first, Is.Not.Null);
        Assert.That(AiOwnerIds.IsAi(first.OwnerId), Is.True, "an owner id from the reserved range");
        Assert.That(host.ready, Is.False, "adding an AI clears everyone's ready");
        Assert.That(first.Colour, Is.Not.EqualTo(host.colourIndex), "the first unused colour");
        Assert.That(System.Array.IndexOf(config.Ai.Callsigns, first.Callsign), Is.GreaterThanOrEqualTo(0));
        Assert.That(core.AddAi(AiDifficulty.Scripted, AiStyle.Random), Is.Null, "Scripted is dev only, never a lobby seat");
        Assert.That(core.AddAi(AiDifficulty.Easy, AiStyle.Rusher, colour: first.Colour), Is.Null, "a taken colour is refused");
        Assert.That(core.AddAi(AiDifficulty.Easy, AiStyle.Rusher, colour: host.colourIndex), Is.Null, "the host's colour too");

        // The client sees it as a player, through its server-owned object.
        yield return PlayModeMatch.WaitUntil(() => ClientPlayer.TryFind(first.OwnerId, out _), 5f);
        Assert.That(ClientPlayer.TryFind(first.OwnerId, out ClientPlayer seen), Is.True);
        Assert.That((seen.isAi, seen.nickname, seen.colourIndex, seen.ready, seen.aiDifficulty, seen.aiStyle),
            Is.EqualTo((true, first.Callsign, first.Colour, true, AiDifficulty.Hard, AiStyle.Random)));
        Assert.That(seen.OwnerId, Is.EqualTo(first.OwnerId));
        Assert.That(seen.connectionToClient, Is.Null, "no connection: its TargetRpcs are never sent");
        Assert.That(core.LobbyStartState().Any(p => p.owner == first.OwnerId && p.ready), Is.True, "an AI is always ready");

        // Humans and AIs together fill the 8 seats; callsigns and colours stay unique.
        for (int i = 0; i < LobbyRules.MaxPlayers - 2; i++) Assert.That(core.AddAi(AiDifficulty.Normal, AiStyle.Balanced), Is.Not.Null, $"seat {i + 3}");
        Assert.That(core.SeatCount, Is.EqualTo(LobbyRules.MaxPlayers));
        Assert.That(core.AddAi(AiDifficulty.Normal, AiStyle.Balanced), Is.Null, "no ninth seat");
        Assert.That(core.AiPlayers.Values.Select(s => s.Callsign).Distinct().Count(), Is.EqualTo(core.AiPlayers.Count));
        Assert.That(core.AiPlayers.Values.Select(s => s.Colour).Append(host.colourIndex).Distinct().Count(), Is.EqualTo(LobbyRules.MaxPlayers));

        int removed = core.AiPlayers.Keys.Last();
        Assert.That(core.RemoveAi(removed), Is.True);
        Assert.That(core.AiPlayers.ContainsKey(removed), Is.False);
        yield return PlayModeMatch.WaitUntil(() => !ClientPlayer.TryFind(removed, out _), 5f);
        Assert.That(core.RemoveAi(removed), Is.False, "already gone");
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator AnAiCountsInOrderTeamsIncomeUpkeepStatsHudAndWinLoss()
    {
        yield return Host();
        GameCore core = GameCore.Instance;
        AiSeat ai = core.AddAi(AiDifficulty.Normal, AiStyle.Random);
        Assert.That(ai, Is.Not.Null);
        int me = PlayModeMatch.LocalOwner;

        DevApi.AllowForTests = true; // a test lobby skips the ready check; the match itself runs as normal
        core.Cmd_StartGame();
        yield return PlayModeMatch.WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        DevApi.AllowForTests = false;
        Assert.That(core.CurrentState, Is.EqualTo(GameState.PlacingHQ));
        Assert.That(core.PlayerOrder.Contains(ai.OwnerId) && core.PlayerOrder.Contains(me), Is.True, "in the player order");
        Assert.That(core.Teams.ContainsKey(ai.OwnerId), Is.True);
        Assert.That(core.TeamOf(ai.OwnerId), Is.Not.EqualTo(core.TeamOf(me)), "Free-for-all: a team of its own");
        Assert.That(core.MatchStartPlayerCount, Is.EqualTo(2), "counted in the match's player count");
        Assert.That(ai.Player.state, Is.EqualTo(PlayerState.Playing));
        Assert.That(ai.ResolvedStyle, Is.Not.EqualTo(AiStyle.Random), "Random resolves on the server at match start");
        Assert.That(ai.Client.aiStyle, Is.EqualTo(AiStyle.Random), "and never reaches the clients");

        // The AI places its HQ through the same action a player's command uses; the countdown waits for it.
        WorldStateManager world = WorldStateManager.Instance;
        Assert.That(world.TryFindBuildableAnchor(BuildingType.Base, 0f, ai.OwnerId, out int2 aiHq), Is.True);
        Assert.That(PlayerActions.PlaceHq(ai.OwnerId, aiHq), Is.True);
        Assert.That(PlayerActions.PlaceHq(ai.OwnerId, aiHq + new int2(20, 0)), Is.False, "one HQ");
        yield return PlayModeMatch.WaitUntil(() => HasBuilding(ai.OwnerId, BuildingType.Base), 5f);
        Assert.That(core.CurrentState, Is.EqualTo(GameState.PlacingHQ), "the host hasn't placed yet");
        yield return PlayModeMatch.PlaceHQ(null);

        // Income and upkeep by the second, like a human.
        DevApi.AllowForTests = true;
        Assert.That(world.TryFindBuildableAnchor(BuildingType.Barracks, 0f, me, out int2 spot), Is.True);
        Assert.That(world.DevPlaceBuilding(ai.OwnerId, BuildingType.Barracks, FarFrom(world, aiHq, spot, me), 0f), Is.True);
        DevApi.AllowForTests = false;
        yield return PlayModeMatch.WaitUntil(() => ai.Player.IncomeLastSecond > 0f && ai.Player.UpkeepLastSecond > 0f, 10f);
        Assert.That(core.Stats.Of(ai.OwnerId).BuildingsBuilt, Is.GreaterThanOrEqualTo(2), "statistics count its HQ and Barracks");

        // The HUD lists it alive under its callsign.
        HudController hud = UnityEngine.Object.FindAnyObjectByType<HudController>();
        yield return PlayModeMatch.WaitUntil(() => hud.Model.Players.Any(p => p.OwnerId == ai.OwnerId && !p.Eliminated && p.Nickname == ai.Callsign), 5f);

        // Win/loss: the AI surrenders through the player's action, and the host wins.
        Assert.That(PlayerActions.Surrender(ai.OwnerId), Is.True);
        yield return PlayModeMatch.WaitUntil(() => core.CurrentState == GameState.GameOver, 10f);
        Assert.That(ai.Player.state, Is.EqualTo(PlayerState.Eliminated));
        Assert.That(core.GetServerPlayerById(me).state, Is.EqualTo(PlayerState.Playing), "the host won");
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator AScriptedSeatIsAlivePlayerInTheHud()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        GameCore core = GameCore.Instance;
        int bot = AiOwnerIds.For(0);
        Assert.That(core.DevAddScriptedAi(bot, 1000f), Is.Null, "dev only");
        DevApi.AllowForTests = true;
        Assert.That(core.DevAddScriptedAi(1000001, 1000f), Is.Null, "only owner ids from the reserved range");
        AiSeat seat = core.DevAddScriptedAi(bot, 1000f);
        Assert.That(seat, Is.Not.Null);
        Assert.That(core.DevAddScriptedAi(bot, 1000f), Is.Null, "once");
        WorldStateManager world = WorldStateManager.Instance;
        Assert.That(world.DevPlaceBuilding(bot, BuildingType.Base, FarFrom(world, hq, hq, bot, BuildingType.Base), 1e6f), Is.True);
        DevApi.AllowForTests = false;

        HudController hud = UnityEngine.Object.FindAnyObjectByType<HudController>();
        yield return PlayModeMatch.WaitUntil(() => hud.Model.Players.Any(p => p.OwnerId == bot && !p.Eliminated && p.Nickname == seat.Callsign), 10f);
        Assert.That(PlayerPalette.ColourIndexOf(bot), Is.EqualTo(seat.Colour), "drawn in its own colour");
        Assert.That(core.CurrentState, Is.EqualTo(GameState.Playing));
    }

    /// <summary>A free anchor for <paramref name="type"/> at least 12 tiles from both points (any owner's placement rules aside).</summary>
    internal static int2 FarFrom(WorldStateManager world, int2 a, int2 b, int owner, BuildingType type = BuildingType.Barracks)
    {
        int2 size = WorldStateManager.GetBuildingSize(type);
        (int2 min, int2 max) = world.MapBounds;
        for (int y = min.y + 2; y < max.y - size.y - 2; y++)
        for (int x = min.x + 2; x < max.x - size.x - 2; x++)
        {
            int2 t = new int2(x, y);
            if (math.distance(t, a) < 12f || math.distance(t, b) < 12f) continue;
            bool free = true;
            foreach (int2 tile in Footprint.Tiles(t, size)) free &= world.Map.IsWalkable(tile) && world.Map.Grid.TileAt(tile) == TileType.Ground;
            if (free) return t;
        }
        Assert.Fail("no free spot");
        return default;
    }

    internal static bool HasBuilding(int owner, BuildingType type) => BuildingId(owner, type) != 0;

    internal static int BuildingId(int owner, BuildingType type)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(BuildingData));
        using var buildings = q.ToComponentDataArray<BuildingData>(Allocator.Temp);
        foreach (BuildingData b in buildings) if (b.ownerId == owner && b.buildingType == type) return b.id;
        return 0;
    }
}

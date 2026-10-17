using System.Collections.Generic;
using System.Linq;
using Config;
using Mirror;
using Unity.Mathematics;
using WAR2D.Dev.Logging;

/// <summary>
/// Private start points: the server deals each player their own options when the match scene is ready, tells only
/// that player (and their teammates), and places an HQ for anyone who doesn't pick in time. Nobody else learns an
/// option or a pick; every start site is plain open ground in the map each client regenerates.
/// </summary>
public partial class GameCore
{
    /// <summary>NetworkTime.time the pick time ends; 0 when random (nothing to pick) or Unlimited (no deadline).</summary>
    [SyncVar]
    public double HqPlacementEndTime;

    /// <summary>
    /// Deals every player their options with server-only randomness (never the map seed, which every client can
    /// reproduce), sends them, and starts the pick clock. Called once the match scene's map and simulation exist.
    /// </summary>
    [Server]
    internal void BeginPlacementPhase(WorldStateManager world)
    {
        HqPlacementEndTime = 0;
        int[] sites = null;
        if (world.Map.HqSites != null && world.Map.HqSites.Length > 0 && Settings.StartOptions > 0) sites = new int[world.Map.HqSites.Length];
        if (sites == null) return; // the scene's own map: no sites, so no dealing

        var owners = new List<int>(PlayerOrder);
        Dictionary<int, int[]> deal = StartPickRules.Deal(sites.Length, owners, Settings.StartOptions, System.Security.Cryptography.RandomNumberGenerator.GetInt32);
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (deal.TryGetValue(BuildingData.UIntToInt(entry.Key.netId), out int[] options)) { entry.Value.StartOptions = options; entry.Value.StartPick = -1; }
        }
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers) SendStartOptions(entry.Key, entry.Value);
        WarLog.Info(LogCategory.Net, $"Dealt {Settings.StartOptions} start options to {deal.Count} players; pick time {Settings.StartPickSeconds}");

        if (Settings.StartPickSeconds == 0) PlaceRemainingRandomly(world);
        else if (Settings.StartPickSeconds > 0) HqPlacementEndTime = NetworkTime.time + Settings.StartPickSeconds;
    }

    /// <summary>Tells a player their own options and pick, and (in a team) each teammate's. Nothing goes to anyone else.</summary>
    [Server]
    private void SendStartOptions(NetworkIdentity identity, ServerPlayer player)
    {
        if (player.StartOptions == null || identity == null) return;
        int owner = BuildingData.UIntToInt(identity.netId);
        var teamOwners = new List<int>();
        var teamOptions = new List<int>();
        var teamPicks = new List<int>();
        IEnumerable<(int, int[], int)> starts = ServerPlayers.Select(p => (BuildingData.UIntToInt(p.Key.netId), p.Value.StartOptions, p.Value.StartPick));
        foreach ((int otherOwner, int[] options, int pick) in StartPickRules.TeamView(owner, starts, AreTeammates))
        {
            teamOwners.Add(otherOwner);
            teamOptions.AddRange(options);
            teamPicks.Add(pick);
        }
        identity.GetComponent<ClientPlayer>().TargetStartOptions(player.connection, player.StartOptions, player.StartPick,
            teamOwners.ToArray(), teamOptions.ToArray(), player.StartOptions.Length, teamPicks.ToArray());
    }

    /// <summary>True for two different owners on the same team (Solo players are on teams of their own).</summary>
    private bool AreTeammates(int a, int b) => a != b && TeamOf(a) == TeamOf(b);

    /// <summary>Called by the server when an HQ is placed: records which option it is in and tells the owner's teammates.</summary>
    [Server]
    internal void OnHqPlaced(ClientPlayer placer, int2 anchor)
    {
        WorldStateManager world = WorldStateManager.Instance;
        ServerPlayer player = GetServerPlayerById(BuildingData.UIntToInt(placer.netId));
        if (world == null || player == null || player.StartOptions == null) return;
        int2[] sites = world.Map.HqSites;
        for (int i = 0; i < player.StartOptions.Length; i++)
        {
            int option = player.StartOptions[i];
            if (option < 0 || option >= sites.Length || !PlacementRules.InClearing(anchor, sites[option], WAR2D.World.MapGenerator.HqClearRadius)) continue;
            player.StartPick = option;
            break;
        }
        int owner = BuildingData.UIntToInt(placer.netId);
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> other in ServerPlayers)
        {
            int otherOwner = BuildingData.UIntToInt(other.Key.netId);
            if (AreTeammates(owner, otherOwner)) other.Key.GetComponent<ClientPlayer>().TargetTeammatePick(other.Value.connection, owner, player.StartPick);
        }
    }

    /// <summary>Places an HQ for every player who hasn't placed one, at a random one of their options (server randomness).</summary>
    [Server]
    internal void PlaceRemainingRandomly(WorldStateManager world)
    {
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers.ToList())
        {
            ClientPlayer client = entry.Key != null ? entry.Key.GetComponent<ClientPlayer>() : null;
            ServerPlayer player = entry.Value;
            if (client == null || client.hasPlacedHQ || player.StartOptions == null || player.StartOptions.Length == 0) continue;
            int first = System.Security.Cryptography.RandomNumberGenerator.GetInt32(player.StartOptions.Length);
            for (int k = 0; k < player.StartOptions.Length; k++)
            {
                int option = player.StartOptions[(first + k) % player.StartOptions.Length];
                if (TryPlaceHqNear(world, client, world.Map.HqSites[option])) break;
            }
        }
    }

    /// <summary>Places the HQ at the free footprint nearest <paramref name="site"/>'s centre, searching outward within the site.</summary>
    [Server]
    private static bool TryPlaceHqNear(WorldStateManager world, ClientPlayer client, int2 site)
    {
        int r = WAR2D.World.MapGenerator.HqClearRadius;
        var offsets = new List<int2>();
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++) offsets.Add(new int2(dx, dy));
        offsets.Sort((a, b) => math.lengthsq(a).CompareTo(math.lengthsq(b)));
        foreach (int2 offset in offsets)
            if (world.CheckPlacement(BuildingType.Base, site + offset, 0f, client) == PlacementResult.Ok)
                return world.PlaceBuilding(client, site + offset, BuildingType.Base, 0f);
        return false;
    }
}

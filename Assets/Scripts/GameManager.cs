using WAR2D.Dev.Logging;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using Unity.Entities;

/// <summary>
/// The GameManager class is responsible for managing the server's connections and disconnections.
/// It inherits from Mirror's NetworkManager class.
/// </summary>
public class GameManager : NetworkManager
{
    /// <summary>
    /// Singleton instance of the GameManager. 
    /// Ensures that only one GameManager exists in the scene at any time.
    /// </summary>
    public static GameManager Instance { get; private set; }

    /// <summary>
    /// The <see cref="GameCore"/> prefab under <c>Resources</c>. The server spawns one when it starts; it is
    /// not a scene object, because a <c>DontDestroyOnLoad</c> scene object in the editor's first scene never
    /// gets Mirror's scene id and a build client could not find it.
    /// </summary>
    public const string GameCorePrefabPath = "Network/GameCore";

    /// <summary>
    /// The transport's port from the scene (7778): the host always listens on it, and a join without a
    /// <c>:port</c> connects to it.
    /// </summary>
    public ushort DefaultPort { get; private set; } = 7778;

    /// <summary>
    /// Awake is called when the script instance is being loaded.
    /// It initializes the singleton instance and ensures it persists across scene loads.
    /// </summary>
    public override void Awake()
    {
        // If an instance of GameManager already exists, destroy this one and return
        if (Instance != null)
        {
            // The scene's duplicate: keep its transports (Fizzy schedules Steam work in OnEnable) from starting.
            foreach (Transport t in GetComponents<Transport>()) { t.CancelInvoke(); t.enabled = false; }
            Destroy(this);
            return;
        }

        // Set this GameManager as the singleton instance
        Instance = this;

        // Ensure this GameManager persists across scene loads
        DontDestroyOnLoad(this);

        // There is no dedicated server: a batch-mode player is a perf client (or a test) that starts its own
        // networking. Mirror's default auto-start would make every one of them host on the same port.
        headlessStartMode = HeadlessStartOptions.DoNothing;

        if (transport is PortTransport scenePort && scenePort.Port != 0) DefaultPort = scenePort.Port;

        // The host listens on the multiplexer (KCP and Steam); a client picks one child by route.
        hostTransport = transport;
        if (transport is MultiplexTransport mux && mux.transports != null)
            foreach (Transport child in mux.transports)
            {
                if (child == null) continue;
                if (child.GetType().Name == "FizzySteamworks") { if (steamTransport == null) steamTransport = child; }
                else if (directTransport == null) directTransport = child;
            }
        // Dev only (-perf, e.g. tools/ai-bench.sh's parallel hosts): -port n moves the KCP port off the scene's.
        if (DevApi.PerfFlag && ushort.TryParse(PerfMatch.ArgValue("-port"), out ushort devPort) && devPort != 0)
        {
            DefaultPort = devPort;
            if (directTransport is PortTransport kcp) kcp.Port = devPort;
            else if (transport is PortTransport single) single.Port = devPort;
            // The host listens on every child, so the multiplexer's other port transports (WebSocket) move too, 1000 up.
            if (transport is MultiplexTransport all && all.transports != null)
                foreach (Transport child in all.transports)
                    if (child != directTransport && child is PortTransport other) other.Port = (ushort)Mathf.Min(ushort.MaxValue, devPort + 1000);
        }
        // Without Steam (not running, -noSteam, tests) the Steam transport would throw on start: host on KCP only.
        if (!WAR2D.Net.Steam.SteamServices.Available && transport is MultiplexTransport plain && steamTransport != null)
        {
            var kept = new List<Transport>(plain.transports);
            kept.Remove(steamTransport);
            plain.transports = kept.ToArray();
            steamTransport.CancelInvoke(); // Fizzy schedules its relay init in OnEnable
            steamTransport.enabled = false;
            steamTransport = null;
        }

        // Load Game Config
        Config.ConfigLoader.LoadConfig();

        // Clients spawn GameCore from its prefab (registered with the client when it starts).
        GameObject core = Resources.Load<GameObject>(GameCorePrefabPath);
        if (core == null) WarLog.Error(LogCategory.Net, $"Resources/{GameCorePrefabPath}.prefab is missing: no match can be hosted or joined.");
        else if (!spawnPrefabs.Contains(core)) spawnPrefabs.Add(core);
    }

    /// <summary>
    /// Called when the script instance is being destroyed.
    /// Cleans up event subscriptions and stops client/server if active.
    /// </summary>
    public override void OnDestroy()
    {
        //Make sure we disconnect
        if (NetworkServer.active)
        {
            StopServer();
        }
        if (NetworkClient.isConnected)
        {
            StopClient();
        }
    }


    /// <summary>
    /// Called on the server when it starts.
    /// </summary>
    public override void OnStartServer()
    {
        base.OnStartServer();
        if (!Config.ConfigLoader.IsValid)
        {
            WarLog.Error(LogCategory.Net, "Stopping server: GameConfig.xml is invalid.");
            StopServer();
            if (Application.isBatchMode) Application.Quit(1);
            return;
        }
        GameObject corePrefab = Resources.Load<GameObject>(GameCorePrefabPath);
        if (GameCore.Instance == null && corePrefab != null) NetworkServer.Spawn(Instantiate(corePrefab));
        WarLog.Info(LogCategory.Net, "Server has started");
    }

    /// <summary>Closes the router port opened for internet players (if the menu's Host opened one).</summary>
    public override void OnStopServer()
    {
        WAR2D.Net.HostReachability.Stop(false);
        WAR2D.Net.Steam.SteamHostLobby.Close();
        base.OnStopServer();
    }

    /// <summary>Stops networking, then waits briefly for the router to drop the port mapping.</summary>
    public override void OnApplicationQuit()
    {
        base.OnApplicationQuit();
        WAR2D.Net.HostReachability.Stop(true);
    }

    /// <summary>
    /// Called on the server when a client connects. Only the lobby takes new players, and only up to
    /// <see cref="LobbyRules.MaxPlayers"/> seats (AIs included): a player joining a running match would have no team,
    /// colour slot, HQ or replication, so the server closes the connection (the joining client says so). A Private
    /// lobby (v0.14) refuses every connection but the host's own (the authenticator tells the client why first).
    /// </summary>
    [Server]
    public override void OnServerConnect(NetworkConnectionToClient conn)
    {
        base.OnServerConnect(conn);
        GameCore core = GameCore.Instance;
        // AI seats take places too: humans and AIs together are at most MaxPlayers; a Private lobby takes only the host (v0.14).
        if (core == null || LobbyRules.MayConnect(core.Settings.Visibility, conn is LocalConnectionToClient, core.CurrentState,
                NetworkServer.connections.Count + core.AiPlayers.Count, out string refusal)) return;
        WarLog.Info(LogCategory.Net, refusal != null
            ? $"Refusing connection {conn.connectionId}: {refusal}"
            : $"Refusing connection {conn.connectionId}: the match is {core.CurrentState} with {NetworkServer.connections.Count - 1} players.");
        LastRefusedConnection = conn.connectionId;
        conn.Disconnect();
    }

    /// <summary>The connection id <see cref="OnServerConnect"/> last refused (tests read it); -1 for none.</summary>
    internal static int LastRefusedConnection { get; set; } = -1;

    /// <summary>
    /// Called on the server when a new player has connected.
    /// Adds the player to the list of players if they are not already in it.
    /// </summary>
    /// <param name="conn">The connection of the new player.</param>
    [Server]
    public override void OnServerAddPlayer(NetworkConnectionToClient conn)
    {
        base.OnServerAddPlayer(conn);

        if (GameCore.Instance == null) return;
        GameCore.Instance.AddPlayer(conn, Config.ConfigLoader.LoadConfig().Resources.StartingResources);

        // Log the connection
        WarLog.Info(LogCategory.Net, $"Player {conn.connectionId} has connected");
        WarLog.Info(LogCategory.Net, $"There are now {GameCore.Instance.ServerPlayers.Count} players connected");
    }

    /// <summary>
    /// Called on the server when a player has disconnected.
    /// Removes the player from the list of players.
    /// </summary>
    /// <param name="conn">The connection of the player who disconnected.</param>
    [Server]
    public override void OnServerDisconnect(NetworkConnectionToClient conn)
    {
        WAR2D.Net.Replication.ReplicationService.Instance?.OnClientLeft(conn);
        (authenticator as WAR2D.Net.Steam.SteamAuthenticator)?.OnServerDisconnect(conn); // a sign-in still in progress
        // Remove the disconnected player from the list of players
        if (GameCore.Instance != null) GameCore.Instance.OnPlayerLeave(conn);
        else CommandGate.Forget(conn.connectionId);

        // Log the disconnection
        WarLog.Info(LogCategory.Net, $"Player {conn.connectionId} has disconnected");



        if (WAR2D.Net.Steam.SteamIdentity.TryGet(conn.connectionId, out ulong steamId)) WAR2D.Net.Steam.SteamServices.Backend?.EndAuthSession(steamId);
        WAR2D.Net.Steam.SteamIdentity.Forget(conn.connectionId);

        base.OnServerDisconnect(conn);
    }

    /// <summary>
    /// Called on the client when an error occurs.
    /// Logs the error reason.
    /// </summary>
    /// <param name="error">The transport error.</param>
    /// <param name="reason">The reason for the error.</param>
    [Client]
    public override void OnClientError(TransportError error, string reason)
    {
        base.OnClientError(error, reason);

        // Log the error reason
        WarLog.Info(LogCategory.Net, $"Error: {reason}");
    }

    /// <summary>Registers the unit replication handler (it forwards batches to <see cref="WAR2D.Net.Replication.ReplicationClient"/>).</summary>
    [Client]
    public override void OnStartClient()
    {
        base.OnStartClient();
        NetworkClient.RegisterHandler<WAR2D.Net.Replication.ReplicationBatch>(WAR2D.Net.Replication.ReplicationClient.Receive, false);
        NetworkClient.RegisterHandler<WAR2D.Net.Replication.FogBatch>(WAR2D.Net.Replication.ClientFog.Receive, false);
        NetworkClient.RegisterHandler<WAR2D.Net.Replication.TerrainBatch>(WAR2D.Net.Replication.ClientTerrain.Receive, false);
        NetworkClient.RegisterHandler<WAR2D.Net.Replication.BuildingBatch>(WAR2D.Net.Replication.ClientBuildings.Receive, false);
        NetworkClient.RegisterHandler<WAR2D.Net.Replay.ReplayOffer>(o =>
        {
            Config.ReplaysConfig replays = Config.ConfigLoader.LoadConfig().Replays;
            // The file is gzip of at most MaxMegabytes of records, so anything much larger isn't a real recording.
            WAR2D.Net.Replay.ReplayReceiver.Install(WAR2D.Net.Replay.ReplayLibrary.Folder, ((long)replays.MaxMegabytes << 20) + (1L << 20), replays.KeepCount);
            bool want = SettingsStore.Current.SaveReplays;
            WAR2D.Net.Replay.ReplayReceiver.Current.OnOffer(o, want);
            if (NetworkClient.localPlayer != null && NetworkClient.localPlayer.TryGetComponent(out ClientPlayer me)) me.CmdReplayWanted(o.Id, want);
        }, false);
        NetworkClient.RegisterHandler<WAR2D.Net.Replay.ReplayChunk>(c => WAR2D.Net.Replay.ReplayReceiver.Current?.OnChunk(c), false);
    }

    /// <summary>Called on the client when it connects to the server.</summary>
    [Client]
    public override void OnClientConnect()
    {
        base.OnClientConnect();
    }

    /// <summary>
    /// Called on the client when it disconnects from the server.
    /// Not [Client]: Mirror calls it after the client is already inactive. Mirror loads the
    /// offline scene (Main_Menu) itself.
    /// </summary>
    public override void OnClientDisconnect()
    {
        base.OnClientDisconnect();
        WAR2D.Net.Replay.ReplayReceiver.Clear();
        WarLog.Info(LogCategory.Net, "Disconnected from server");
    }


    /// <summary>
    /// Stops the host if the local player is the server, otherwise stops the client.
    /// Resets the GameCore and reloads the main menu.
    /// </summary>
    public void LeaveLobby()
    {

        if (NetworkClient.localPlayer && NetworkClient.localPlayer.isServer)
        {
            WarLog.Info(LogCategory.Net, $"The game manager ({GameManager.Instance}) is stopping the host ({NetworkClient.localPlayer})");
            GameManager.Instance.StopHost();

        }
        else
        {
            GameManager.Instance.StopClient();
        }

        if (GameCore.Instance != null)
        {
            //Reset the game core
            Destroy(GameCore.Instance.gameObject);
        }

        if (WorldStateManager.Instance != null)
        {
            //Reset the world state manager
            Destroy(WorldStateManager.Instance.gameObject);
        }

        //Reset the scene by reloading it
        SceneManager.LoadScene("Main_Menu");


    }


    /// <summary>
    /// Quits the game. If the game is running in the Unity editor, it stops the game.
    /// </summary>
    public void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    /// <summary>How this client reached (or is reaching) its host.</summary>
    public WAR2D.Net.Steam.ConnectionRoute ActiveRoute { get; private set; }

    /// <summary>The KCP and Steam children of the multiplexer (found in Awake unless assigned); the host listens on both.</summary>
    [SerializeField] private Transport directTransport, steamTransport;

    private Transport hostTransport;

    /// <summary>Dev only (the perf clients): the transport direct (KCP) joins use, in place of the scene's.</summary>
    internal void SetDirectTransport(Transport t) => directTransport = t;

    /// <summary>Whether a route may be used right now: Steam routes need Steam, Direct routes need <c>-direct</c> (or the editor / dev API).</summary>
    internal static bool CanConnect(WAR2D.Net.Steam.ConnectionRoute route, bool steamAvailable, bool directAllowed, out string why)
    {
        why = null;
        if (route.Kind == WAR2D.Net.Steam.RouteKind.Steam && !steamAvailable) { why = "Steam is not running."; return false; }
        if (route.Kind == WAR2D.Net.Steam.RouteKind.Direct && !directAllowed) { why = "Direct connections are off in this build; start the game with -direct."; return false; }
        return true;
    }

    /// <summary>Whether Direct (KCP, no Steam sign-in) play is on in this process.</summary>
    internal static bool DirectAllowed => WAR2D.Net.Steam.SteamAuthPolicy.DirectAllowed(
        Application.isEditor, DevApi.Allowed, System.Environment.GetCommandLineArgs());

    private void UseTransport(Transport t)
    {
        if (t == null) return; // single-transport scenes (tests): leave the configured one
        transport = t;
        Transport.active = t;
    }

    /// <summary>
    /// Starts hosting a server.
    /// </summary>
    public void HostServer()
    {
        if (!Config.ConfigLoader.IsValid)
        {
            WarLog.Error(LogCategory.Net, "Refusing to host: GameConfig.xml is invalid. See [GameConfig] errors above.");
            return;
        }
        if (!NetworkClient.active)
        {
            UseTransport(hostTransport); // a previous join may have picked one child
            ActiveRoute = default;
            SetPort(DefaultPort); // a previous join may have used another port
            StartHost();
        }
    }

    /// <summary>
    /// Connects to a server at the specified address: a host, optionally with a port (<c>host:port</c> or
    /// <c>[ipv6]:port</c>; <see cref="DefaultPort"/> otherwise), for hosts behind a tunnel or a port forward
    /// to another external port.
    /// </summary>
    /// <param name="address">The address to connect to.</param>
    public void ConnectToServer(string address)
    {
        if (!WAR2D.Net.Steam.ConnectionRoute.TryParse(address, DefaultPort, out WAR2D.Net.Steam.ConnectionRoute route))
        {
            WarLog.Warn(LogCategory.Net, $"Not a server address: '{address}'. Enter a host, host:port, or steam:<id>.");
            return;
        }
        Connect(route);
    }

    /// <summary>Connects to a host by route: a Steam host over the relay, or an address over KCP.</summary>
    public void Connect(WAR2D.Net.Steam.ConnectionRoute route)
    {
        if (!CanConnect(route, WAR2D.Net.Steam.SteamServices.Available, DirectAllowed, out string why))
        {
            WarLog.Warn(LogCategory.Net, $"Not connecting to {route}: {why}");
            return;
        }
        if (NetworkServer.active) return; // hosting: there is nothing to join
        if (NetworkClient.active) StopClient(); // a lingering connection must not swallow this join
        ActiveRoute = route;
        if (route.Kind == WAR2D.Net.Steam.RouteKind.Steam)
        {
            UseTransport(steamTransport);
            networkAddress = route.SteamId.ToString();
        }
        else
        {
            UseTransport(directTransport);
            networkAddress = route.Host;
            SetPort(route.Port);
        }
        StartClient();
    }

    /// <summary>
    /// Sets the port the transport connects to (a multiplexer sets its first available transport, KCP). Only
    /// on a change: a headless multiplexer ignores the setter and warns.
    /// </summary>
    private void SetPort(ushort port)
    {
        if (transport is PortTransport t && t.Port != port) t.Port = port;
    }
}
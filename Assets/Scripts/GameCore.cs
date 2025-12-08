using Mirror;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Represents the various states of the game.
/// </summary>
public enum GameState
{
    /// <summary>Players are in the lobby waiting for the game to start.</summary>
    Lobby,
    /// <summary>Players are placing their Headquarters (HQ).</summary>
    PlacingHQ,
    /// <summary>Countdown before the game officially starts.</summary>
    Countdown,
    /// <summary>The main gameplay loop is active.</summary>
    Playing,
    /// <summary>The game has ended.</summary>
    GameOver
}

/// <summary>
/// The GameCore class is responsible for managing the server's game state.
/// It inherits from Mirror's NetworkBehaviour class.
/// </summary>
public class GameCore : NetworkBehaviour
{
    /// <summary>
    /// Singleton instance of the GameCore. 
    /// Ensures that only one GameCore exists in the scene at any time.
    /// </summary>
    public static GameCore Instance { get; private set; }

    /// <summary>
    /// The current state of the game, synchronized across the network.
    /// </summary>
    [SyncVar]
    public GameState CurrentState = GameState.Lobby;

    // Events for UI
    /// <summary>Event triggered when the local player wins.</summary>
    public event System.Action OnLocalPlayerWon;
    /// <summary>Event triggered when the local player loses.</summary>
    public event System.Action OnLocalPlayerLost;
    /// <summary>Event triggered when the game state changes.</summary>
    public event System.Action<GameState> OnGameStateChanged;

    /// <summary>
    /// List of ServerPlayer objects representing the players on the server.
    /// Key is the NetworkIdentity of the player's connection.
    /// </summary>
    public Dictionary<NetworkIdentity, ServerPlayer> ServerPlayers = new Dictionary<NetworkIdentity, ServerPlayer>();

    /// <summary>
    /// NetworkConnection object representing the owner of the server.
    /// </summary>
    private NetworkConnection serverOwner;

    private float countdownTimer = 0f;

    /// <summary>
    /// Awake is called when the script instance is being loaded.
    /// It initializes the singleton instance.
    /// </summary>
    public void Awake()
    {
        TIM.Console.Log($"GameCore Awake", TIM.MessageType.Network);

        if (Instance != null)
        {
            Destroy(this);
            return;
        }

        Instance = this;

        DontDestroyOnLoad(this);

    }

    /// <summary>
    /// Called on the server when it starts.
    /// </summary>
    [Server]
    public override void OnStartServer()
    {
        base.OnStartServer();
    }

    /// <summary>
    /// Sends the individual player's server player object to each client.
    /// This ensures clients have up-to-date information about their own resources and state.
    /// </summary>
    [Server]
    private void UpdateClientsPrivateData()
    {
        foreach (var player in ServerPlayers)
        {
            ClientPlayer clientPlayer = player.Key.GetComponent<ClientPlayer>();
            ////Debug.Log($"Server sending private data to {clientPlayer.GetConnectionToClient().connectionId} ({player.Value.data.Serialize()}, R:{player.Value.data.resources})");

            clientPlayer.SetServerPlayer(clientPlayer.GetConnectionToClient(), player.Value.data.Serialize());
        }
    }

    /// <summary>
    /// Called every frame on the server after Update.
    /// Used to synchronize private data to clients.
    /// </summary>
    [ServerCallback]
    public void LateUpdate()
    {
        //TODO: Change this to 1 as a safe guard. For testing purposes it is set to 0
        if (ServerPlayers.Count > 0)
        {
            UpdateClientsPrivateData();
        }

    }

    /// <summary>
    /// Handles a player disconnecting from the server.
    /// Removes the player from the list and handles server ownership transfer if necessary.
    /// </summary>
    /// <param name="conn">The connection of the player who left.</param>
    [Server]
    public void OnPlayerLeave(NetworkConnectionToClient conn)
    {
        //        Debug.Log($"Player {conn.connectionId} has disconnected");


        ServerPlayers.Remove(conn.identity);
        ServerPlayers.TrimExcess();

        //For each player still in the game, call the RPC_RemoveClientLobbyUI method
        foreach (var player in ServerPlayers)
        {
            ClientPlayer client = player.Key.GetComponent<ClientPlayer>();
            if (client.lobbySystem != null)
            {
                client.RPC_RemoveClientLobbyUI();
            }
        }

        // If the disconnected player was the server owner, set a new server owner
        if (IsServerOwner(conn))
        {

            //If the server owner was also the server -> kick all clients

            if (serverOwner.identity.isServer)
            {
                NetworkServer.Shutdown();
            }
            else
            {
                SetServerOwner(ServerPlayers.First().Key.connectionToClient);
            }


        }
    }

    /// <summary>
    /// Sets the owner of the server.
    /// </summary>
    /// <param name="conn">The connection to set as the new owner.</param>
    [Server]
    public void SetServerOwner(NetworkConnectionToClient conn)
    {
        if (serverOwner != null)
        {
            serverOwner.identity.RemoveClientAuthority();
        }

        Debug.Log($"Player {conn.identity.netId} is now the server owner");
        serverOwner = conn;

        // Assign client authority to the new server owner
        conn.identity.AssignClientAuthority(conn);

        Debug.Log($"Server owner is now {conn.identity.netId}");
    }

    /// <summary>
    /// Checks if a NetworkConnection object is the owner of the server.
    /// </summary>
    /// <param name="conn">The connection to check.</param>
    /// <returns>True if the connection is the server owner, false otherwise.</returns>
    [Server]
    public bool IsServerOwner(NetworkConnectionToClient conn)
    {
        return conn.identity == serverOwner.identity;
    }

    /// <summary>
    /// Adds resources to a specific player.
    /// </summary>
    /// <param name="conn">The connection of the player.</param>
    /// <param name="amount">The amount of resources to add.</param>
    [Server]
    public void AddResourcesToPlayer(NetworkConnectionToClient conn, float amount)
    {
        ServerPlayers[conn.identity].AddResources(amount);
    }

    /// <summary>
    /// Gets a ServerPlayer by their owner ID (netId).
    /// </summary>
    /// <param name="ownerId">The netId of the player.</param>
    /// <returns>The ServerPlayer object, or null if not found.</returns>
    [Server]
    public ServerPlayer GetServerPlayerById(int ownerId)
    {
        foreach (var kvp in ServerPlayers)
        {
            if (kvp.Key.netId == (uint)ownerId)
            {
                return kvp.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Public property to access server players for ECS systems.
    /// </summary>
    public IEnumerable<ServerPlayer> serverPlayers => ServerPlayers.Values;

    /// <summary>
    /// Command sent to the server to start the game.
    /// If the connection is the server owner, it changes the scene to the game scene.
    /// </summary>
    /// <param name="connection">The connection sending the command (automatically filled by Mirror).</param>
    [Command(requiresAuthority = false)]
    public void Cmd_StartGame(NetworkConnectionToClient connection = null)
    {
        Debug.Log("Starting game");
        if (IsServerOwner(connection))
        {
            Debug.Log("Changing scene");
            CurrentState = GameState.PlacingHQ;
            GameManager.Instance.ServerChangeScene("Map_2");
        }
    }

    /// <summary>
    /// Eliminates a player from the game.
    /// </summary>
    /// <param name="conn">The connection of the player to eliminate.</param>
    [Server]
    public void EliminatePlayer(NetworkConnectionToClient conn)
    {
        if (ServerPlayers.TryGetValue(conn.identity, out ServerPlayer player))
        {
            player.state = PlayerState.Eliminated;
            RpcOnPlayerLost(conn.identity);
            Debug.Log($"Player {conn.connectionId} eliminated.");
        }
    }

    /// <summary>
    /// Declares a winner for the game.
    /// </summary>
    /// <param name="conn">The connection of the winning player.</param>
    [Server]
    public void DeclareWinner(NetworkConnectionToClient conn)
    {
        CurrentState = GameState.GameOver;
        RpcOnPlayerWon(conn.identity);
        Debug.Log($"Player {conn.connectionId} won!");
    }

    /// <summary>
    /// ClientRpc called when a player wins.
    /// </summary>
    /// <param name="winner">The NetworkIdentity of the winner.</param>
    [ClientRpc]
    public void RpcOnPlayerWon(NetworkIdentity winner)
    {
        // UI Implementation to handle this
        if (winner.isLocalPlayer)
        {
            Debug.Log("Victory!");
            OnLocalPlayerWon?.Invoke();
        }
    }

    /// <summary>
    /// ClientRpc called when a player loses.
    /// </summary>
    /// <param name="loser">The NetworkIdentity of the loser.</param>
    [ClientRpc]
    public void RpcOnPlayerLost(NetworkIdentity loser)
    {
        // UI Implementation to handle this
        if (loser.isLocalPlayer)
        {
            Debug.Log("Defeat!");
            OnLocalPlayerLost?.Invoke();
        }
    }
    
    /// <summary>
    /// Called when a player places their HQ.
    /// Checks if all players have placed their HQ to start the countdown.
    /// </summary>
    /// <param name="conn">The connection of the player who placed the HQ.</param>
    [Server]
    public void PlayerPlacedHQ(NetworkConnectionToClient conn)
    {
        if (ServerPlayers.TryGetValue(conn.identity, out ServerPlayer player))
        {
            player.state = PlayerState.Playing;
            
            // Notify client that HQ is placed
            ClientPlayer clientPlayer = conn.identity.GetComponent<ClientPlayer>();
            if (clientPlayer != null)
            {
                clientPlayer.TargetHQPlaced(conn);
            }

            // Check if all players have placed HQ
            bool allPlaced = true;
            foreach (var p in ServerPlayers.Values)
            {
                if (p.state == PlayerState.PlacingHQ)
                {
                    allPlaced = false;
                    break;
                }
            }
            
            if (allPlaced)
            {
                CurrentState = GameState.Countdown;
                countdownTimer = 3f;
            }
        }
    }

    /// <summary>
    /// Update loop for managing game state transitions (e.g., countdown).
    /// </summary>
    [ServerCallback]
    public void Update()
    {
        if (CurrentState == GameState.Countdown)
        {
            countdownTimer -= Time.deltaTime;
            if (countdownTimer <= 0)
            {
                CurrentState = GameState.Playing;
            }
        }
    }
}
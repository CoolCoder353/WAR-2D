using Mirror;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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



    /// <summary>
    /// List of ServerPlayer objects representing the players on the server.
    /// Key is the NetworkIdentity of the player's connection.
    /// </summary>
    public Dictionary<NetworkIdentity, ServerPlayer> ServerPlayers = new Dictionary<NetworkIdentity, ServerPlayer>();

    public int playersReadyToStart = 0;

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


        ServerPlayers[conn.identity].state = PlayerState.Eliminated;
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

        //Get all entities that belong to the disconnected player and destroy them
        WorldStateManager.Instance.DestroyAllEntitiesOwnedByPlayer((int)conn.identity.netId);

    }

    [Server]
    public override void OnStopServer()
    {
        base.OnStopServer();
        ServerPlayers.Clear();
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



    [Command(requiresAuthority = false)]
    public void Cmd_ReadyToStartGame(NetworkConnectionToClient connection = null)
    {
        Debug.Log($"Player {connection.connectionId} is ready to start the game.");
        playersReadyToStart++;

        if (playersReadyToStart >= ServerPlayers.Count)
        {
            Debug.Log("All players are ready. Starting the game.");
            CurrentState = GameState.Playing;
        }
    }




    [Server]
    public void DeclareDraw()
    {
        Debug.Log("Game ended in a draw - all players were eliminated!");

        DeclareWinner(-1); // Using -1 to indicate a draw since no player will have an id of -1
    }

    [Server]
    public void DeclareWinner(int playerId)
    {
        Debug.Log($"Player {playerId} has won the game!");

        // Notify all clients about the winner
        foreach (var player in ServerPlayers)
        {
            ClientPlayer clientPlayer = player.Key.GetComponent<ClientPlayer>();
            if (clientPlayer != null)
            {
                if (player.Key.netId == (uint)playerId)
                {
                    Debug.Log($"Notifying player {playerId} of their victory!");
                    clientPlayer.RpcOnPlayerWon(player.Key.connectionToClient);
                }
                else
                {
                    Debug.Log($"Notifying player {playerId} of their defeat!");
                    player.Value.state = PlayerState.Eliminated;
                    clientPlayer.RpcOnPlayerLost(player.Key.connectionToClient);
                }
            }
        }

        CurrentState = GameState.GameOver;
    }

    [Server]
    public void EliminatePlayer(int playerId)
    {
        Debug.Log($"Player {playerId} has been eliminated!");

        // Notify the eliminated player
        foreach (var player in ServerPlayers)
        {
            if (player.Key.netId == (uint)playerId)
            {
                ClientPlayer clientPlayer = player.Key.GetComponent<ClientPlayer>();
                if (clientPlayer != null)
                {
                    player.Value.state = PlayerState.Eliminated;
                    Debug.Log($"Notifying player {playerId} of their elimination!");
                    clientPlayer.RpcOnPlayerLost(player.Key.connectionToClient);
                }
                break;
            }
        }
    }
    /// <summary>
    /// ClientRpc called when the game ends in a draw.
    /// </summary>
    [ClientRpc]
    public void RpcGameDraw()
    {
        Debug.Log("Game ended in a draw - all players were eliminated!");
        // Could add UI event here if needed
    }

    /// <summary>
    /// Called from WorldStateManager after an HQ is successfully placed.
    /// Checks if all players have placed their HQ and transitions to Countdown state if so.
    /// </summary>
    [Server]
    public void CheckHQPlacementProgress()
    {
        // Count how many players still need to place HQ
        int remainingPlayers = 0;
        foreach (var player in ServerPlayers)
        {
            ClientPlayer clientPlayer = player.Key.GetComponent<ClientPlayer>();
            if (clientPlayer != null && !clientPlayer.hasPlacedHQ)
            {
                remainingPlayers++;
            }
        }

        Debug.Log($"HQ Placement Progress: {remainingPlayers} player(s) still need to place HQ.");

        // Notify all clients about the progress
        RpcUpdateHQPlacementProgress(remainingPlayers);

        // Check if all players have placed HQ
        if (remainingPlayers == 0)
        {
            Debug.Log("All players have placed HQ! Starting countdown.");
            CurrentState = GameState.Countdown;
            countdownTimer = 3f;
        }
    }

    /// <summary>
    /// ClientRpc that updates all clients on HQ placement progress.
    /// </summary>
    /// <param name="remainingPlayersCount">Number of players still needing to place HQ.</param>
    [ClientRpc]
    public void RpcUpdateHQPlacementProgress(int remainingPlayersCount)
    {
        // This RPC is called to keep clients in sync with progress
        // Clients can use this to update UI if needed
        if (remainingPlayersCount > 0)
        {
            Debug.Log($"[Client] {remainingPlayersCount} player(s) still placing HQ");
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
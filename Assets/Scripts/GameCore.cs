using Mirror;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Tilemaps;

public enum GameState
{
    Lobby,
    PlacingHQ,
    Playing,
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

    [SyncVar]
    public GameState CurrentState = GameState.Lobby;

    // Events for UI
    public event System.Action OnLocalPlayerWon;
    public event System.Action OnLocalPlayerLost;
    public event System.Action<GameState> OnGameStateChanged;

    /// <summary>
    /// List of ServerPlayer objects representing the players on the server.
    /// </summary>
    public Dictionary<NetworkIdentity, ServerPlayer> ServerPlayers = new Dictionary<NetworkIdentity, ServerPlayer>();

    /// <summary>
    /// NetworkConnection object representing the owner of the server.
    /// </summary>
    private NetworkConnection serverOwner;

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

    [Server]
    public override void OnStartServer()
    {
        base.OnStartServer();
    }



    //<summary>
    //Sends the individual player's server player object to each client
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



    [ServerCallback]
    public void LateUpdate()
    {
        //TODO: Change this to 1 as a safe guard. For testing purposes it is set to 0
        if (ServerPlayers.Count > 0)
        {
            UpdateClientsPrivateData();
        }

    }








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
    [Server]
    public bool IsServerOwner(NetworkConnectionToClient conn)
    {
        return conn.identity == serverOwner.identity;
    }

    [Server]
    public void AddResourcesToPlayer(NetworkConnectionToClient conn, float amount)
    {
        ServerPlayers[conn.identity].AddResources(amount);
    }

    /// <summary>
    /// Gets a ServerPlayer by their owner ID (netId).
    /// </summary>
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

    [Server]
    public void DeclareWinner(NetworkConnectionToClient conn)
    {
        CurrentState = GameState.GameOver;
        RpcOnPlayerWon(conn.identity);
        Debug.Log($"Player {conn.connectionId} won!");
    }

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
    
    [Server]
    public void PlayerPlacedHQ(NetworkConnectionToClient conn)
    {
        if (ServerPlayers.TryGetValue(conn.identity, out ServerPlayer player))
        {
            player.state = PlayerState.Playing;
            
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
                CurrentState = GameState.Playing;
            }
        }
    }
}
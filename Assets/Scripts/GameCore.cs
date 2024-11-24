using Mirror;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Tilemaps;
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
    /// Command sent to the server to start the game.
    /// If the connection is the server owner, it changes the scene to the game scene.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void Cmd_StartGame(NetworkConnectionToClient connection = null)
    {
        if (IsServerOwner(connection))
        {
            GameManager.Instance.ServerChangeScene("Map_1");
        }
    }
}
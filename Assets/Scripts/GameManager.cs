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
    /// Awake is called when the script instance is being loaded.
    /// It initializes the singleton instance and ensures it persists across scene loads.
    /// </summary>
    public override void Awake()
    {
        // If an instance of GameManager already exists, destroy this one and return
        if (Instance != null)
        {
            Destroy(this);
            return;
        }

        // Set this GameManager as the singleton instance
        Instance = this;

        // Ensure this GameManager persists across scene loads
        DontDestroyOnLoad(this);

        // Load Game Config
        Config.ConfigLoader.LoadConfig();
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
            Debug.LogError("Stopping server: GameConfig.xml is invalid.");
            StopServer();
            if (Application.isBatchMode) Application.Quit(1);
            return;
        }
        Debug.Log("Server has started");
    }

    /// <summary>
    /// Called on the server when a new player has connected.
    /// Adds the player to the list of players if they are not already in it.
    /// </summary>
    /// <param name="conn">The connection of the new player.</param>
    [Server]
    public override void OnServerAddPlayer(NetworkConnectionToClient conn)
    {
        base.OnServerAddPlayer(conn);

        GameCore.Instance.AddPlayer(conn, Config.ConfigLoader.LoadConfig().Resources.StartingResources);

        // Log the connection
        Debug.Log($"Player {conn.connectionId} has connected");
        Debug.Log($"There are now {GameCore.Instance.ServerPlayers.Count} players connected");
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
        // Remove the disconnected player from the list of players
        GameCore.Instance.OnPlayerLeave(conn);


        // Log the disconnection
        Debug.Log($"Player {conn.connectionId} has disconnected");
        Debug.Log($"There are now {GameCore.Instance.ServerPlayers.Count} players connected");



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
        Debug.Log($"Error: {reason}");
    }

    /// <summary>
    /// Called on the client when it connects to the server.
    /// </summary>
    /// <summary>Registers the unit replication handler (it forwards batches to <see cref="WAR2D.Net.Replication.ReplicationClient"/>).</summary>
    [Client]
    public override void OnStartClient()
    {
        base.OnStartClient();
        NetworkClient.RegisterHandler<WAR2D.Net.Replication.ReplicationBatch>(WAR2D.Net.Replication.ReplicationClient.Receive, false);
    }

    [Client]
    public override void OnClientConnect()
    {
        base.OnClientConnect();
    }

    /// <summary>
    /// Called on the client when it disconnects from the server.
    /// </summary>
    [Client]
    public override void OnClientDisconnect()
    {
        base.OnClientDisconnect();

        // Log the disconnection
        Debug.Log("Disconnected from server");

        //Return to main menu via resetting the scene
        SceneManager.LoadScene("Main_Menu");
    }


    /// <summary>
    /// Stops the host if the local player is the server, otherwise stops the client.
    /// Resets the GameCore and reloads the main menu.
    /// </summary>
    public void LeaveLobby()
    {

        if (NetworkClient.localPlayer && NetworkClient.localPlayer.isServer)
        {
            Debug.Log($"The game manager ({GameManager.Instance}) is stopping the host ({NetworkClient.localPlayer})");
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

    /// <summary>
    /// Starts hosting a server.
    /// </summary>
    public void HostServer()
    {
        if (!Config.ConfigLoader.IsValid)
        {
            Debug.LogError("Refusing to host: GameConfig.xml is invalid. See [GameConfig] errors above.");
            return;
        }
        if (!NetworkClient.active)
        {
            StartHost();
        }
    }

    /// <summary>
    /// Connects to a server at the specified address.
    /// </summary>
    /// <param name="address">The IP address to connect to.</param>
    public void ConnectToServer(string address)
    {
        address = address?.Trim();
        if (string.IsNullOrEmpty(address) || address.Length > 253)
        {
            Debug.LogWarning("Enter a server address to join.");
            return;
        }
        if (!NetworkClient.active)
        {
            networkAddress = address;
            StartClient();
        }
    }
}
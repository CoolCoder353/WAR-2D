using Mirror;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

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

    public GameManagerSettings settings;


    private NetworkIdentity localPlayer;

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

        // Subscribe to the sceneLoaded event
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    public override void OnDestroy()
    {
        // Unsubscribe from the sceneLoaded event
        SceneManager.sceneLoaded -= OnSceneLoaded;

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


    public override void OnStartServer()
    {
        base.OnStartServer();
        Debug.Log("Server has started");
    }

    /// <summary>
    /// Called on the server when a new player has connected.
    /// Adds the player to the list of players if they are not already in it.
    /// </summary>
    [Server]
    public override void OnServerAddPlayer(NetworkConnectionToClient conn)
    {
        base.OnServerAddPlayer(conn);

        if (GameCore.Instance == null) { Debug.LogError("GameCore is null"); }
        if (GameCore.Instance.ServerPlayers == null) { Debug.LogError("ServerPlayers is null"); }
        if (conn.identity == null) { Debug.LogError("conn.identity is null"); }
        if (settings == null) { Debug.LogError("settings is null. Did you forget to attach the settings object? (ツ)_/¯"); }

        // Add the new player to the list of players
        GameCore.Instance.ServerPlayers.Add(conn.identity, new ServerPlayer(conn, settings.initalResources));

        // Log the connection
        Debug.Log($"Player {conn.connectionId} has connected");
        Debug.Log($"There are now {GameCore.Instance.ServerPlayers.Count} players connected");

        // If this is the first player to connect, set them as the server owner
        if (GameCore.Instance.ServerPlayers.Count == 1)
        {
            GameCore.Instance.SetServerOwner(conn);
        }


    }

    /// <summary>
    /// Called on the server when a player has disconnected.
    /// Removes the player from the list of players.
    /// </summary>
    [Server]
    public override void OnServerDisconnect(NetworkConnectionToClient conn)
    {
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
    [Client]
    public override void OnClientConnect()
    {
        base.OnClientConnect();
        NetworkClient.AddPlayer();
        localPlayer = NetworkClient.localPlayer;


        //CreatePlayerMessage characterMessage = new CreatePlayerMessage();
        //NetworkClient.Send(characterMessage);
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
        if (!NetworkClient.active)
        {
            StartHost();
        }

    }

    /// <summary>
    /// Connects to a server at the specified address.
    /// </summary>
    public void ConnectToServer(string address)
    {
        if (!NetworkClient.active)
        {
            networkAddress = address;
            StartClient();
        }

    }

    public void ConnectToServerThroughUI()
    {
        if (!NetworkClient.active)
        {
            networkAddress = GameObject.FindWithTag("LobbyManager").GetComponent<LobbySystem>().joinIPInputField.text;
            StartClient();
        }
    }

    /// <summary>
    /// Connects to a server at localhost for debugging purposes.
    /// </summary>
    public void ConnectToServerDebug()
    {
        if (!NetworkClient.active)
        {
            networkAddress = "localhost";
            StartClient();
        }

    }

    /// <summary>
    /// Called when a scene is loaded.
    /// Sets up button listeners for the host, join, leave, and quit buttons.
    /// </summary>
    public void OnSceneLoaded(Scene scene, LoadSceneMode loadSceneMode)
    {
        if (Instance == null)
        {
            Instance = this;
        }

        //TODO: Add a check to see if this is the server, and not bother with this if it is.

        // If we can find network spawn spots, set client players position to one of them, in a random order
        //Note: This causes a warning because clients are not ready, this is fine as we want the bases to enter the world as the clients spawn in
        if (GameObject.FindGameObjectsWithTag("SpawnSpot").Length > 0)
        {
            List<GameObject> spawnSpots = new List<GameObject>(GameObject.FindGameObjectsWithTag("SpawnSpot"));
            int randomSpot = Random.Range(0, spawnSpots.Count);
            Vector3 spawnPosition = spawnSpots[randomSpot].transform.position;


            // Get the local player

            Debug.Log("Local player: " + localPlayer);

            if (localPlayer != null)
            {
                // Get the ClientPlayer component
                ClientPlayer clientPlayer = localPlayer.gameObject.GetComponent<ClientPlayer>();
                Debug.Log("ClientPlayer component: " + clientPlayer);

                if (clientPlayer != null)
                {
                    // Spawn the primary base

                }
            }
            // Remove the used spawn spot from the list
            spawnSpots.RemoveAt(randomSpot);
        }


        // If we can find any object with the tag 'Host'
        if (GameObject.FindWithTag("Host"))
        {
            ////Debug.Log("Found host");
            // Set the host button to be interactable
            GameObject.FindWithTag("Host").GetComponent<Button>().onClick.AddListener(HostServer);
        }
        // If we can find any object with the tag 'Join'
        if (GameObject.FindWithTag("Join"))
        {
            Debug.Log("Found join");
            // Set the join button to be interactable
            GameObject.FindWithTag("Join").GetComponent<Button>().onClick.AddListener(ConnectToServerThroughUI);
        }
        // If we can find any object with the tag 'LobbyManager'
        if (GameObject.FindWithTag("LobbyManager"))
        {
            Debug.Log("Found LobbyManager");
            // Set the join button to be interactable
            Button button = GameObject.FindWithTag("LobbyManager").GetComponent<LobbySystem>().joinIPButton.GetComponent<Button>();
            button.gameObject.transform.parent.gameObject.SetActive(true);
            button.onClick.AddListener(ConnectToServerThroughUI);
            button.gameObject.transform.parent.gameObject.SetActive(false);
        }
        // If we can find any object with the tag 'Leave'
        if (GameObject.FindWithTag("Leave"))
        {
            // Set the leave button to be interactable
            GameObject.FindWithTag("Leave").GetComponent<Button>().onClick.AddListener(LeaveLobby);
        }

        // If we can find any object with the tag 'Quit'
        if (GameObject.FindWithTag("Quit"))
        {
            // Set the quit button to be interactable
            GameObject.FindWithTag("Quit").GetComponent<Button>().onClick.AddListener(QuitGame);
        }
    }



}
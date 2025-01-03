using System;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

[System.Serializable]
public class ClientPlayer : NetworkBehaviour
{
    [SyncVar(hook = nameof(OnNicknameChangedEvent))]
    public string nickname;

    public LobbySystem lobbySystem;


    public readonly SyncList<ClientUnit> visuableUnits = new SyncList<ClientUnit>();
    public ServerData serverPlayer;


    [Client]
    public override void OnStartClient()
    {
        base.OnStartClient();

        DontDestroyOnLoad(this);
        //Find the lobby system
        lobbySystem = FindObjectOfType<LobbySystem>();
        if (lobbySystem != null) { lobbySystem.AddClientPlayer(this, addNicknameListener: ClientCanEdit(), addStartGameListener: ClientIsServerOwner()); }

        //Add the hook to the scene change event
        if (!isLocalPlayer) return;
        SceneManager.sceneLoaded += OnSceneChangedEvent;

    }

    [Client]
    public override void OnStopClient()
    {
        if (!isLocalPlayer) return;
        SceneManager.sceneLoaded -= OnSceneChangedEvent;

        //TODO: Need to make sure we remove handles when the player disconnects, or the scene changes

        // RemoveUnitHandles();

        Destroy(this.gameObject);

    }



    [TargetRpc]
    public void SetServerPlayer(NetworkConnectionToClient connection, string playerData)
    {
        serverPlayer = ServerData.Deserialize(playerData);

    }



    [Server]
    public NetworkConnectionToClient GetConnectionToClient()
    {
        return connectionToClient;
    }
    [Client]
    public NetworkConnection GetConnectionToServer()
    {
        return connectionToServer;
    }

    [ClientRpc]
    public void RPC_RemoveClientLobbyUI()
    {

        if (lobbySystem != null)
        {
            lobbySystem.CheckForLostPlayers();
        }
        else
        {
            Debug.LogError("Could not find lobby system to remove client player from.");
        }
    }

    [Server]
    public override void OnStartServer()
    {
        base.OnStartServer();
        nickname = $"Player {netId}";

        DontDestroyOnLoad(this);
    }

    [Command]
    public void CmdSetNickname(string nickname)
    {
        if (string.IsNullOrEmpty(nickname))
        {
            return;
        }

        this.nickname = nickname;

    }

    [Server]
    public bool CanEdit()
    {
        return connectionToClient.identity == NetworkClient.connection.identity;
    }

    [Server]
    public bool IsServerOwner()
    {
        return GameCore.Instance.IsServerOwner(connectionToClient);
    }

    [Client]
    public bool ClientIsServerOwner()
    {
        return isServer && isLocalPlayer;
    }

    [Client]
    public bool ClientCanEdit()
    {
        return isLocalPlayer;
    }

    [Client]
    private void OnNicknameChangedEvent(string old, string newNickname)
    {

        if (newNickname == null || newNickname == string.Empty)
        {
            ////Debug.LogWarning("Nickname is null or empty");
            return;
        }
        if (lobbySystem == null)
        {
            ////Debug.LogWarning("LobbySystem not found when trying to update nickname.");
            return;
        }
        lobbySystem.UpdateClientPlayerNickname(this, newNickname);

    }



    [Client]
    public void OnSceneChangedEvent(Scene newScene, LoadSceneMode sceneMode)
    {
        if (!isLocalPlayer) return;

        Debug.Log($"Scene changed to {newScene.name} with mode {sceneMode}");
        //Setup the hooks to the visable units
        if (serverPlayer != null && visuableUnits != null && UnitCommander.Instance != null)
        {
            // Debug.Log($"Debugging hooks state is {visuableUnits.OnChange != null}");
            // Debug.Log("Setting up unit hooks");
            // SetUnitHandles();

        }

    }

    public void SetUnitHandles()
    {

        Debug.Log("Setting up unit hooks");
        visuableUnits.OnAdd += (int index) =>
           {
               ClientUnit unit = visuableUnits[index];
               UnitCommander.Instance.UnitListInsert(index, unit);

           };
        visuableUnits.OnInsert += (int index) =>
        {
            ClientUnit unit = visuableUnits[index];
            Debug.Log("Inserting unit");
            UnitCommander.Instance.UnitListInsert(index, unit);
        };
        visuableUnits.OnSet += (int index, ClientUnit old) =>
        {
            ClientUnit unit = visuableUnits[index];
            UnitCommander.Instance.UnitListSet(index, old, unit);
        };

        visuableUnits.OnRemove += UnitCommander.Instance.UnitListRemove;

        visuableUnits.OnClear += UnitCommander.Instance.UnitListClear;

        //Register the intial state of the units
        for (int i = 0; i < visuableUnits.Count; i++)
        {
            ClientUnit unit = visuableUnits[i];
            UnitCommander.Instance.UnitListInsert(i, unit);
        }


        // //For Debugging log all changes in the visuable units
        // visuableUnits.OnChange += (SyncList<ClientUnit>.Operation operation, int index, ClientUnit unit) =>
        // {
        //     Debug.Log($"Operation: {operation} Index: {index} Unit: {unit}");
        // };

        // Debug.Log($"Debugging hooks state is {visuableUnits.OnChange != null}");
    }

    public void RemoveUnitHandles()
    {
        if (serverPlayer != null && visuableUnits != null)
        {
            Debug.Log("Removing unit hooks");
            visuableUnits.OnChange = null;
            visuableUnits.OnAdd = null;
            visuableUnits.OnInsert = null;
            visuableUnits.OnSet = null;
            visuableUnits.OnRemove = null;
            visuableUnits.OnClear = null;
        }
    }
}
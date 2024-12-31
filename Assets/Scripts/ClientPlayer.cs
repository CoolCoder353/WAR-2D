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

    public ServerData serverPlayer;

    [Client]
    public override void OnStartClient()
    {
        base.OnStartClient();
        //Find the lobby system
        lobbySystem = FindObjectOfType<LobbySystem>();

        //Add the hook to the scene change event
        SceneManager.sceneLoaded += OnSceneChangedEvent;

        if (lobbySystem != null) { lobbySystem.AddClientPlayer(this, addNicknameListener: ClientCanEdit(), addStartGameListener: ClientIsServerOwner()); }
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



    //TODO Hook this to the server changing scenes, I think there is a handle we can connect to somewhere but need to find it. 
    //TODO Try looking in the GameManger for the scene change event.
    [Client]
    public void OnSceneChangedEvent(Scene newScene, LoadSceneMode sceneMode)
    {

        Debug.Log($"Scene changed to {newScene.name} with mode {sceneMode}");
        //Setup the hooks to the visable units
        if (serverPlayer != null && serverPlayer.visuableUnits != null && UnitCommander.Instance != null)
        {
            serverPlayer.visuableUnits.OnAdd += (int index) =>
            {
                ClientUnit unit = serverPlayer.visuableUnits[index];
                UnitCommander.Instance.UnitListInsert(index, unit);

            };
            serverPlayer.visuableUnits.OnInsert += (int index) =>
            {
                ClientUnit unit = serverPlayer.visuableUnits[index];
                UnitCommander.Instance.UnitListInsert(index, unit);
            };
            serverPlayer.visuableUnits.OnSet += (int index, ClientUnit old) =>
            {
                ClientUnit unit = serverPlayer.visuableUnits[index];
                UnitCommander.Instance.UnitListSet(index, old, unit);
            };

            serverPlayer.visuableUnits.OnRemove += UnitCommander.Instance.UnitListRemove;

            serverPlayer.visuableUnits.OnClear += UnitCommander.Instance.UnitListClear;
        }
    }



}
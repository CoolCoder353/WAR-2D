using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;
/// <summary>
/// The LobbySystem class is responsible for managing the lobby UI and player interactions within the lobby.
/// It inherits from Mirror's NetworkBehaviour class.
/// </summary>
public class LobbySystem : MonoBehaviour
{
    // UI elements and prefabs used in the lobby
    public GameObject startGameButton;
    public GameObject lobbyUI;
    public GameObject lobbyPlayerUI;
    public GameObject lobbyPlayerPrefab;

    public GameObject joinIPPrefab;

    public GameObject joinIPButton;

    public TMP_InputField joinIPInputField;


    // List of ClientPlayer objects representing the players in the lobby, in order
    public Dictionary<NetworkIdentity, ClientPlayer> ClientPlayers = new Dictionary<NetworkIdentity, ClientPlayer>();


    public Dictionary<ClientPlayer, TMP_Text> playerNicknames = new Dictionary<ClientPlayer, TMP_Text>();

    public GameCore core;

    public void Awake()
    {
        startGameButton.SetActive(false);
    }

    [Client]
    public void CheckForLostPlayers()
    {
        //Go through all the client players, keeping track of the index
        int index = 0;

        List<NetworkIdentity> keysToRemove = new List<NetworkIdentity>();
        foreach (KeyValuePair<NetworkIdentity, ClientPlayer> clientPlayer in ClientPlayers)
        {
            //If the client player is null,
            if (clientPlayer.Value == null)
            {

                //Remove the client player from the all references that still exist
                keysToRemove.Add(clientPlayer.Key);
                playerNicknames.Remove(clientPlayer.Value);
                Destroy(lobbyPlayerUI.transform.GetChild(index).gameObject);
            }
            index++;
        }

        //Remove the keys from ClientPlayers
        foreach (NetworkIdentity key in keysToRemove)
        {
            ClientPlayers.Remove(key);
        }

        if (ClientPlayers.Count != playerNicknames.Count)
        {
            Debug.LogError("Client players and player nicknames are out of sync");
        }
    }

    [Client]
    public void AddClientPlayer(ClientPlayer player, bool addNicknameListener = false, bool addStartGameListener = false)
    {
        if (core == null)
        {
            core = GameCore.Instance;
        }

        ClientPlayers.Add(player.netIdentity, player);


        // Instantiate a lobby player UI element for the player
        GameObject lobbyPlayer = Instantiate(lobbyPlayerPrefab, lobbyPlayerUI.transform);
        playerNicknames.Add(player, lobbyPlayer.GetComponentInChildren<TMP_Text>());

        lobbyPlayer.GetComponentInChildren<TMP_Text>().text = player.nickname;

        TMP_InputField input_field = lobbyPlayer.GetComponentInChildren<TMP_InputField>();

        input_field.gameObject.SetActive(false);

        if (addNicknameListener)
        {
            // Add a listener to the nickname input field to update the player's nickname when it changes
            input_field.onValueChanged.AddListener((string newNickname) =>
             {
                 player.CmdSetNickname(newNickname);
             });
            input_field.text = player.nickname;
            lobbyPlayer.GetComponentInChildren<TMP_Text>().gameObject.SetActive(false);
            input_field.gameObject.SetActive(true);
        }
        if (addStartGameListener)
        {
            // Add a listener to the start game button to start the game when it is clicked
            startGameButton.GetComponent<Button>().onClick.AddListener(() =>
            {
                StartGame();
            });

            startGameButton.SetActive(true);
        }
        input_field.gameObject.SetActive(addNicknameListener);



        lobbyUI.SetActive(true);
    }


    [Client]
    public void UpdateClientPlayerNickname(ClientPlayer player, string newNickname)
    {

        if (core == null)
        {
            Debug.LogError("Core is null");
            return;
        }
        if (player == null)
        {
            Debug.LogError("Player is null");
            return;
        }
        if (playerNicknames == null)
        {
            Debug.LogError("Player nicknames list is null");
            return;
        }
        if (playerNicknames[player] == null)
        {
            Debug.LogError("Player nickname not in list");
            return;
        }
        if (newNickname == null || newNickname == string.Empty)
        {
            Debug.LogError("New nickname is null");
            return;
        }
        if (player.ClientCanEdit())
        {
            ////Debug.LogWarning("Dont set the text of yourself");
            return;
        }

        if (ClientPlayers.ContainsValue(player))
        {
            playerNicknames[player].text = newNickname;
        }
    }



    /// <summary>
    /// Leaves the lobby by calling the LeaveLobby method of the GameManager.
    /// </summary>
    public void LeaveLobby()
    {
        GameManager.Instance.LeaveLobby();
    }



    /// <summary>
    /// Starts the game by sending a command to the server to start the game.
    /// </summary>
    [Client]
    public void StartGame()
    {
        GameCore.Instance.Cmd_StartGame();
    }


}
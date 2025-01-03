using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TIM;
using UnityEngine.Events;
using Mirror;
using System.Linq;
public class Custom_Commands : MonoBehaviour
{

    public static Custom_Commands Instance = null;
    public CmdFormula helpCommand = new CmdFormula();
    public CmdFormula clientPlayerCountCommand = new CmdFormula();

    public CmdFormula serverPlayersCountCommand = new CmdFormula();

    public CmdFormula getClientPlayerCommand = new CmdFormula();

    public CmdFormula isServerOnCommand = new CmdFormula();

    public CmdFormula isClientOnCommand = new CmdFormula();

    public CmdFormula isGameCoreInstanceNullCommand = new CmdFormula();
    public CmdFormula getPlayersResources = new CmdFormula();

    public CmdFormula GetWorldStateManager = new CmdFormula();



    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(this);
        }


        Console.RegisterCommand(helpCommand, HelpCommand);
        // Console.RegisterCommand(clientPlayerCountCommand, ClientPlayerCountCommand);
        Console.RegisterCommand(serverPlayersCountCommand, ServerPlayerCountCommand);
        //  Console.RegisterCommand(getClientPlayerCommand, GetClientPlayerCommand);
        Console.RegisterCommand(isServerOnCommand, IsServerOnCommand);
        Console.RegisterCommand(isClientOnCommand, IsClientOnCommand);
        Console.RegisterCommand(isGameCoreInstanceNullCommand, IsGameCoreInstanceNullCommand);
        Console.RegisterCommand(getPlayersResources, GetPlayersResources);
        Console.RegisterCommand(GetWorldStateManager, GetWorldStateManagerCommand);

    }

    private void GetWorldStateManagerCommand(CmdInputResult arg0)
    {
        Console.Log(WorldStateManager.Instance.ToString());
    }

    public void HelpCommand(CmdInputResult result)
    {
        Console.Log("Available commands:");
        foreach (var item in Console.RegisteredCommands)
        {
            Console.Log(((CmdFormula)item.Key).Preview);
        }
    }

    [Server]
    public void GetPlayersResources(CmdInputResult result)
    {
        foreach (var player in GameCore.Instance.ServerPlayers)
        {
            Console.Log(player.Value.connection.identity.GetComponent<NetworkIdentity>().netId + " " + player.Value.data.resources);
        }
    }

    // public void ClientPlayerCountCommand(CmdInputResult result)
    // {
    //     Console.Log("Client player count: " + GameCore.Instance.ClientPlayers.Count, MessageType.Network);
    // }

    public void ServerPlayerCountCommand(CmdInputResult result)
    {
        Console.Log("Server player count: " + GameCore.Instance.ServerPlayers.Count, MessageType.Network);
    }

    // public void GetClientPlayerCommand(CmdInputResult result)
    // {
    //     if (result.Parts.Count == 0)
    //     {
    //         Console.Log("Usage: getClientPlayer <index>", MessageType.Error);
    //         return;
    //     }

    //     int index = result.Parts[1].Integer;


    //     if (index >= GameCore.Instance.ClientPlayers.Count)
    //     {
    //         Console.Log("Index out of range.", MessageType.Error);
    //         return;
    //     }

    //     Console.Log("Client player at index " + index + ": " + GameCore.Instance.ClientPlayers.ToList()[index].Value.nickname, MessageType.Network);
    // }

    public void IsServerOnCommand(CmdInputResult result)
    {
        Console.Log("Server is on: " + NetworkServer.active, MessageType.Network);
    }

    public void IsClientOnCommand(CmdInputResult result)
    {
        Console.Log("Client is on: " + NetworkClient.active, MessageType.Network);
    }

    public void IsGameCoreInstanceNullCommand(CmdInputResult result)
    {
        Console.Log("GameCore.Instance is null: " + (GameCore.Instance == null), MessageType.Network);
    }
}

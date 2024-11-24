using System.Collections.Generic;
using UnityEngine;
using Mirror;
using Newtonsoft.Json;

public class ServerPlayer
{
    public NetworkConnectionToClient connection;

    public ServerData data
    { get; protected set; } = new();
    public ServerPlayer(NetworkConnectionToClient connection, float resources)
    {
        this.connection = connection;
        this.data.resources = resources;
    }

    public void AddResources(float amount)
    {
        data.resources += amount;
    }

    public void RemoveResources(float amount)
    {
        Debug.Log($"Removing {amount} resources from player {connection.identity.netId}");
        data.resources -= amount;
    }

}

[System.Serializable]
public class ServerData
{
    public float resources = 0;

    public static string Serialize(ServerData data)
    {
        return JsonConvert.SerializeObject(data);
    }
    public string Serialize()
    {
        return JsonConvert.SerializeObject(this);
    }

    public static ServerData Deserialize(string data)
    {
        return JsonConvert.DeserializeObject<ServerData>(data);
    }
}
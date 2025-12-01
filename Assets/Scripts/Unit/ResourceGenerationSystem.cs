using Unity.Entities;
using Unity.Burst;
using Mirror;
using UnityEngine;

[BurstCompile]
public partial struct ResourceGenerationSystem : ISystem
{
    private float timeSinceLastUpdate;

    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        if (GameCore.Instance == null) return;

        float deltaTime = SystemAPI.Time.DeltaTime;
        timeSinceLastUpdate += deltaTime;

        // Update every 0.1 seconds to reduce network traffic
        if (timeSinceLastUpdate < 0.1f) return;

        ResourceConfigData config = ResourceConfigLoader.LoadConfig();
        float resourcesThisUpdate = config.passiveGenerationRate * timeSinceLastUpdate;

        // Add passive resources to all players
        foreach (var serverPlayer in GameCore.Instance.serverPlayers)
        {
            if (serverPlayer != null)
            {
                serverPlayer.AddResources(resourcesThisUpdate);
                
                // Update client display
                if (serverPlayer.connection != null && serverPlayer.connection.identity != null)
                {
                    ClientPlayer clientPlayer = serverPlayer.connection.identity.GetComponent<ClientPlayer>();
                    if (clientPlayer != null)
                    {
                        clientPlayer.TargetUpdateResources(serverPlayer.connection, serverPlayer.data.resources);
                    }
                }
            }
        }

        timeSinceLastUpdate = 0f;
    }
}

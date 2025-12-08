using Unity.Entities;
using Unity.Burst;
using Unity.Transforms;
using Unity.Mathematics;
using Mirror;
using UnityEngine;
using Config;

/// <summary>
/// System responsible for managing resource generation, mining, and consumption.
/// </summary>
[BurstCompile]
public partial struct ResourceSystem : ISystem
{
    private float timeSinceLastPassiveGeneration;

    /// <summary>
    /// Called every frame on the server to handle resource logic.
    /// </summary>
    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        if (GameCore.Instance == null || WorldStateManager.Instance == null) return;

        if (GameCore.Instance.CurrentState != GameState.Playing) return;

        float deltaTime = SystemAPI.Time.DeltaTime;
        GameConfigData config = ConfigLoader.LoadConfig();

        // 1. Passive Resource Generation
        HandlePassiveGeneration(ref state, deltaTime, config);

        // 2. Mining Logic
        HandleMining(ref state, deltaTime, config);

        // 3. Resource Consumption (Units & Buildings)
        HandleConsumption(ref state, deltaTime, config);
    }

    /// <summary>
    /// Handles passive resource generation for all players.
    /// </summary>
    private void HandlePassiveGeneration(ref SystemState state, float deltaTime, GameConfigData config)
    {
        timeSinceLastPassiveGeneration += deltaTime;

        // Update every 0.1 seconds to reduce network traffic
        if (timeSinceLastPassiveGeneration < 0.1f) return;

        float resourcesThisUpdate = config.Resources.PassiveGenerationRate * timeSinceLastPassiveGeneration;

        foreach (var serverPlayer in GameCore.Instance.serverPlayers)
        {
            if (serverPlayer != null)
            {
                serverPlayer.AddResources(resourcesThisUpdate);
                UpdateClientDisplay(serverPlayer);
            }
        }

        timeSinceLastPassiveGeneration = 0f;
    }

    /// <summary>
    /// Handles mining logic for miners.
    /// Checks if miners are facing gems and adds resources if they are.
    /// </summary>
    private void HandleMining(ref SystemState state, float deltaTime, GameConfigData config)
    {
        foreach (var (miningComponent, buildingData, transform) in 
                 SystemAPI.Query<RefRW<MiningComponent>, RefRO<BuildingData>, RefRO<LocalTransform>>())
        {
            miningComponent.ValueRW.timeSinceLastMining += deltaTime;

            // Mine resources every second
            if (miningComponent.ValueRO.timeSinceLastMining >= 1.0f)
            {
                // Calculate direction based on rotation
                quaternion rotation = transform.ValueRO.Rotation;
                float zRotationRadians = math.atan2(
                    2.0f * (rotation.value.w * rotation.value.z + rotation.value.x * rotation.value.y),
                    1.0f - 2.0f * (rotation.value.y * rotation.value.y + rotation.value.z * rotation.value.z)
                );
                
                float zRotation = math.degrees(zRotationRadians);
                zRotation = (zRotation % 360 + 360) % 360;
                int rotationIndex = Mathf.RoundToInt(zRotation / 90f) % 4;
                
                int2 direction = rotationIndex switch
                {
                    0 => new int2(1, 0),   // 0° - Right
                    1 => new int2(0, 1),   // 90° - Up
                    2 => new int2(-1, 0),  // 180° - Left
                    3 => new int2(0, -1),  // 270° - Down
                    _ => new int2(1, 0)
                };

                int2 buildingPos = new int2((int)math.floor(transform.ValueRO.Position.x), 
                                           (int)math.floor(transform.ValueRO.Position.y));
                int2 checkPos = buildingPos + direction;

                TileNode tile = WorldStateManager.Instance.GetTile(checkPos);
                bool isGemTile = tile.tileType == TileType.Gem;

                miningComponent.ValueRW.isActive = isGemTile;

                if (isGemTile)
                {
                    float resourcesToAdd = config.Resources.MiningRate * miningComponent.ValueRO.timeSinceLastMining;
                    
                    ServerPlayer owner = GameCore.Instance.GetServerPlayerById(buildingData.ValueRO.ownerId);
                    if (owner != null)
                    {
                        owner.AddResources(resourcesToAdd);
                        UpdateClientDisplay(owner);
                    }
                }

                miningComponent.ValueRW.timeSinceLastMining = 0f;
            }
        }
    }

    /// <summary>
    /// Handles resource consumption for units and buildings.
    /// Deducts running costs from the owner's resources.
    /// </summary>
    private void HandleConsumption(ref SystemState state, float deltaTime, GameConfigData config)
    {
        // Unit Consumption
        foreach (var (resourceCost, clientUnit) in SystemAPI.Query<RefRW<ResourceCostComponent>, RefRO<ClientUnit>>())
        {
            resourceCost.ValueRW.timeSinceLastCost += deltaTime;

            if (resourceCost.ValueRO.timeSinceLastCost >= 1.0f)
            {
                float costToDeduct = resourceCost.ValueRO.runningCostPerSecond * resourceCost.ValueRO.timeSinceLastCost;
                ProcessDeduction(clientUnit.ValueRO.ownerId, costToDeduct);
                resourceCost.ValueRW.timeSinceLastCost = 0f;
            }
        }

        // Building Consumption
        foreach (var (buildingResource, buildingData) in SystemAPI.Query<RefRW<BuildingResourceComponent>, RefRO<BuildingData>>())
        {
            buildingResource.ValueRW.timeSinceLastCost += deltaTime;

            if (buildingResource.ValueRO.timeSinceLastCost >= 1.0f)
            {
                float costToDeduct = buildingResource.ValueRO.runningCostPerSecond * buildingResource.ValueRO.timeSinceLastCost;
                ProcessDeduction(buildingData.ValueRO.ownerId, costToDeduct);
                buildingResource.ValueRW.timeSinceLastCost = 0f;
            }
        }
    }

    /// <summary>
    /// Helper to deduct resources from a player.
    /// </summary>
    private void ProcessDeduction(int ownerId, float amount)
    {
        ServerPlayer owner = GameCore.Instance.GetServerPlayerById(ownerId);
        if (owner != null)
        {
            if (owner.data.resources >= amount)
            {
                owner.RemoveResources(amount);
                UpdateClientDisplay(owner);
            }
        }
    }

    /// <summary>
    /// Helper to update the client's resource display.
    /// </summary>
    private void UpdateClientDisplay(ServerPlayer player)
    {
        if (player.connection != null && player.connection.identity != null)
        {
            ClientPlayer clientPlayer = player.connection.identity.GetComponent<ClientPlayer>();
            if (clientPlayer != null)
            {
                clientPlayer.TargetUpdateResources(player.connection, player.data.resources);
            }
        }
    }
}

using Unity.Entities;
using Unity.Burst;
using Mirror;
using UnityEngine;
using Unity.Collections;

[BurstCompile]
public partial struct ResourceConsumptionSystem : ISystem
{
    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        if (GameCore.Instance == null) return;

        float deltaTime = SystemAPI.Time.DeltaTime;

        // Process unit resource consumption
        foreach (var (resourceCost, clientUnit) in SystemAPI.Query<RefRW<ResourceCostComponent>, RefRO<ClientUnit>>())
        {
            resourceCost.ValueRW.timeSinceLastCost += deltaTime;

            // Deduct resources every second
            if (resourceCost.ValueRO.timeSinceLastCost >= 1.0f)
            {
                float costToDeduct = resourceCost.ValueRO.runningCostPerSecond * resourceCost.ValueRO.timeSinceLastCost;
                
                // Find the owner's ServerPlayer
                ServerPlayer owner = GameCore.Instance.GetServerPlayerById(clientUnit.ValueRO.ownerId);
                if (owner != null)
                {
                    if (owner.data.resources >= costToDeduct)
                    {
                        owner.RemoveResources(costToDeduct);
                        
                        // Update client display
                        if (owner.connection != null && owner.connection.identity != null)
                        {
                            ClientPlayer clientPlayer = owner.connection.identity.GetComponent<ClientPlayer>();
                            if (clientPlayer != null)
                            {
                                clientPlayer.TargetUpdateResources(owner.connection, owner.data.resources);
                            }
                        }
                    }
                    else
                    {
                        // Insufficient resources - silently continue
                    }
                }

                resourceCost.ValueRW.timeSinceLastCost = 0f;
            }
        }

        // Process building resource consumption
        foreach (var (buildingResource, buildingData) in SystemAPI.Query<RefRW<BuildingResourceComponent>, RefRO<BuildingData>>())
        {
            buildingResource.ValueRW.timeSinceLastCost += deltaTime;

            // Deduct resources every second
            if (buildingResource.ValueRO.timeSinceLastCost >= 1.0f)
            {
                float costToDeduct = buildingResource.ValueRO.runningCostPerSecond * buildingResource.ValueRO.timeSinceLastCost;
                
                // Find the owner's ServerPlayer
                ServerPlayer owner = GameCore.Instance.GetServerPlayerById(buildingData.ValueRO.ownerId);
                if (owner != null)
                {
                    if (owner.data.resources >= costToDeduct)
                    {
                        owner.RemoveResources(costToDeduct);
                        
                        // Update client display
                        if (owner.connection != null && owner.connection.identity != null)
                        {
                            ClientPlayer clientPlayer = owner.connection.identity.GetComponent<ClientPlayer>();
                            if (clientPlayer != null)
                            {
                                clientPlayer.TargetUpdateResources(owner.connection, owner.data.resources);
                            }
                        }
                    }
                    else
                    {
                        // Insufficient resources - silently continue
                    }
                }

                buildingResource.ValueRW.timeSinceLastCost = 0f;
            }
        }
    }
}

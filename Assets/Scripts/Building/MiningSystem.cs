using Unity.Entities;
using Unity.Burst;
using Unity.Transforms;
using Unity.Mathematics;
using Mirror;
using UnityEngine;

[BurstCompile]
public partial struct MiningSystem : ISystem
{
    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        if (GameCore.Instance == null || WorldStateManager.Instance == null) return;

        float deltaTime = SystemAPI.Time.DeltaTime;
        ResourceConfigData config = ResourceConfigLoader.LoadConfig();

        foreach (var (miningComponent, buildingData, transform) in 
                 SystemAPI.Query<RefRW<MiningComponent>, RefRO<BuildingData>, RefRO<LocalTransform>>())
        {
            miningComponent.ValueRW.timeSinceLastMining += deltaTime;

            // Mine resources every second
            if (miningComponent.ValueRO.timeSinceLastMining >= 1.0f)
            {
                // Calculate direction based on rotation (90-degree increments)
                // Extract Z rotation from quaternion
                quaternion rotation = transform.ValueRO.Rotation;
                
                // Convert quaternion to euler angles manually
                // For 2D rotation around Z axis: atan2(2(w*z + x*y), 1 - 2(y^2 + z^2))
                float zRotationRadians = math.atan2(
                    2.0f * (rotation.value.w * rotation.value.z + rotation.value.x * rotation.value.y),
                    1.0f - 2.0f * (rotation.value.y * rotation.value.y + rotation.value.z * rotation.value.z)
                );
                
                float zRotation = math.degrees(zRotationRadians);
                
                // Normalize to 0-360 range
                zRotation = (zRotation % 360 + 360) % 360;
                
                // Round to nearest 90 degrees
                int rotationIndex = Mathf.RoundToInt(zRotation / 90f) % 4;
                
                int2 direction = rotationIndex switch
                {
                    0 => new int2(1, 0),   // 0° - Right
                    1 => new int2(0, 1),   // 90° - Up
                    2 => new int2(-1, 0),  // 180° - Left
                    3 => new int2(0, -1),  // 270° - Down
                    _ => new int2(1, 0)
                };

                // Calculate tile position to check
                int2 buildingPos = new int2((int)math.floor(transform.ValueRO.Position.x), 
                                           (int)math.floor(transform.ValueRO.Position.y));
                int2 checkPos = buildingPos + direction;

                // Check if the tile is a gem tile using TileType enum
                TileNode tile = WorldStateManager.Instance.GetTile(checkPos);
                bool isGemTile = tile.tileType == TileType.Gem;

                miningComponent.ValueRW.isActive = isGemTile;

                if (isGemTile)
                {
                    float resourcesToAdd = miningComponent.ValueRO.miningRate * miningComponent.ValueRO.timeSinceLastMining;
                    
                    // Find the owner's ServerPlayer
                    ServerPlayer owner = GameCore.Instance.GetServerPlayerById(buildingData.ValueRO.ownerId);
                    if (owner != null)
                    {
                        owner.AddResources(resourcesToAdd);
                        
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
                }

                miningComponent.ValueRW.timeSinceLastMining = 0f;
            }
        }
    }
}

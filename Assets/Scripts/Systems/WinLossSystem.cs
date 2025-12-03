using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using Mirror;
using System.Linq;

[BurstCompile]
public partial struct WinLossSystem : ISystem
{
    private float checkTimer;

    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        if (GameCore.Instance == null) return;

        // Run check every 1 second to avoid overhead
        checkTimer += SystemAPI.Time.DeltaTime;
        if (checkTimer < 1.0f) return;
        checkTimer = 0f;

        // Only run if game is in Playing state
        if (GameCore.Instance.CurrentState != GameState.Playing) return;

        int playersWithHQ = 0;
        int lastPlayerWithHQ = -1;

        // Iterate through all connected players
        foreach (var kvp in GameCore.Instance.ServerPlayers)
        {
            ServerPlayer player = kvp.Value;
            int playerId = BuildingData.UIntToInt(player.connection.identity.netId);
            
            // Check if this player has an HQ
            bool hasHQ = false;
            foreach (var (hq, buildingData) in SystemAPI.Query<RefRO<HQComponent>, RefRO<BuildingData>>())
            {
                if (buildingData.ValueRO.ownerId == playerId)
                {
                    hasHQ = true;
                    break;
                }
            }

            if (hasHQ)
            {
                playersWithHQ++;
                lastPlayerWithHQ = playerId;
            }
            else
            {
                // Player has lost
                if (player.state != PlayerState.Eliminated)
                {
                    GameCore.Instance.EliminatePlayer(player.connection);
                }
            }
        }

        // Check for Win Condition
        // If only 1 player has HQ and there was more than 1 player initially (or just 1 player testing)
        // For multiplayer, we usually wait for >1 player to start.
        // If we want to support single player testing, we might need a flag.
        // Assuming >1 player game for now, or 1 player sandbox.
        
        if (playersWithHQ == 1 && GameCore.Instance.ServerPlayers.Count > 1)
        {
            // The last player wins
             ServerPlayer winner = GameCore.Instance.GetServerPlayerById(lastPlayerWithHQ);
             if (winner != null)
             {
                 GameCore.Instance.DeclareWinner(winner.connection);
             }
        }
        else if (playersWithHQ == 0 && GameCore.Instance.ServerPlayers.Count > 0)
        {
             // Draw? Or everyone lost?
             // GameCore.Instance.EndGameDraw();
        }
    }
}

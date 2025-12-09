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

        // Only run if game is in Playing state
        if (GameCore.Instance.CurrentState != GameState.Playing) return;

        // Run check every 1 second to avoid overhead
        checkTimer += SystemAPI.Time.DeltaTime;
        if (checkTimer < 1.0f) return;
        checkTimer = 0f;

        int playersWithHQ = 0;
        int lastPlayerWithHQ = -1;

        // Iterate through all connected players
        foreach (var kvp in GameCore.Instance.ServerPlayers)
        {
            ServerPlayer player = kvp.Value;
            int playerId = (int)kvp.Key.netId;

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
                    Debug.Log($"Player {playerId} eliminated - HQ destroyed!");
                    GameCore.Instance.EliminatePlayer(player.connection);
                }
            }
        }

        // Check for Win Condition
        // If only 1 player has HQ and there was more than 1 player initially
        if (playersWithHQ == 1 && GameCore.Instance.ServerPlayers.Count > 1)
        {
            // Find the winning player
            ServerPlayer winner = null;
            foreach (var kvp in GameCore.Instance.ServerPlayers)
            {
                if ((int)kvp.Key.netId == lastPlayerWithHQ)
                {
                    winner = kvp.Value;
                    break;
                }
            }

            if (winner != null)
            {
                Debug.Log($"Player {lastPlayerWithHQ} won the game!");
                GameCore.Instance.DeclareWinner(winner.connection);
            }
        }
        else if (playersWithHQ == 0 && GameCore.Instance.ServerPlayers.Count > 0)
        {
            // Draw - everyone lost
            Debug.Log("Draw - all players eliminated!");
            GameCore.Instance.EndGameDraw();
        }
    }
}


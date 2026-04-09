using Unity.Entities;
using Unity.Burst;
using UnityEngine;
using Mirror;


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

        if (GameCore.Instance.ServerPlayers.Count == 0)
        {
            // No players connected, end game as draw
            Debug.LogWarning("No players connected, this should not happen.");

            return;
        }


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
                    GameCore.Instance.EliminatePlayer(playerId);
                }
            }
        }

        // Check for Win Condition
        // If only 1 player has HQ and there was more than 1 player initially
        if (playersWithHQ == 1 && GameCore.Instance.ServerPlayers.Count > 1)
        {
            // Find the winning player
            int winner = -1;
            foreach (var kvp in GameCore.Instance.ServerPlayers)
            {
                if ((int)kvp.Key.netId == lastPlayerWithHQ)
                {
                    winner = (int)kvp.Key.netId;
                    break;
                }
            }

            if (winner != -1)
            {
                Debug.Log($"Player {lastPlayerWithHQ} won the game!");
                Debug.Log($"Calling DeclareWinner for player {lastPlayerWithHQ}, connection: {winner}");
                GameCore.Instance.DeclareWinner(winner);
            }
        }
        else if (playersWithHQ == 0 && GameCore.Instance.ServerPlayers.Count > 0)
        {
            // Draw - everyone lost
            Debug.Log("Draw - all players eliminated!");
            GameCore.Instance.DeclareDraw();
        }
    }
}


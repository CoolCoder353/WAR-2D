using Steamworks;
using UnityEngine;

namespace WAR2D.Steam
{
    /// <summary>Runs Steam's callbacks every frame and shuts the API down on quit.</summary>
    internal sealed class SteamPump : MonoBehaviour
    {
        private void Update() => SteamAPI.RunCallbacks();
        private void OnApplicationQuit() => SteamAPI.Shutdown();
    }
}

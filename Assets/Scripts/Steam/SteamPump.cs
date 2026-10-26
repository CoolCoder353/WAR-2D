using Steamworks;
using UnityEngine;

namespace WAR2D.Steam
{
    /// <summary>Runs Steam's callbacks every frame and shuts the API down on quit.</summary>
    internal sealed class SteamPump : MonoBehaviour
    {
        private void Update()
        {
            SteamAPI.RunCallbacks();
            (WAR2D.Net.Steam.SteamServices.Backend as SteamworksBackend)?.Tick();
        }
        private void OnApplicationQuit() => SteamAPI.Shutdown();
    }
}

using Steamworks;
using UnityEngine;
using WAR2D.Net.Steam;

namespace WAR2D.Steam
{
    internal static class SteamBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Init()
        {
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-noSteam") >= 0) return;
            bool ok;
            try { ok = Packsize.Test() && DllCheck.Test() && SteamAPI.Init(); }
            catch (System.DllNotFoundException e) { Debug.LogWarning($"[Steam] native library missing: {e.Message}"); ok = false; }
            if (!ok) { Debug.Log("[Steam] not running; Steam features are off."); return; }
            SteamServices.Backend = new SteamworksBackend(true);
            var go = new GameObject("SteamPump") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<SteamPump>();
            if (string.IsNullOrWhiteSpace(WAR2D.UI.MenuModel.Nickname)) WAR2D.UI.MenuModel.Nickname = SteamServices.Backend.LocalName;
            Debug.Log($"[Steam] signed in as {SteamServices.Backend.LocalName}");
        }
    }
}

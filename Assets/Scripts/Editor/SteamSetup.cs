using System.Collections.Generic;
using Mirror;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace WAR2D.EditorTools
{
    /// <summary>One-click Steam setup: the WAR2D_STEAM define for the standalone targets, and the Steam transport in the menu scene.</summary>
    public static class SteamSetup
    {
        private const string Define = "WAR2D_STEAM";
        private const string MenuScene = "Assets/Main_Menu.unity";

        [MenuItem("WAR-2D/Steam/Enable WAR2D_STEAM define")]
        public static void EnableDefine()
        {
            NamedBuildTarget t = NamedBuildTarget.Standalone;
            PlayerSettings.GetScriptingDefineSymbols(t, out string[] symbols);
            var list = new List<string>(symbols);
            if (list.Contains(Define)) return;
            list.Add(Define);
            PlayerSettings.SetScriptingDefineSymbols(t, list.ToArray());
        }

        [MenuItem("WAR-2D/Steam/Add FizzySteamworks to Main_Menu")]
        public static void AddTransport()
        {
            var scene = EditorSceneManager.OpenScene(MenuScene, OpenSceneMode.Single);
            var manager = Object.FindFirstObjectByType<GameManager>();
            if (manager == null) { Debug.LogError("No GameManager in Main_Menu."); return; }
            var multiplex = manager.GetComponent<MultiplexTransport>();
            if (multiplex == null) { Debug.LogError("GameManager has no MultiplexTransport."); return; }
            var fizzy = manager.GetComponent<Mirror.FizzySteam.FizzySteamworks>();
            if (fizzy == null) fizzy = manager.gameObject.AddComponent<Mirror.FizzySteam.FizzySteamworks>();

            var transports = new List<Transport>(multiplex.transports ?? new Transport[0]);
            if (!transports.Contains(fizzy)) transports.Add(fizzy);
            multiplex.transports = transports.ToArray();
            EditorUtility.SetDirty(multiplex);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"FizzySteamworks added to the multiplexer ({transports.Count} transports).");
        }

        [MenuItem("WAR-2D/Steam/Add authenticator to Main_Menu")]
        public static void AddAuthenticator()
        {
            var scene = EditorSceneManager.OpenScene(MenuScene, OpenSceneMode.Single);
            var manager = Object.FindFirstObjectByType<GameManager>();
            if (manager == null) { Debug.LogError("No GameManager in Main_Menu."); return; }
            var auth = manager.GetComponent<WAR2D.Net.Steam.SteamAuthenticator>();
            if (auth == null) auth = manager.gameObject.AddComponent<WAR2D.Net.Steam.SteamAuthenticator>();
            manager.authenticator = auth;
            EditorUtility.SetDirty(manager);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("SteamAuthenticator assigned to GameManager.");
        }
    }
}

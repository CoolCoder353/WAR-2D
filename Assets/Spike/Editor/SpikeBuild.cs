using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WAR2D.Spike.Editor
{
    /// <summary>Creates the empty spike scene and builds a non-development Linux player containing only it.</summary>
    public static class SpikeBuild
    {
        public const string ScenePath = "Assets/Spike/ScaleSpike.unity";
        public const string PlayerPath = "Builds/Spike/WAR2D-Spike.x86_64";

        [MenuItem("Spike/Create Scene")]
        public static void CreateScene()
        {
            if (File.Exists(ScenePath)) return;
            if (!SaveDirtyScenes()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        [MenuItem("Spike/Build Linux Player")]
        public static void BuildLinux()
        {
            CreateScene();
            if (!File.Exists(ScenePath))
            {
                Debug.LogError($"[Spike] Build aborted: {ScenePath} does not exist.");
                return;
            }
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = PlayerPath,
                target = BuildTarget.StandaloneLinux64,
                options = BuildOptions.None,
            });
            Debug.Log($"[Spike] Build {report.summary.result}, {report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalTime}");
        }

        /// <summary>
        /// Saves every dirty open scene, so <see cref="EditorSceneManager.NewScene"/> cannot raise
        /// the modal save prompt that would hang a menu item invoked from the CLI. A dirty untitled
        /// scene cannot be saved without a dialog, so the caller is refused instead.
        /// </summary>
        private static bool SaveDirtyScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isDirty) continue;
                if (string.IsNullOrEmpty(scene.path))
                {
                    Debug.LogError($"[Spike] Refusing to create the spike scene: '{scene.name}' has unsaved changes and no path. Save it first.");
                    return false;
                }
                if (!EditorSceneManager.SaveScene(scene))
                {
                    Debug.LogError($"[Spike] Refusing to create the spike scene: could not save '{scene.path}'.");
                    return false;
                }
            }
            return true;
        }
    }
}

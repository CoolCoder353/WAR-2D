using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace WAR2D.EditorTools
{
    /// <summary>Release builds for the Steam depots.</summary>
    public static class BuildCommands
    {
        [MenuItem("WAR-2D/Build/Linux (Steam)")] public static void Linux() => Build(BuildTarget.StandaloneLinux64, "Builds/Steam/linux/WAR-2D.x86_64");
        [MenuItem("WAR-2D/Build/Windows (Steam)")] public static void Windows() => Build(BuildTarget.StandaloneWindows64, "Builds/Steam/windows/WAR-2D.exe");

        private static void Build(BuildTarget target, string path)
        {
            var scenes = new List<string>();
            foreach (var s in EditorBuildSettings.scenes) if (s.enabled) scenes.Add(s.path);
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes.ToArray(), locationPathName = path, target = target, options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded) throw new System.Exception($"Build failed: {report.summary.result}");
            UnityEngine.Debug.Log($"Built {target} to {path} ({report.summary.totalSize / 1048576} MB)");
        }
    }
}

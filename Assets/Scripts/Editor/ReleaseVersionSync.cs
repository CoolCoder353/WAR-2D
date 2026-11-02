using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// Every build takes its version (<c>bundleVersion</c>, which Steam joins compare) from <c>&lt;Release&gt;&lt;Version&gt;</c> in
    /// GameConfig.xml (2026-11-02), so the main menu's line and the build can't disagree.
    /// </summary>
    public sealed class ReleaseVersionSync : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
            if (!Config.ConfigLoader.IsValid || string.IsNullOrEmpty(config.Release.Version))
                throw new BuildFailedException("GameConfig.xml is invalid: the build's version comes from <Release><Version>.");
            if (PlayerSettings.bundleVersion == config.Release.Version) return;
            UnityEngine.Debug.Log($"Build version {PlayerSettings.bundleVersion} -> {config.Release.Version} (from <Release> in GameConfig.xml)");
            PlayerSettings.bundleVersion = config.Release.Version;
        }
    }
}

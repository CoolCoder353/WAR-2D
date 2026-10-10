using UnityEngine;

/// <summary>
/// Debug UI is for the editor and development builds only: in a release build the TIM developer console
/// (which spawns itself from vendored code) is removed as soon as it appears, so its toggle key does
/// nothing. <see cref="ForceReleaseForTests"/> lets a test check the release behaviour in the editor.
/// </summary>
public static class DevUi
{
    /// <summary>Test seam: behave as a release build.</summary>
    internal static bool ForceReleaseForTests;

    /// <summary>True in the editor and development builds.</summary>
    public static bool Enabled => !ForceReleaseForTests && Debug.isDebugBuild;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (Enabled) return;
        var gate = new GameObject("DevUiGate");
        Object.DontDestroyOnLoad(gate);
        gate.AddComponent<Gate>();
    }

    /// <summary>True while the developer console exists.</summary>
    public static bool ConsoleExists => TIM.Console.Instance != null;

    /// <summary>Removes the debug UI when it isn't allowed.</summary>
    public static void Apply()
    {
        if (Enabled) return;
        TIM.Console console = TIM.Console.Instance;
        if (console != null) Object.Destroy(console.gameObject);
    }

    /// <summary>Runs <see cref="Apply"/> once every load-time spawner has run.</summary>
    private sealed class Gate : MonoBehaviour
    {
        private void Start()
        {
            Apply();
            Destroy(gameObject);
        }
    }
}

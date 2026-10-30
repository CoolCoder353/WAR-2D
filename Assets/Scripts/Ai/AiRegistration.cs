using UnityEngine;

namespace WAR2D.Ai
{
    /// <summary>
    /// Registers the AI with the game at load (v0.14): the game assembly can't name this one (it references the game),
    /// so it makes brains and the director through <see cref="AiBrains"/>.
    /// </summary>
    internal static class AiRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Register()
        {
            AiBrains.Factory = AiBrain.For;
            AiBrains.Director = () => new AiDirector();
        }

#if UNITY_EDITOR
        // EditMode (tests, the editor's own server) never runs RuntimeInitializeOnLoadMethod.
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterInEditor() => Register();
#endif
    }
}

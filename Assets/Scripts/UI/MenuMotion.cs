using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Menu motion (v0.16, Figma M1): a screen or overlay arriving gets an "enter" class (offset and transparent) for one
    /// frame, and the USS transitions take it to its resting place (150–250 ms). The screen leaving hides at once, so motion
    /// never delays a click. Settings → Graphics → Reduce motion puts <c>reduce-motion</c> on the root, which zeroes the
    /// transitions.
    /// </summary>
    public static class MenuMotion
    {
        /// <summary>The root class that turns every menu transition into an instant swap.</summary>
        public const string ReduceMotionClass = "reduce-motion";

        /// <summary>Adds <paramref name="enterClass"/> now and removes it on the next frame, so the element animates in.</summary>
        public static void Enter(VisualElement element, string enterClass)
        {
            if (element == null) return;
            element.AddToClassList(enterClass);
            element.schedule.Execute(() => element.RemoveFromClassList(enterClass)).ExecuteLater(16);
        }

        /// <summary>Puts <see cref="ReduceMotionClass"/> on the root while the setting is on.</summary>
        public static void ApplySetting(VisualElement root) => root?.EnableInClassList(ReduceMotionClass, SettingsStore.Current.ReduceMotion);
    }
}

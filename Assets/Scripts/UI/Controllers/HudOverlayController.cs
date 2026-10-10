using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// The HQ placement prompt and the countdown (Figma: HUD / HQ placement, HUD / Countdown), shown by
    /// game state from public data only: the state, each player's <c>hasPlacedHQ</c> and the countdown end.
    /// </summary>
    public sealed class HudOverlayController
    {
        private readonly VisualElement prompt, countdown;
        private readonly Label progress, seconds;

        public HudOverlayController(VisualElement root)
        {
            prompt = root.Q("hq-prompt");
            countdown = root.Q("countdown");
            progress = root.Q<Label>("hq-progress");
            seconds = root.Q<Label>("countdown-value");
        }

        /// <summary>Shows the overlay for the state: the prompt while placing HQs, the number during the countdown.</summary>
        public void Show(GameState state, int placed, int players, double secondsLeft)
        {
            prompt.EnableInClassList("hud-overlay--visible", state == GameState.PlacingHQ);
            countdown.EnableInClassList("hud-overlay--visible", state == GameState.Countdown);
            if (state == GameState.PlacingHQ) progress.text = $"{placed} of {players} players placed";
            if (state == GameState.Countdown) seconds.text = Math.Max(0, (int)Math.Ceiling(secondsLeft)).ToString();
        }
    }
}

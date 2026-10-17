using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>What the HQ placement prompt shows; built by <see cref="HudController"/> from the local player's own data.</summary>
    public struct PlacementPrompt
    {
        /// <summary>Players in the match, and how many have placed their HQ.</summary>
        public int Players, Placed;
        /// <summary>The local player's dealt options (0 until the deal arrives).</summary>
        public int Options;
        /// <summary>The option on screen (index into the local player's options), or -1.</summary>
        public int Focused;
        /// <summary>The host's pick time setting (0 random, &gt; 0 seconds, <see cref="StartPickRules.Unlimited"/>).</summary>
        public int PickSeconds;
        /// <summary>Seconds until the pick time ends (timed picks only).</summary>
        public double SecondsLeft;
        /// <summary>True when the local player has teammates whose options are ringed.</summary>
        public bool HasTeammates;
        /// <summary>Names of teammates who have picked their start.</summary>
        public IReadOnlyList<string> TeammatesPicked;
    }

    /// <summary>
    /// The HQ placement prompt and the countdown (Figma: HUD / HQ placement – timed pick, Unlimited, teammate's options;
    /// HUD / Countdown), shown by game state from public data and the local player's own deal only.
    /// </summary>
    public sealed class HudOverlayController
    {
        private readonly VisualElement prompt, countdown, optionButtons;
        private readonly Label title, body, progress, seconds;
        private int shownOptions = -1, shownFocus = -2;

        /// <summary>An Option button was pressed (index into the local player's options): move the camera there.</summary>
        public event Action<int> OptionClicked;

        public HudOverlayController(VisualElement root)
        {
            prompt = root.Q("hq-prompt");
            countdown = root.Q("countdown");
            title = root.Q<Label>("hq-prompt-title");
            body = root.Q<Label>("hq-prompt-body");
            optionButtons = root.Q("hq-option-buttons");
            progress = root.Q<Label>("hq-progress");
            seconds = root.Q<Label>("countdown-value");
        }

        /// <summary>Shows the overlay for the state: the prompt while placing HQs, the number during the countdown.</summary>
        public void Show(GameState state, PlacementPrompt placement, double countdownLeft)
        {
            prompt.EnableInClassList("hud-overlay--visible", state == GameState.PlacingHQ);
            countdown.EnableInClassList("hud-overlay--visible", state == GameState.Countdown);
            if (state == GameState.PlacingHQ) ShowPrompt(placement);
            if (state == GameState.Countdown) seconds.text = Math.Max(0, (int)Math.Ceiling(countdownLeft)).ToString();
        }

        private void ShowPrompt(PlacementPrompt p)
        {
            title.text = Title(p.PickSeconds, p.SecondsLeft);
            body.text = Body(p.Options, p.PickSeconds, p.HasTeammates);
            body.EnableInClassList("hidden", p.Options == 0); // placed (or the deal hasn't arrived): nothing to pick
            progress.text = Progress(p.Players, p.Placed, p.PickSeconds, p.TeammatesPicked);
            if (p.Options == shownOptions && p.Focused == shownFocus) return;
            shownOptions = p.Options;
            shownFocus = p.Focused;
            optionButtons.Clear();
            for (int i = 0; i < p.Options; i++)
            {
                int index = i;
                optionButtons.Add(Widgets.TextButton("hq-option-" + (i + 1), $"Option {i + 1}", i == p.Focused, () => OptionClicked?.Invoke(index)));
            }
            optionButtons.EnableInClassList("hidden", p.Options <= 1);
        }

        /// <summary>"Pick your start · 0:38" for a timed pick, "Pick your start" otherwise.</summary>
        public static string Title(int pickSeconds, double secondsLeft)
        {
            if (pickSeconds <= 0) return "Pick your start";
            int left = Math.Max(0, (int)Math.Ceiling(secondsLeft));
            return $"Pick your start · {left / 60}:{left % 60:00}";
        }

        /// <summary>The rule line under the title.</summary>
        public static string Body(int options, int pickSeconds, bool hasTeammates)
        {
            string count = options == 1 ? "your start option, ringed" : $"one of your {options} start options, ringed and numbered";
            string camera = options > 1 ? " Option buttons move the camera." : "";
            string end = hasTeammates ? "Your teammates' options are ringed in their colour."
                : pickSeconds == StartPickRules.Unlimited ? "The match waits until everyone has placed."
                : "When time runs out, your HQ goes to a random option.";
            return $"Place your HQ (5×5) inside {count}.{camera} R rotates. {end}";
        }

        /// <summary>"2 of 4 players placed" (or "Waiting for 2 players" when Unlimited), then any teammates' picks.</summary>
        public static string Progress(int players, int placed, int pickSeconds, IReadOnlyList<string> teammatesPicked)
        {
            int waiting = Math.Max(0, players - placed);
            string line = pickSeconds == StartPickRules.Unlimited
                ? $"Waiting for {waiting} {(waiting == 1 ? "player" : "players")}"
                : $"{placed} of {players} players placed";
            if (teammatesPicked != null) foreach (string name in teammatesPicked) line += $" · {name} picked their start";
            return line;
        }
    }
}

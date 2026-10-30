using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the in-match menu (<c>Assets/UI/Hud/InMatchMenu.uxml</c>): Resume, Settings, Surrender (with a
    /// confirmation) and Leave match. With one human connected the match pauses while it (or Settings opened from it) is
    /// open (v0.14, <c>GameCore.CmdSetMenuPause</c>) and the menu shows its Paused state; otherwise the match keeps running.
    /// </summary>
    public sealed class InMatchMenuController
    {
        private readonly VisualElement screen;
        private readonly Label title, note;
        private bool shownPaused;

        public const string Title = "Menu", PausedTitle = "Menu · Paused";
        public const string RunningNote = "The match keeps running while this is open.";
        public const string PausedNote = "The match is paused while this is open. Resume (or Esc) continues it.";

        public event Action SettingsClicked, SurrenderConfirmed, LeaveClicked;

        public InMatchMenuController(VisualElement root)
        {
            screen = root.Q("in-match-menu");
            title = root.Q<Label>("match-menu-title");
            note = root.Q<Label>("match-menu-note");
            root.Q<Button>("resume-button").clicked += Close;
            root.Q<Button>("match-settings-button").clicked += () => SettingsClicked?.Invoke();
            root.Q<Button>("surrender-button").clicked += () =>
            {
                // Settings → Gameplay can skip the confirm, except where a misclick would give up for a side (SettingsController.SurrenderAlwaysConfirms).
                bool locked = SettingsController.SurrenderAlwaysConfirms != null && SettingsController.SurrenderAlwaysConfirms();
                if (!SettingsStore.Current.ConfirmSurrender && !locked) { Close(); SurrenderConfirmed?.Invoke(); return; }
                screen.AddToClassList("in-match-menu--confirm");
            };
            root.Q<Button>("surrender-cancel").clicked += () => screen.RemoveFromClassList("in-match-menu--confirm");
            root.Q<Button>("surrender-confirm").clicked += () => { Close(); SurrenderConfirmed?.Invoke(); };
            Button leave = root.Q<Button>("leave-match-button");
            leave.clicked += () =>
            {
                // v0.15.1 (Figma R1): a joined player waits up to a second for the server to let go of the seat, so the menu stays
                // open with every button greyed out and Leave reading "Leaving…" (a host or a finished match leaves at once).
                screen.Query<Button>().ForEach(b => b.SetEnabled(false));
                leave.text = "Leaving…";
                LeaveClicked?.Invoke();
            };
        }

        public bool IsOpen => screen.ClassListContains("overlay-screen--visible");

        /// <summary>Opens the menu, or closes it (and any confirmation) when open: the Esc key.</summary>
        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        public void Open()
        {
            screen.RemoveFromClassList("in-match-menu--confirm");
            screen.AddToClassList("overlay-screen--visible");
        }

        public void Close()
        {
            screen.RemoveFromClassList("overlay-screen--visible");
            screen.RemoveFromClassList("in-match-menu--confirm");
        }

        /// <summary>True while the menu shows its Paused state.</summary>
        public bool ShowsPaused => shownPaused;

        /// <summary>Shows the Paused state (title, banner, note) while the server says the menu pauses the match.</summary>
        public void ShowPaused(bool paused)
        {
            if (paused == shownPaused) return;
            shownPaused = paused;
            screen.EnableInClassList("in-match-menu--paused", paused);
            if (title != null) title.text = paused ? PausedTitle : Title;
            if (note != null) note.text = paused ? PausedNote : RunningNote;
        }

        /// <summary>Whether Surrender is offered (only while the local player is still in the match).</summary>
        public void SetCanSurrender(VisualElement root, bool can) => root.Q<Button>("surrender-button").SetEnabled(can);
    }
}

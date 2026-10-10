using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the in-match menu (<c>Assets/UI/Hud/InMatchMenu.uxml</c>): Resume, Settings, Surrender (with a
    /// confirmation) and Leave match. Nothing pauses: the match keeps running behind it.
    /// </summary>
    public sealed class InMatchMenuController
    {
        private readonly VisualElement screen;

        public event Action SettingsClicked, SurrenderConfirmed, LeaveClicked;

        public InMatchMenuController(VisualElement root)
        {
            screen = root.Q("in-match-menu");
            root.Q<Button>("resume-button").clicked += Close;
            root.Q<Button>("match-settings-button").clicked += () => SettingsClicked?.Invoke();
            root.Q<Button>("surrender-button").clicked += () => screen.AddToClassList("in-match-menu--confirm");
            root.Q<Button>("surrender-cancel").clicked += () => screen.RemoveFromClassList("in-match-menu--confirm");
            root.Q<Button>("surrender-confirm").clicked += () => { Close(); SurrenderConfirmed?.Invoke(); };
            root.Q<Button>("leave-match-button").clicked += () => { Close(); LeaveClicked?.Invoke(); };
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

        /// <summary>Whether Surrender is offered (only while the local player is still in the match).</summary>
        public void SetCanSurrender(VisualElement root, bool can) => root.Q<Button>("surrender-button").SetEnabled(can);
    }
}

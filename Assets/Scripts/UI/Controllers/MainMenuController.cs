using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>Binds the main menu (<c>Assets/UI/Menus/MainMenu.uxml</c>): Play, Replays, Settings and Quit.</summary>
    public sealed class MainMenuController
    {
        public event Action PlayClicked, ReplaysClicked, SettingsClicked, QuitClicked;

        public MainMenuController(VisualElement root)
        {
            root.Q<Button>("play-button").clicked += () => PlayClicked?.Invoke();
            root.Q<Button>("replays-button").clicked += () => ReplaysClicked?.Invoke();
            root.Q<Button>("settings-button").clicked += () => SettingsClicked?.Invoke();
            root.Q<Button>("quit-button").clicked += () => QuitClicked?.Invoke();
            // The version line comes from <Release> in GameConfig.xml (2026-11-02; it was a hard-coded "v0.10 Insight").
            Label version = root.Q<Label>("version");
            if (version != null && Config.ConfigLoader.IsValid) version.text = Config.ConfigLoader.LoadConfig().Release.Display;
        }
    }
}

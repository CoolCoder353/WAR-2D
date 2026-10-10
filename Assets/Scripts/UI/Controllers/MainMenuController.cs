using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>Binds the main menu (<c>Assets/UI/Menus/MainMenu.uxml</c>): Play, Settings and Quit.</summary>
    public sealed class MainMenuController
    {
        public event Action PlayClicked, SettingsClicked, QuitClicked;

        public MainMenuController(VisualElement root)
        {
            root.Q<Button>("play-button").clicked += () => PlayClicked?.Invoke();
            root.Q<Button>("settings-button").clicked += () => SettingsClicked?.Invoke();
            root.Q<Button>("quit-button").clicked += () => QuitClicked?.Invoke();
        }
    }
}

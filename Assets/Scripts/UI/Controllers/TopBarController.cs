using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the top bar (<c>Assets/UI/Hud/TopBar.uxml</c>) to the <see cref="HudModel"/>: the player list,
    /// the local player's resources, income and upkeep, and the Gift, Diplomacy and Menu buttons.
    /// Queries elements by name only and holds no game logic.
    /// </summary>
    public sealed class TopBarController : IDisposable
    {
        private readonly HudModel model;
        private readonly VisualElement players;
        private readonly Label resources, income, upkeep;

        /// <summary>Raised when the Gift, Diplomacy or Menu button is clicked.</summary>
        public event Action GiftClicked, DiplomacyClicked, MenuClicked;

        public TopBarController(VisualElement root, HudModel model)
        {
            this.model = model;
            players = root.Q("players");
            resources = root.Q<Label>("resources-value");
            income = root.Q<Label>("income-value");
            upkeep = root.Q<Label>("upkeep-value");
            root.Q<Button>("gift-button").clicked += () => GiftClicked?.Invoke();
            root.Q<Button>("diplomacy-button").clicked += () => DiplomacyClicked?.Invoke();
            root.Q<Button>("menu-button").clicked += () => MenuClicked?.Invoke();
            model.ResourcesChanged += ShowResources;
            model.PlayersChanged += ShowPlayers;
            ShowResources();
            ShowPlayers();
        }

        private void ShowResources()
        {
            resources.text = model.Resources.ToString("N0");
            income.text = $"+{model.IncomePerSecond:N0}/s income";
            upkeep.text = $"−{model.UpkeepPerSecond:N0}/s upkeep";
        }

        private void ShowPlayers()
        {
            players.Clear();
            foreach (PlayerRow row in model.Players)
            {
                var chip = new VisualElement { name = "player-" + row.OwnerId };
                chip.AddToClassList("top-bar__player");
                var swatch = new VisualElement();
                swatch.AddToClassList("swatch");
                swatch.AddToClassList(row.Eliminated ? "swatch--eliminated" : "player-" + (row.ColourIndex % 8 + 1));
                var label = new Label(row.Eliminated ? row.Nickname + " (eliminated)" : row.Nickname);
                label.AddToClassList("top-bar__name");
                if (row.Eliminated) label.AddToClassList("top-bar__name--eliminated");
                chip.Add(swatch);
                chip.Add(label);
                players.Add(chip);
            }
        }

        public void Dispose()
        {
            model.ResourcesChanged -= ShowResources;
            model.PlayersChanged -= ShowPlayers;
        }
    }
}

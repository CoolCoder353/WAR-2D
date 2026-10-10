using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the diplomacy panel (<c>Assets/UI/Hud/DiplomacyPanel.uxml</c>): a row per other player with
    /// their colour, whether they share vision with you, and switches for whether you attack them and
    /// share your vision with them. Shown from the top bar's Diplomacy button, only when the match allows
    /// diplomacy. State comes only from the local player's own row (<see cref="HudModel"/>).
    /// </summary>
    public sealed class DiplomacyPanelController : IDisposable
    {
        private readonly HudModel model;
        private readonly VisualElement panel, rows;

        /// <summary>Raised by a switch: (owner, on) for attacking them.</summary>
        public event Action<int, bool> AttackToggled;

        /// <summary>Raised by a switch: (owner, on) for sharing vision with them.</summary>
        public event Action<int, bool> ShareToggled;

        public DiplomacyPanelController(VisualElement root, HudModel model)
        {
            this.model = model;
            panel = root.Q("diplomacy-panel");
            rows = root.Q("diplomacy-rows");
            model.PlayersChanged += Show;
            model.DiplomacyChanged += Show;
            Show();
        }

        /// <summary>True while the panel is open.</summary>
        public bool IsOpen => panel.ClassListContains("diplomacy-panel--open");

        /// <summary>Opens or closes the panel (closed whenever the match doesn't allow diplomacy).</summary>
        public void Toggle() => SetOpen(!IsOpen);

        public void SetOpen(bool open) => panel.EnableInClassList("diplomacy-panel--open", open && model.DiplomacyEnabled);

        private void Show()
        {
            if (!model.DiplomacyEnabled) SetOpen(false);
            rows.Clear();
            foreach (PlayerRow player in model.Players)
            {
                if (player.OwnerId == model.LocalOwnerId) continue;
                int bit = 1 << player.OrderIndex;
                var row = new VisualElement { name = "diplomacy-row-" + player.OwnerId };
                row.AddToClassList("diplomacy-row");

                var swatch = new VisualElement();
                swatch.AddToClassList("swatch");
                swatch.AddToClassList(player.Eliminated ? "swatch--eliminated" : "player-" + (player.ColourIndex % 8 + 1));
                var name = new VisualElement();
                name.AddToClassList("diplomacy-row__name");
                var nick = new Label(player.Eliminated ? player.Nickname + " (eliminated)" : player.Nickname);
                nick.AddToClassList("text");
                if (player.Eliminated) nick.AddToClassList("text--disabled");
                name.Add(nick);
                if (!player.Eliminated && (model.SharedWithMe & bit) != 0)
                {
                    var shares = new Label("Shares vision with you");
                    shares.AddToClassList("diplomacy-row__shares");
                    name.Add(shares);
                }

                int owner = player.OwnerId;
                bool attack = (model.AttackMask & bit) != 0, share = (model.ShareMask & bit) != 0;
                row.Add(swatch);
                row.Add(name);
                row.Add(Widgets.Switch("attack-" + owner, attack, !player.Eliminated, () => AttackToggled?.Invoke(owner, !attack)));
                row.Add(Widgets.Switch("share-" + owner, share, !player.Eliminated, () => ShareToggled?.Invoke(owner, !share)));
                rows.Add(row);
            }
        }

        public void Dispose()
        {
            model.PlayersChanged -= Show;
            model.DiplomacyChanged -= Show;
        }
    }
}

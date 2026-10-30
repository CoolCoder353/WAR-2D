using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the top bar (<c>Assets/UI/Hud/TopBar.uxml</c>) to the <see cref="HudModel"/>: the player list, the local player's
    /// resources with the v0.15 net readout as the main rate (income and upkeep as a quieter breakdown, four states: Figma v0.15
    /// QoL redesign, frame 4), the quick-select counts right of it (frame 1), and the HQ, Gift, Diplomacy and Menu buttons.
    /// Queries elements by name only and holds no game logic.
    /// </summary>
    public sealed class TopBarController : IDisposable
    {
        /// <summary>How long the idle Builders count's gold outline shows when it goes from 0 to more (once, never repeating).</summary>
        private const long PulseMilliseconds = 600;

        private readonly HudModel model;
        private readonly VisualElement players, resourcesBlock;
        private readonly Label resources, income, upkeep, net, netSelect;
        private readonly Button idleChip, buildersChip, armyChip, hqButton;
        private readonly Label idleCount, buildersCount, armyCount;
        private readonly Dictionary<int, string> away = new Dictionary<int, string>();
        private int lastIdle = -1;
        private int decaying;

        /// <summary>Raised when the Gift, Diplomacy or Menu button is clicked.</summary>
        public event Action GiftClicked, DiplomacyClicked, MenuClicked;

        /// <summary>Raised by the HQ button (Jump to HQ).</summary>
        public event Action HqClicked;

        /// <summary>Raised by the quick-select counts: idle Builders (false) or, with the assign modifier, all Builders (true).</summary>
        public event Action<bool> BuildersClicked;

        /// <summary>Raised by the Army count.</summary>
        public event Action ArmyClicked;

        /// <summary>Raised by "· select" in the Decaying state: select what is decaying.</summary>
        public event Action DecayingClicked;

        public TopBarController(VisualElement root, HudModel model, HoverCard card = null)
        {
            this.model = model;
            players = root.Q("players");
            resourcesBlock = root.Q("resources");
            resources = root.Q<Label>("resources-value");
            income = root.Q<Label>("income-value");
            upkeep = root.Q<Label>("upkeep-value");
            net = root.Q<Label>("net-value");
            netSelect = root.Q<Label>("net-select");
            idleChip = root.Q<Button>("select-idle-builders");
            buildersChip = root.Q<Button>("select-all-builders");
            armyChip = root.Q<Button>("select-army");
            idleCount = root.Q<Label>("select-idle-builders-count");
            buildersCount = root.Q<Label>("select-all-builders-count");
            armyCount = root.Q<Label>("select-army-count");
            hqButton = root.Q<Button>("hq-button");
            root.Q<Button>("gift-button").clicked += () => GiftClicked?.Invoke();
            root.Q<Button>("diplomacy-button").clicked += () => DiplomacyClicked?.Invoke();
            root.Q<Button>("menu-button").clicked += () => MenuClicked?.Invoke();
            hqButton.clicked += () => HqClicked?.Invoke();
            idleChip.clicked += () => BuildersClicked?.Invoke(GameInput.AssignModifier.IsPressed());
            buildersChip.clicked += () => BuildersClicked?.Invoke(true);
            armyChip.clicked += () => ArmyClicked?.Invoke();
            netSelect.RegisterCallback<ClickEvent>(_ => DecayingClicked?.Invoke());
            if (card != null)
            {
                string Assign() => HoverCard.KeyOf(GameInput.AssignModifier);
                card.Attach(idleChip, () => HoverText.IdleBuilders(HoverCard.KeyOf(GameInput.SelectIdleBuilders), Assign()), HoverCard.Side.Below, resourcesBlock.parent);
                card.Attach(buildersChip, () => HoverText.AllBuilders(HoverCard.KeyOf(GameInput.SelectIdleBuilders), Assign()), HoverCard.Side.Below, resourcesBlock.parent);
                card.Attach(armyChip, () => HoverText.Army(HoverCard.KeyOf(GameInput.SelectArmy)), HoverCard.Side.Below, resourcesBlock.parent);
                card.Attach(hqButton, () => HoverText.Hq(HoverCard.KeyOf(GameInput.JumpToHq)), HoverCard.Side.Below, resourcesBlock.parent);
                card.Attach(net, () => HoverText.Net(model.Economy), HoverCard.Side.Below, resourcesBlock.parent);
            }
            model.ResourcesChanged += ShowResources;
            model.PlayersChanged += ShowPlayers;
            ShowResources();
            ShowPlayers();
            ShowNet();
            ShowQuick(default);
        }

        private void ShowResources()
        {
            resources.text = model.Resources.ToString("N0");
            income.text = $"+{model.IncomePerSecond:N0} income";
            upkeep.text = $"−{model.UpkeepPerSecond:N0} upkeep";
        }

        /// <summary>
        /// The net readout in its state colour (healthy green, negative amber with ▼, running low and decaying red with the
        /// balance outlined). Called on the HUD's refresh timer, since the five-second average moves without new numbers.
        /// </summary>
        public void ShowNet()
        {
            EconomyReadout economy = model.Economy;
            EconomyState state = economy.State;
            net.text = state == EconomyState.Decaying && decaying > 0 ? $"{decaying:N0} decaying" : economy.Text();
            net.EnableInClassList("top-bar__net--healthy", state == EconomyState.Healthy);
            net.EnableInClassList("top-bar__net--negative", state == EconomyState.Negative);
            net.EnableInClassList("top-bar__net--danger", state == EconomyState.RunningLow || state == EconomyState.Decaying);
            resources.EnableInClassList("top-bar__resources-value--danger", state == EconomyState.RunningLow || state == EconomyState.Decaying);
            netSelect.style.display = state == EconomyState.Decaying && decaying > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>How many own buildings are decaying (unpaid) as the client knows them: the Decaying state's "12 decaying".</summary>
        public void SetDecaying(int count) => decaying = count;

        /// <summary>
        /// The quick-select counts: idle Builders in gold ("3 idle", dimmed and not clickable at 0, the gold outline once when it
        /// goes from 0 to more), all Builders, and the army.
        /// </summary>
        public void ShowQuick(WAR2D.Client.QuickCounts counts)
        {
            idleCount.text = counts.IdleBuilders.ToString("N0");
            buildersCount.text = counts.Builders.ToString("N0");
            armyCount.text = counts.Army.ToString("N0");
            idleChip.SetEnabled(counts.IdleBuilders > 0);
            buildersChip.SetEnabled(counts.Builders > 0);
            armyChip.SetEnabled(counts.Army > 0);
            if (lastIdle == 0 && counts.IdleBuilders > 0)
            {
                idleChip.AddToClassList("quick-select__chip--pulse");
                idleChip.schedule.Execute(() => idleChip.RemoveFromClassList("quick-select__chip--pulse")).StartingIn(PulseMilliseconds);
            }
            lastIdle = counts.IdleBuilders;
        }

        /// <summary>Shows the HQ button only while the local player has an HQ (hidden when spectating or in a replay).</summary>
        public void ShowHq(bool shown) => hqButton.EnableInClassList("top-bar__hidden", !shown);

        /// <summary>
        /// Reconnect hook (item 19, frame E1): marks a player as away, greying the name and adding <paramref name="timer"/> ("0:42")
        /// after it; null clears it. The reconnect work calls this every second with the time left.
        /// </summary>
        public void SetAway(int ownerId, string timer)
        {
            bool changed = timer == null ? away.Remove(ownerId) : !away.TryGetValue(ownerId, out string old) || old != timer;
            if (timer != null) away[ownerId] = timer;
            if (changed) ShowPlayers();
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
                bool isAway = !row.Eliminated && away.TryGetValue(row.OwnerId, out string timer) && timer != null;
                // The AI tag follows an AI's callsign; "(eliminated)" goes last, greyed like a human (Figma 95:563).
                VisualElement label = PlayerNameLabel.Build(row.Nickname, row.AiTag, "top-bar__name", row.Eliminated ? "(eliminated)" : null, row.Eliminated);
                if (row.Eliminated || isAway) label.Q<Label>("player-name-text").AddToClassList("top-bar__name--eliminated");
                chip.Add(swatch);
                chip.Add(label);
                if (isAway)
                {
                    // The away marker (reconnect, item 19, Figma 135:1503): the reconnect icon, then the timer counting down.
                    chip.Add(ReconnectBannerController.SmallIcon());
                    var marker = new Label(away[row.OwnerId]) { name = "player-away-" + row.OwnerId };
                    marker.AddToClassList("top-bar__away");
                    chip.Add(marker);
                }
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

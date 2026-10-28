using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.Client.Overlays
{
    /// <summary>
    /// The building hover card (Figma <c>v0.15 QoL – redesign</c>, item 12, frame 128:1542): resting the pointer on a
    /// building opens a card above the pointer, after the Gameplay setting's tooltip delay (Fast 0.25 s, Normal 0.6 s,
    /// Off never). A visible enemy shows its name, owner and health; a ghost the same as last seen, an "Out of sight"
    /// tag and "Last seen 2:14 ago"; an own building its health and upkeep ("Unpaid" and "decaying" when unpaid).
    /// Gems, rock and walls show no card. It reads only <see cref="BuildingHover.TryDescribe"/> (the client's own
    /// building set), so it never shows more than the client knows, and never production. The card is built from the
    /// HUD's <c>build-tooltip</c> classes and added to the HUD's tag layer at run time.
    /// </summary>
    public sealed class BuildingHoverCard : IDisposable
    {
        private const float AbovePointer = 14f;
        private VisualElement card, swatch, fill;
        private Label title, tag, owner, healthText, footer;
        private VisualElement layer;
        private int hoveredId;
        private float restSince;
        private (int id, int health, int max, bool ghost, int ago, byte progress, bool unpaid, int owner) shownKey;

        /// <summary>The building whose card is open (tests), or 0.</summary>
        public int OpenId { get; private set; }

        public void Update(OverlayFrame f)
        {
            int mode = SettingsStore.Current.TooltipDelay;
            bool blocked = f.PointerOverUI || GameInput.Select.IsPressed() || f.Commander.RallyAllArmed || f.Commander.ArmedOrder.HasValue ||
                           (BuildingPlacement.Instance != null && BuildingPlacement.Instance.IsPlacing);
            BuildingHoverInfo info = default;
            bool over = !blocked && OverlayRules.TooltipDelaySeconds(mode) >= 0f &&
                        BuildingHover.TryDescribe(f.Commander.Picker, f.Commander.KnownBuildings, Config.ConfigLoader.LoadConfig(), (int2)math.floor(f.Pointer), f.Own, Time.time, out info);
            int id = over ? info.Id : 0;
            if (id != hoveredId)
            {
                hoveredId = id;
                restSince = f.Now;
            }
            if (id == 0 || !OverlayRules.TooltipOpen(mode, f.Now - restSince) || !Ensure(f.Labels))
            {
                Hide();
                return;
            }
            Fill(info);
            Vector2 p = RuntimePanelUtils.ScreenToPanel(layer.panel, new Vector2(f.PointerScreen.x, Screen.height - f.PointerScreen.y));
            card.style.left = p.x;
            card.style.top = p.y - AbovePointer;
            card.style.display = DisplayStyle.Flex;
            OpenId = id;
        }

        /// <summary>Closes the card.</summary>
        public void Hide()
        {
            OpenId = 0;
            if (card != null && card.style.display != DisplayStyle.None) card.style.display = DisplayStyle.None;
        }

        private void Fill(in BuildingHoverInfo info)
        {
            var key = (info.Id, (int)math.round(info.Health), (int)math.round(info.MaxHealth), info.Ghost, (int)info.LastSeenSecondsAgo, info.Progress, info.Unpaid, info.OwnerId);
            if (key.Equals(shownKey) && OpenId == info.Id) return;
            shownKey = key;

            title.text = WAR2D.UI.AlertFeedModel.BuildingName(info.Type) ?? WAR2D.UI.AlertFeedModel.Spaced(info.Type.ToString());
            tag.text = info.Ghost ? "Out of sight" : info.Unpaid ? "Unpaid" : "";
            tag.style.display = info.Ghost || info.Unpaid ? DisplayStyle.Flex : DisplayStyle.None;
            Color tagColour = info.Unpaid ? (Color)OverlayColours.Danger(Palettes.Current) : new Color(0xAE / 255f, 0xBB / 255f, 0xCB / 255f);
            tag.style.color = tagColour;
            tag.style.borderTopColor = tag.style.borderBottomColor = tag.style.borderLeftColor = tag.style.borderRightColor = tagColour;

            string name = ClientPlayer.TryFind(info.OwnerId, out ClientPlayer player) && !string.IsNullOrEmpty(player.nickname) ? player.nickname : "Unknown";
            owner.text = info.Own ? $"{name} (you)" : name;
            swatch.style.backgroundColor = (Color)PlayerPalette.OfOwner(info.OwnerId);

            float h = math.saturate(info.Health01);
            fill.style.width = Length.Percent(100f * h);
            fill.style.backgroundColor = info.Ghost ? new Color(0xAE / 255f, 0xBB / 255f, 0xCB / 255f) // a ghost's health is old news: grey
                : (Color)(info.Unpaid ? Palettes.Warning(Palettes.Current) : h < 0.35f ? OverlayColours.Danger(Palettes.Current) : OverlayColours.Success(Palettes.Current));
            healthText.text = $"{key.Item2} / {key.Item3}";

            footer.RemoveFromClassList("text--danger");
            footer.EnableInClassList("text--secondary", true);
            if (info.Ghost) footer.text = $"Last seen {BuildingHover.Ago(info.LastSeenSecondsAgo)} ago";
            else if (info.Progress < 100) footer.text = $"Under construction · {info.Progress}%";
            else if (info.Own && info.Unpaid)
            {
                footer.text = $"Upkeep {Rate(info.Upkeep)}/s · decaying";
                footer.EnableInClassList("text--secondary", false);
                footer.AddToClassList("text--danger");
            }
            else if (info.Own && info.Upkeep > 0f) footer.text = $"Upkeep {Rate(info.Upkeep)}/s";
            else footer.text = "";
            footer.style.display = footer.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static string Rate(float v) => v.ToString(v == math.floor(v) ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Builds the card in the HUD's tag layer the first time (and again if the HUD was rebuilt). False without a HUD.</summary>
        private bool Ensure(WorldLabels labels)
        {
            VisualElement l = labels.Layer;
            if (l == null) return false;
            if (card != null && layer == l && card.parent == l) return true;
            layer = l;
            card = new VisualElement { name = "building-hover-card", pickingMode = PickingMode.Ignore };
            card.AddToClassList("build-tooltip");
            card.AddToClassList("build-tooltip--open");
            card.style.minWidth = 200;

            var header = Row(card);
            header.style.justifyContent = Justify.SpaceBetween;
            title = Text(header, "text");
            tag = Text(header, "text--secondary");
            tag.style.borderTopWidth = tag.style.borderBottomWidth = tag.style.borderLeftWidth = tag.style.borderRightWidth = 1;
            tag.style.paddingLeft = tag.style.paddingRight = 4;
            tag.style.marginLeft = 12;

            var ownerRow = Row(card);
            swatch = new VisualElement { pickingMode = PickingMode.Ignore };
            swatch.style.width = swatch.style.height = 8;
            swatch.style.marginRight = 6;
            ownerRow.Add(swatch);
            owner = Text(ownerRow, "text--secondary");

            var healthRow = Row(card);
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.AddToClassList("health-bar");
            bar.style.width = 110;
            bar.style.marginRight = 8;
            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList("health-bar__fill");
            bar.Add(fill);
            healthRow.Add(bar);
            healthText = Text(healthRow, "text--secondary");

            footer = Text(card, "text--secondary");
            card.style.display = DisplayStyle.None;
            l.Add(card);
            shownKey = default;
            return true;
        }

        private static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 4;
            parent.Add(row);
            return row;
        }

        private static Label Text(VisualElement parent, string cls)
        {
            var label = new Label { pickingMode = PickingMode.Ignore };
            label.AddToClassList(cls);
            label.style.marginTop = label.style.marginBottom = 0;
            label.style.paddingTop = label.style.paddingBottom = 0;
            parent.Add(label);
            return label;
        }

        public void Dispose()
        {
            card?.RemoveFromHierarchy();
            card = null;
        }
    }
}

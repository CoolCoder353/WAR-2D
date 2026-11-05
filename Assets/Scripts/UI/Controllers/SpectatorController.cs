using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// What a defeated player sees while the match goes on (owner, 2026-11-04: the replay viewer of Figma R1, for a live match): the
    /// players' numbers along the top, and the match time, the View selector and Leave along the bottom. A live match has no
    /// seek bar or speed, and its graphs still wait for the end screen. Choosing a player asks the server for that player's
    /// team's sight (<see cref="GameCore.Cmd_SetSpectateView"/>), so the fog, units and ghosts arrive as they would for them.
    /// </summary>
    public sealed class SpectatorController
    {
        private const float RefreshSeconds = 0.25f;

        private readonly VisualElement panel, players, viewList;
        private readonly Label time;
        private readonly Button viewButton;
        private readonly Func<IEnumerable<PlayerRow>> rows;
        private readonly Func<int, string> aiTagOf;
        private float refresh;
        private int viewed;

        /// <summary>The whole screen's layer, named as the panel it replaces.</summary>
        public VisualElement Panel => panel;

        public SpectatorController(VisualElement root, Func<IEnumerable<PlayerRow>> rows, Func<int, string> aiTagOf, Action leave)
        {
            this.rows = rows;
            this.aiTagOf = aiTagOf;
            panel = new VisualElement { name = "spectating-panel", pickingMode = PickingMode.Ignore };
            panel.AddToClassList("spectator");

            players = new VisualElement { name = "spectating-players" };
            players.AddToClassList("panel");
            players.AddToClassList("row");
            players.AddToClassList("replay-viewer__bar");
            panel.Add(players);

            viewList = new VisualElement { name = "spectating-view-list" };
            viewList.AddToClassList("panel");
            viewList.AddToClassList("replay-viewer__view-list");
            viewList.AddToClassList("hidden");
            panel.Add(viewList);

            var controls = new VisualElement { name = "spectating-controls" };
            controls.AddToClassList("panel");
            controls.AddToClassList("row");
            controls.AddToClassList("replay-viewer__bar");
            var title = new Label("Spectating");
            title.AddToClassList("text");
            controls.Add(title);
            time = new Label("0:00") { name = "spectating-time" };
            time.AddToClassList("text");
            time.AddToClassList("replay-viewer__time");
            controls.Add(time);
            viewButton = new Button(ToggleViewList) { text = "View: Everything", name = "spectating-view" };
            viewButton.AddToClassList("button");
            controls.Add(viewButton);
            var leaveButton = new Button(leave) { text = "Leave", name = "spectating-leave" };
            leaveButton.AddToClassList("button");
            controls.Add(leaveButton);
            panel.Add(controls);
            root.Add(panel);
        }

        /// <summary>Called every frame while spectating.</summary>
        public void Tick(float deltaSeconds)
        {
            GameCore core = GameCore.Instance;
            if (core != null) time.text = ReplaysModel.Duration((float)(NetworkTime.time - core.MatchStartTime));
            refresh -= deltaSeconds;
            if (refresh > 0f) return;
            refresh = RefreshSeconds;
            ClientPlayer me = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;
            if (viewed != 0 && me != null && me.SpectatorOwners != null && Array.IndexOf(me.SpectatorOwners, viewed) < 0) ShowViewed(0, "Everything"); // the server fell back when they went out
            RenderPlayers(me);
        }

        private void RenderPlayers(ClientPlayer me)
        {
            players.Clear();
            foreach (PlayerRow row in rows())
            {
                float resources = 0f; int units = 0;
                if (me != null && me.SpectatorOwners != null)
                {
                    int i = Array.IndexOf(me.SpectatorOwners, row.OwnerId);
                    if (i >= 0) { resources = me.SpectatorResources[i]; units = me.SpectatorUnits[i]; }
                }
                var chip = new VisualElement { name = "spectating-player-" + row.OwnerId };
                chip.AddToClassList("replay-viewer__player");
                chip.EnableInClassList("replay-viewer__player--viewed", viewed == row.OwnerId);
                var swatch = new VisualElement();
                swatch.AddToClassList("lobby-row__swatch");
                swatch.AddToClassList("player-" + (row.ColourIndex % 8 + 1));
                chip.Add(swatch);
                chip.Add(PlayerNameLabel.Build(string.IsNullOrEmpty(row.Nickname) ? "Player" : row.Nickname, aiTagOf(row.OwnerId), "text"));
                var numbers = new Label(Numbers(resources, units));
                numbers.AddToClassList("replay-row__meta");
                chip.Add(numbers);
                players.Add(chip);
            }
        }

        /// <summary>A player's numbers: "3,420 res · 410 units", as the replay viewer writes them.</summary>
        public static string Numbers(float resources, int units) =>
            resources >= ServerPlayer.InfiniteBalance ? $"Infinite · {units:N0} units" : $"{resources:N0} res · {units:N0} units";

        private void ToggleViewList()
        {
            bool show = viewList.ClassListContains("hidden");
            viewList.EnableInClassList("hidden", !show);
            if (!show) return;
            viewList.Clear();
            viewList.Add(ViewEntry(0, "Everything"));
            ClientPlayer me = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;
            foreach (PlayerRow row in rows())
                if (me != null && me.SpectatorOwners != null && Array.IndexOf(me.SpectatorOwners, row.OwnerId) >= 0)
                    viewList.Add(ViewEntry(row.OwnerId, string.IsNullOrEmpty(row.Nickname) ? "Player" : row.Nickname));
        }

        private Button ViewEntry(int owner, string label) => Widgets.Option("spectating-view-" + owner, label, viewed == owner, true, () =>
        {
            GameCore.Instance?.Cmd_SetSpectateView(owner);
            ShowViewed(owner, label);
            viewList.AddToClassList("hidden");
            refresh = 0f;
        });

        private void ShowViewed(int owner, string label)
        {
            viewed = owner;
            viewButton.text = "View: " + label;
            viewButton.EnableInClassList("button--primary", owner != 0);
        }
    }
}

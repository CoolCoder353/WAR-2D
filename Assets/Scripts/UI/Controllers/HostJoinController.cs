using System;
using System.Collections.Generic;
using UnityEngine.UIElements;
using WAR2D.Net.Steam;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the Play screen (<c>Assets/UI/Menus/HostJoin.uxml</c>): the nickname, Host, and Join with its
    /// states (invalid address, connecting with Cancel, couldn't connect).
    /// </summary>
    public sealed class HostJoinController
    {
        private readonly TextField nickname, address;
        private readonly VisualElement addressInput, joinButtons, connecting;
        private readonly Label connectingText, failed;

        public event Action HostClicked, BackClicked, CancelClicked, RefreshClicked;

        /// <summary>Raised with the lobby the player chose to join.</summary>
        public event Action<LobbyInfo> JoinGameClicked;

        private readonly VisualElement gameList, friendsEmpty, directBlock, directSection;
        private readonly Label steamOff, friendsNotice;
        private readonly Button directToggle;

        /// <summary>Raised with a valid address.</summary>
        public event Action<string> JoinClicked;

        public HostJoinController(VisualElement root)
        {
            nickname = root.Q<TextField>("nickname-field");
            address = root.Q<TextField>("address-field");
            addressInput = root.Q("address-input");
            joinButtons = root.Q("join-buttons");
            connecting = root.Q("connecting");
            connectingText = root.Q<Label>("connecting-text");
            failed = root.Q<Label>("join-failed");

            gameList = root.Q("game-list");
            friendsEmpty = root.Q("friends-empty");
            steamOff = root.Q<Label>("steam-off");
            friendsNotice = root.Q<Label>("friends-notice");
            directBlock = root.Q("direct-block");
            directSection = root.Q("direct-section");
            directToggle = root.Q<Button>("direct-toggle");
            root.Q<Button>("refresh-button").clicked += () => RefreshClicked?.Invoke();
            directToggle.clicked += () => SetDirectExpanded(directSection.ClassListContains("hidden"));
            SetDirectExpanded(false);

            nickname.value = MenuModel.Nickname;
            address.value = MenuModel.Address;
            nickname.RegisterValueChangedCallback(e => MenuModel.Nickname = e.newValue);
            address.RegisterValueChangedCallback(_ => addressInput.RemoveFromClassList("input--error"));
            root.Q<Button>("host-button").clicked += () => HostClicked?.Invoke();
            root.Q<Button>("back-button").clicked += () => BackClicked?.Invoke();
            root.Q<Button>("cancel-button").clicked += () => CancelClicked?.Invoke();
            root.Q<Button>("join-button").clicked += Join;
        }

        /// <summary>Shows the friends' list: the rows, or the empty state when there are none.</summary>
        public void ShowGames(IReadOnlyList<LobbyInfo> games)
        {
            steamOff.RemoveFromClassList("play-panel__notice--visible");
            gameList.Clear();
            foreach (LobbyInfo game in games)
            {
                LobbyInfo g = game;
                var row = new VisualElement();
                row.AddToClassList("row");
                row.AddToClassList("game-row");
                var info = new VisualElement();
                info.AddToClassList("game-row__name");
                info.Add(new Label(g.HostName) { name = "game-host" });
                var meta = new Label($"{(g.Mode == nameof(MatchMode.Teams) ? "Teams" : "Free for all")} · map {g.MapSize}");
                meta.AddToClassList("game-row__meta");
                info.Add(meta);
                row.Add(info);
                row.Add(new Label($"{g.Players}/{LobbyRules.MaxPlayers}"));
                var join = new Button(() => JoinGameClicked?.Invoke(g)) { text = "Join", name = "game-join" };
                join.AddToClassList("button");
                join.AddToClassList("button--primary");
                row.Add(join);
                gameList.Add(row);
            }
            friendsEmpty.EnableInClassList("play-panel__notice--visible", games.Count == 0);
        }

        /// <summary>Replaces the list with "Steam isn't running".</summary>
        public void ShowSteamOff()
        {
            gameList.Clear();
            friendsEmpty.RemoveFromClassList("play-panel__notice--visible");
            steamOff.AddToClassList("play-panel__notice--visible");
        }

        /// <summary>Shows a failure notice under the friends' list; null clears it.</summary>
        public void ShowFriendsNotice(string message)
        {
            friendsNotice.text = message ?? "";
            friendsNotice.EnableInClassList("play-panel__notice--visible", !string.IsNullOrEmpty(message));
        }

        /// <summary>Shows or hides the Direct (IP) block as a whole (it is off in release builds without -direct).</summary>
        public void SetDirectAvailable(bool available) => directBlock.EnableInClassList("hidden", !available);

        /// <summary>Expands or collapses the Direct (IP) form.</summary>
        public void SetDirectExpanded(bool expanded)
        {
            directSection.EnableInClassList("hidden", !expanded);
            directToggle.text = expanded ? "▾ Direct (IP)" : "▸ Direct (IP)";
        }

        private void Join()
        {
            string value = address.value?.Trim();
            if (!MenuModel.IsAddressValid(value))
            {
                addressInput.AddToClassList("input--error");
                return;
            }
            MenuModel.Address = value;
            JoinClicked?.Invoke(value);
        }

        /// <summary>Shows the join form (no state).</summary>
        public void ShowIdle()
        {
            joinButtons.RemoveFromClassList("hidden");
            connecting.RemoveFromClassList("play-panel__state--visible");
            failed.RemoveFromClassList("play-panel__state--visible");
        }

        /// <summary>Shows "Connecting to …" with Cancel.</summary>
        public void ShowConnecting(string to)
        {
            joinButtons.AddToClassList("hidden");
            connectingText.text = $"Connecting to {to}…";
            connecting.AddToClassList("play-panel__state--visible");
            failed.RemoveFromClassList("play-panel__state--visible");
        }

        /// <summary>Shows why the last join failed, under the join form (<paramref name="to"/> as typed, with or without a port).</summary>
        public void ShowFailed(string to)
        {
            ShowIdle();
            if (to != null && to.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
            {
                ShowIdle();
                failed.text = "Couldn't reach the host. Check that Steam is online and try again.";
                failed.AddToClassList("play-panel__state--visible");
                ShowFriendsNotice(failed.text);
                return;
            }
            ushort port = GameManager.Instance != null ? GameManager.Instance.DefaultPort : (ushort)7778;
            string at = Net.JoinAddress.TryParse(to, port, out string host, out port) ? (host.Contains(":") ? $"[{host}]:{port}" : $"{host}:{port}") : to;
            failed.text = $"No server answered at {at}. Check the address, that the host is running, and that their port is open to the internet.";
            failed.AddToClassList("play-panel__state--visible");
        }

        /// <summary>Shows that the server answered but closed the connection before the lobby (match started or full).</summary>
        public void ShowRefused(string to)
        {
            ShowIdle();
            if (to != null && to.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
            {
                ShowFriendsNotice(SteamAuthenticator.LastRefusal ?? "Game full or already started.");
                return;
            }
            failed.text = SteamAuthenticator.LastRefusal != null
                ? $"{to} refused the connection: {SteamAuthenticator.LastRefusal}"
                : $"{to} closed the connection: its match has already started, or it is full ({LobbyRules.MaxPlayers} players).";
            failed.AddToClassList("play-panel__state--visible");
        }
    }
}

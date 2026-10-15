using System;
using UnityEngine;
using WAR2D.Net.Steam;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the friends' games list on the Play screen to <see cref="SteamJoinFlow"/>: refreshes on open, on the
    /// button and every few seconds, and turns a chosen game (or an accepted invite) into a route.
    /// </summary>
    public sealed class LobbyListController
    {
        private const float AutoRefreshSeconds = 5f;

        private readonly HostJoinController view;
        private readonly SteamJoinFlow flow;
        private float timer;
        private int generation;

        /// <summary>Raised with the host's route and a label to show while connecting.</summary>
        public event Action<ConnectionRoute, string> RouteChosen;

        public LobbyListController(HostJoinController view, SteamJoinFlow flow)
        {
            this.view = view;
            this.flow = flow;
            view.RefreshClicked += Refresh;
            view.JoinGameClicked += g => Join(g.LobbyId, g.HostName);
        }

        /// <summary>Asks Steam for the lobby list and shows it.</summary>
        public void Refresh()
        {
            timer = AutoRefreshSeconds;
            if (!SteamServices.Available) { view.ShowSteamOff(); return; }
            int mine = ++generation;
            flow.Refresh(shown => { if (mine == generation) view.ShowGames(shown); });
        }

        /// <summary>Call every frame while the Play screen is open.</summary>
        public void Tick(float deltaTime)
        {
            timer -= deltaTime;
            if (timer <= 0f) Refresh();
        }

        /// <summary>Joins a friend's lobby (also used for accepted invites).</summary>
        public void Join(ulong lobbyId, string label)
        {
            view.ShowFriendsNotice(null);
            flow.JoinLobby(lobbyId, route =>
            {
                if (route == null) { view.ShowFriendsNotice("Game full or already started."); return; }
                RouteChosen?.Invoke(route.Value, label ?? "the host");
            });
        }
    }
}

using WAR2D.Dev.Logging;
namespace WAR2D.Net.Steam
{
    /// <summary>Keeps the host's Steam lobby listing in step with the game lobby.</summary>
    public sealed class SteamLobbyService
    {
        private readonly ISteamBackend backend;
        private string hostName;
        private LobbyInfo last;
        private bool open;

        public SteamLobbyService(ISteamBackend backend) { this.backend = backend; }

        /// <summary>The Steam lobby id, 0 until Steam has created it.</summary>
        public ulong LobbyId { get; private set; }

        /// <summary>Creates the Steam lobby and publishes the first listing.</summary>
        public void Open(string hostName, MatchSettings settings, int players)
        {
            this.hostName = hostName;
            backend.CreateLobby(LobbyRules.MaxPlayers, id =>
            {
                if (id == 0) { WarLog.Warn(LogCategory.Steam, "could not create a lobby."); return; }
                LobbyId = id;
                open = true;
                last = default;
                Push(GameState.Lobby, settings, players);
            });
        }

        /// <summary>Pushes changed metadata only; closes the lobby to joiners once the game leaves <see cref="GameState.Lobby"/>.</summary>
        public void Update(GameState state, MatchSettings settings, int players)
        {
            if (open) Push(state, settings, players);
        }

        private void Push(GameState state, MatchSettings settings, int players)
        {
            bool joinable = state == GameState.Lobby;
            LobbyInfo now = new LobbyInfo
            {
                LobbyId = LobbyId, HostSteamId = backend.LocalSteamId, HostName = hostName, Version = GameVersion.Current,
                State = joinable ? "lobby" : "playing", Mode = settings.Mode.ToString(), MapSize = settings.MapSize, Players = players,
            };
            if (now.Equals(last)) return;
            bool firstPush = last.LobbyId == 0;
            if (firstPush || last.State != now.State) backend.SetLobbyJoinable(LobbyId, joinable);
            backend.SetLobbyData(LobbyId, now.Encode());
            backend.SetRichPresence(joinable ? "In a lobby" : "In a match");
            last = now;
        }

        /// <summary>Leaves the Steam lobby (once).</summary>
        public void Close()
        {
            if (!open) return;
            open = false;
            backend.LeaveLobby(LobbyId);
            backend.SetRichPresence(null);
        }
    }

    /// <summary>The one lobby service of a hosting process; it outlives the menu scene and ends with the server.</summary>
    public static class SteamHostLobby
    {
        public static SteamLobbyService Current { get; private set; }

        public static void Open(string hostName, MatchSettings settings, int players)
        {
            Close();
            if (!SteamServices.Available) return;
            Current = new SteamLobbyService(SteamServices.Backend);
            Current.Open(hostName, settings, players);
        }

        /// <summary>True once Steam has created the lobby, so friends can be invited to it.</summary>
        public static bool CanInvite => Current != null && Current.LobbyId != 0 && SteamServices.Available;

        /// <summary>Opens Steam's "invite friends" dialog for the hosted lobby.</summary>
        public static void OpenInvite()
        {
            if (CanInvite) SteamServices.Backend.OpenInviteDialog(Current.LobbyId);
        }

        public static void Close()
        {
            Current?.Close();
            Current = null;
        }
    }
}

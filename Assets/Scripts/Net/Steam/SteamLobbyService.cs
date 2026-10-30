using WAR2D.Dev.Logging;
namespace WAR2D.Net.Steam
{
    /// <summary>
    /// Keeps the host's Steam lobby in step with the game lobby: its listing, and (v0.14) its existence and type from
    /// <see cref="MatchSettings.Visibility"/>: Public or Friends opens a lobby of that type (or retypes the open one),
    /// Private closes it (a Private match has none).
    /// </summary>
    public sealed class SteamLobbyService
    {
        private readonly ISteamBackend backend;
        private string hostName;
        private LobbyInfo last;
        private bool open, creating, closed;
        /// <summary>The type of the open lobby (or of the one being created).</summary>
        private LobbyVisibility type;
        /// <summary>Counts creations, so an answer for a lobby no longer wanted is left at once.</summary>
        private int generation;
        /// <summary>The type whose creation Steam refused: not asked again until the type changes.</summary>
        private LobbyVisibility? failed;
        private GameState lastState = GameState.Lobby;
        private MatchSettings lastSettings;
        private int lastPlayers;

        public SteamLobbyService(ISteamBackend backend) { this.backend = backend; }

        /// <summary>The Steam lobby id, 0 while there is none (not yet created, or Private).</summary>
        public ulong LobbyId { get; private set; }

        /// <summary>True while a Steam lobby is open.</summary>
        public bool IsOpen => open;

        /// <summary>Starts keeping the lobby for <paramref name="hostName"/>: creates it unless the match is Private.</summary>
        public void Open(string hostName, MatchSettings settings, int players)
        {
            this.hostName = hostName;
            closed = false;
            Update(GameState.Lobby, settings, players);
        }

        /// <summary>
        /// Follows the game lobby: opens, retypes or closes the Steam lobby on a Visibility change, and pushes changed
        /// metadata only; closes the lobby to joiners once the game leaves <see cref="GameState.Lobby"/>.
        /// </summary>
        public void Update(GameState state, MatchSettings settings, int players)
        {
            if (closed) return;
            lastState = state;
            lastSettings = settings;
            lastPlayers = players;
            LobbyVisibility wanted = settings.Visibility;
            if (wanted == LobbyVisibility.Private)
            {
                failed = null;
                Leave();
                return;
            }
            if (failed.HasValue && failed.Value != wanted) failed = null;
            if (!open && !creating)
            {
                if (failed.HasValue) return;
                Create(wanted);
                return;
            }
            if (open && type != wanted)
            {
                backend.SetLobbyType(LobbyId, wanted);
                type = wanted;
            }
            if (open) Push(state, settings, players);
        }

        private void Create(LobbyVisibility visibility)
        {
            creating = true;
            type = visibility;
            int mine = ++generation;
            backend.CreateLobby(LobbyRules.MaxPlayers, visibility, id =>
            {
                if (mine != generation)
                {
                    // Went Private (or closed) while Steam was creating it: nobody may find it.
                    if (id != 0) backend.LeaveLobby(id);
                    return;
                }
                creating = false;
                if (id == 0) { failed = visibility; WarLog.Warn(LogCategory.Steam, "could not create a lobby."); return; }
                LobbyId = id;
                open = true;
                last = default;
                if (type != visibility) backend.SetLobbyType(id, type);
                Push(lastState, lastSettings, lastPlayers);
            });
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

        /// <summary>Leaves the open lobby (or drops one being created); the service may open another later.</summary>
        private void Leave()
        {
            if (creating) { creating = false; generation++; }
            if (!open) return;
            open = false;
            backend.LeaveLobby(LobbyId);
            backend.SetRichPresence(null);
            LobbyId = 0;
            last = default;
        }

        /// <summary>Leaves the Steam lobby (once) and stops following the game lobby.</summary>
        public void Close()
        {
            closed = true;
            Leave();
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

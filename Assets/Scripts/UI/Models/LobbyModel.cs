using System;
using System.Collections.Generic;
using Mirror;

namespace WAR2D.UI
{
    /// <summary>One player in the lobby: public lobby data only.</summary>
    public readonly struct LobbyPlayer : IEquatable<LobbyPlayer>
    {
        public readonly int OwnerId;
        public readonly string Nickname;
        public readonly int ColourIndex;
        /// <summary>The lobby team choice (<see cref="TeamRules.NoTeam"/> = Solo).</summary>
        public readonly int Team;
        public readonly bool Ready, IsHost;
        /// <summary>The HQ clearing claimed, or <see cref="LobbyRules.NoStartSite"/>.</summary>
        public readonly int StartSite;

        public LobbyPlayer(int ownerId, string nickname, int colourIndex, int team, bool ready, bool isHost, int startSite = LobbyRules.NoStartSite)
        {
            StartSite = startSite;
            OwnerId = ownerId;
            Nickname = nickname;
            ColourIndex = colourIndex;
            Team = team;
            Ready = ready;
            IsHost = isHost;
        }

        public bool Equals(LobbyPlayer o) => OwnerId == o.OwnerId && Nickname == o.Nickname && ColourIndex == o.ColourIndex && Team == o.Team && Ready == o.Ready && IsHost == o.IsHost && StartSite == o.StartSite;
        public override bool Equals(object obj) => obj is LobbyPlayer o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(OwnerId, Nickname, ColourIndex, Team, Ready, IsHost, StartSite);
    }

    /// <summary>
    /// What the lobby screen shows: the players (in join order), the host's settings, who the local player
    /// is, and whether the match can start. Filled from public SyncVars only. Raises <see cref="Changed"/>
    /// only when something shown changed.
    /// </summary>
    public sealed class LobbyModel
    {
        private readonly List<LobbyPlayer> players = new List<LobbyPlayer>();
        private readonly List<LobbyPlayer> scratch = new List<LobbyPlayer>();
        private readonly List<(int, bool, int)> startState = new List<(int, bool, int)>();

        public IReadOnlyList<LobbyPlayer> Players => players;
        public MatchSettings Settings { get; private set; }
        public int LocalOwnerId { get; private set; }

        /// <summary>True when the local player is the host (may change settings and start).</summary>
        public bool LocalIsHost
        {
            get
            {
                foreach (LobbyPlayer p in players) if (p.OwnerId == LocalOwnerId) return p.IsHost;
                return false;
            }
        }

        /// <summary>The local player's own row (default when not in the lobby).</summary>
        public LobbyPlayer Local
        {
            get
            {
                foreach (LobbyPlayer p in players) if (p.OwnerId == LocalOwnerId) return p;
                return default;
            }
        }

        public event Action Changed;

        /// <summary>Replaces what is shown.</summary>
        public void Set(IReadOnlyList<LobbyPlayer> rows, MatchSettings settings, int localOwnerId)
        {
            bool same = rows.Count == players.Count && settings.Equals(Settings) && localOwnerId == LocalOwnerId;
            for (int i = 0; same && i < rows.Count; i++) same = rows[i].Equals(players[i]);
            if (same) return;
            players.Clear();
            for (int i = 0; i < rows.Count; i++) players.Add(rows[i]);
            Settings = settings;
            LocalOwnerId = localOwnerId;
            Changed?.Invoke();
        }

        /// <summary>True when every player is ready and they form at least two teams.</summary>
        public bool CanStart => LobbyRules.CanStart(StartState());

        /// <summary>
        /// The footer's status line: for the host, who isn't ready yet or that a second team is needed;
        /// for everyone else, that the host starts the match.
        /// </summary>
        public string Status
        {
            get
            {
                if (!LocalIsHost) return "Waiting for the host to start";
                var waiting = new List<string>();
                foreach (LobbyPlayer p in players) if (!p.Ready) waiting.Add(p.OwnerId == LocalOwnerId ? "you" : p.Nickname);
                if (waiting.Count > 0) return $"Waiting for {Join(waiting)} to be ready";
                if (LobbyRules.Teams(StartState()) < 2) return "Needs at least two players or teams";
                return "Everyone is ready";
            }
        }

        /// <summary>Taken colours, other than the local player's.</summary>
        public bool IsColourTaken(int colour)
        {
            foreach (LobbyPlayer p in players) if (p.OwnerId != LocalOwnerId && p.ColourIndex == colour) return true;
            return false;
        }

        /// <summary>The player who claimed this clearing, if anyone has.</summary>
        public bool TryClaimant(int site, out LobbyPlayer claimant)
        {
            foreach (LobbyPlayer p in players)
                if (p.StartSite == site && site != LobbyRules.NoStartSite) { claimant = p; return true; }
            claimant = default;
            return false;
        }

        /// <summary>Reads the lobby from every player's public SyncVars and <c>GameCore.Settings</c>.</summary>
        public void Pull()
        {
            GameCore core = GameCore.Instance;
            if (core == null || NetworkClient.localPlayer == null) return;
            scratch.Clear();
            foreach (NetworkIdentity identity in NetworkClient.spawned.Values)
            {
                if (identity == null || !identity.TryGetComponent(out ClientPlayer p)) continue;
                scratch.Add(new LobbyPlayer((int)identity.netId, string.IsNullOrEmpty(p.nickname) ? "Player" : p.nickname, p.colourIndex, p.lobbyTeam, p.ready, p.isServerOwner, p.startSite));
            }
            scratch.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId)); // join order
            Set(scratch, core.Settings, (int)NetworkClient.localPlayer.netId);
        }

        private List<(int, bool, int)> StartState()
        {
            startState.Clear();
            foreach (LobbyPlayer p in players) startState.Add((p.OwnerId, p.Ready, p.Team));
            return startState;
        }

        private static string Join(List<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
    }
}

using System;
using System.Collections.Generic;
using Mirror;

namespace WAR2D.UI
{
    /// <summary>One player in the HUD's player list: public lobby data only.</summary>
    public readonly struct PlayerRow : IEquatable<PlayerRow>
    {
        public readonly int OwnerId;
        public readonly string Nickname;
        /// <summary>Index into the palette's player colours (the colour they picked in the lobby).</summary>
        public readonly int ColourIndex;
        /// <summary>The owner's place in <c>GameCore.PlayerOrder</c> (their bit in the diplomacy masks).</summary>
        public readonly int OrderIndex;
        public readonly int StartTeam;
        public readonly bool Eliminated;

        public PlayerRow(int ownerId, string nickname, int colourIndex, int startTeam, bool eliminated, int orderIndex = -1)
        {
            OwnerId = ownerId;
            Nickname = nickname;
            ColourIndex = colourIndex;
            OrderIndex = orderIndex < 0 ? colourIndex : orderIndex;
            StartTeam = startTeam;
            Eliminated = eliminated;
        }

        public bool Equals(PlayerRow other) => OwnerId == other.OwnerId && Nickname == other.Nickname &&
            ColourIndex == other.ColourIndex && OrderIndex == other.OrderIndex && StartTeam == other.StartTeam && Eliminated == other.Eliminated;

        public override bool Equals(object obj) => obj is PlayerRow other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(OwnerId, Nickname, ColourIndex, OrderIndex, StartTeam, Eliminated);
    }

    /// <summary>
    /// Everything the HUD shows, filled only from data this client receives: the local player's own
    /// resources (TargetRpc) and public player data (SyncVars). Raises one event per field group, and
    /// only when a value changed. The only HUD type that reads <see cref="ClientPlayer"/> and
    /// <see cref="GameCore"/>; controllers read the model.
    /// </summary>
    public sealed class HudModel
    {
        private readonly List<PlayerRow> players = new List<PlayerRow>();
        private readonly List<PlayerRow> scratch = new List<PlayerRow>();

        /// <summary>The local player's resources.</summary>
        public float Resources { get; private set; }

        /// <summary>The local player's income during the last second.</summary>
        public float IncomePerSecond { get; private set; }

        /// <summary>The local player's upkeep during the last second.</summary>
        public float UpkeepPerSecond { get; private set; }

        /// <summary>Every player in match order.</summary>
        public IReadOnlyList<PlayerRow> Players => players;

        /// <summary>Raised when the local player's resources, income or upkeep changed.</summary>
        public event Action ResourcesChanged;

        /// <summary>Raised when the player list changed.</summary>
        public event Action PlayersChanged;

        /// <summary>Sets the local player's own numbers.</summary>
        public void SetResources(float resources, float incomePerSecond, float upkeepPerSecond)
        {
            if (resources == Resources && incomePerSecond == IncomePerSecond && upkeepPerSecond == UpkeepPerSecond) return;
            Resources = resources;
            IncomePerSecond = incomePerSecond;
            UpkeepPerSecond = upkeepPerSecond;
            ResourcesChanged?.Invoke();
        }

        /// <summary>Replaces the player list.</summary>
        public void SetPlayers(IReadOnlyList<PlayerRow> rows)
        {
            bool same = rows.Count == players.Count;
            for (int i = 0; same && i < rows.Count; i++) same = rows[i].Equals(players[i]);
            if (same) return;
            players.Clear();
            for (int i = 0; i < rows.Count; i++) players.Add(rows[i]);
            PlayersChanged?.Invoke();
        }

        /// <summary>The local player's owner id (0 before it spawns).</summary>
        public int LocalOwnerId { get; private set; }

        /// <summary>True when the match lets players change diplomacy.</summary>
        public bool DiplomacyEnabled { get; private set; }

        /// <summary>Whom the local player attacks; bit i is the player at <c>GameCore.PlayerOrder[i]</c> (their <see cref="PlayerRow.ColourIndex"/>).</summary>
        public ushort AttackMask { get; private set; }

        /// <summary>Whom the local player shares vision with (bits as <see cref="AttackMask"/>).</summary>
        public ushort ShareMask { get; private set; }

        /// <summary>Who shares vision with the local player (bits as <see cref="AttackMask"/>).</summary>
        public ushort SharedWithMe { get; private set; }

        /// <summary>Raised when the local player's diplomacy row or the diplomacy setting changed.</summary>
        public event Action DiplomacyChanged;

        /// <summary>Sets the local player's own diplomacy row.</summary>
        public void SetDiplomacy(int localOwnerId, bool enabled, ushort attack, ushort share, ushort sharedWithMe)
        {
            if (localOwnerId == LocalOwnerId && enabled == DiplomacyEnabled && attack == AttackMask && share == ShareMask && sharedWithMe == SharedWithMe) return;
            LocalOwnerId = localOwnerId;
            DiplomacyEnabled = enabled;
            AttackMask = attack;
            ShareMask = share;
            SharedWithMe = sharedWithMe;
            DiplomacyChanged?.Invoke();
        }

        /// <summary>Reads the local player's diplomacy row (<see cref="ClientPlayer.TargetDiplomacy"/>) and the match's diplomacy setting.</summary>
        public void PullDiplomacy()
        {
            ClientPlayer local = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;
            if (local == null || GameCore.Instance == null) return;
            SetDiplomacy((int)local.netId, GameCore.Instance.DiplomacyEnabled, local.AttackMask, local.ShareVisionMask, local.SharedWithMe);
        }

        /// <summary>Reads the local player's resources from its <see cref="ClientPlayer"/>.</summary>
        public void PullResources()
        {
            ClientPlayer local = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;
            if (local == null) return;
            SetResources(local.currentResources, local.incomePerSecond, local.upkeepPerSecond);
        }

        /// <summary>Reads the player list from <c>GameCore.PlayerOrder</c>, the starting teams and each player's SyncVars.</summary>
        public void PullPlayers()
        {
            GameCore core = GameCore.Instance;
            if (core == null) return;
            scratch.Clear();
            for (int i = 0; i < core.PlayerOrder.Count; i++)
            {
                int owner = core.PlayerOrder[i];
                ClientPlayer player = null;
                if (NetworkClient.spawned.TryGetValue((uint)owner, out NetworkIdentity identity) && identity != null)
                    player = identity.GetComponent<ClientPlayer>();
                // A player who left has no object; one whose HQ fell lost hasPlacedHQ while Playing.
                bool eliminated = player == null || (core.CurrentState == GameState.Playing && !player.hasPlacedHQ);
                string name = player != null && !string.IsNullOrEmpty(player.nickname) ? player.nickname : "Player";
                int colour = player != null ? player.colourIndex : i;
                scratch.Add(new PlayerRow(owner, name, colour, core.TeamOf(owner), eliminated, i));
            }
            SetPlayers(scratch);
        }
    }
}

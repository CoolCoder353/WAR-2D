using System.Collections.Generic;

namespace WAR2D.Net.Steam
{
    /// <summary>Which lobbies the browser lists: open, not full, same build; busiest first.</summary>
    public static class LobbyListModel
    {
        public static List<LobbyInfo> Visible(IEnumerable<LobbyInfo> all, string myVersion)
        {
            var shown = new List<LobbyInfo>();
            foreach (LobbyInfo l in all)
                if (l.State == "lobby" && l.Players < LobbyRules.MaxPlayers && GameVersion.IsCompatible(l.Version, myVersion)) shown.Add(l);
            shown.Sort((a, b) => a.Players != b.Players ? b.Players.CompareTo(a.Players) : string.CompareOrdinal(a.HostName, b.HostName));
            return shown;
        }
    }
}

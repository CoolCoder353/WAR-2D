using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.UI
{
    /// <summary>One alert in the feed.</summary>
    public sealed class AlertEntry
    {
        public int Id;
        public AlertKind Kind;
        public string Text;
        public int2 Tile;
        public float Expires;
    }

    /// <summary>
    /// The alert feed: the newest alerts (at most <see cref="MaxShown"/>), each shown until dismissed or
    /// <c>Alerts/ShowSeconds</c> pass. UnderAttack alerts also ask for a minimap ping.
    /// </summary>
    public sealed class AlertFeedModel
    {
        /// <summary>Alerts shown at once; older ones drop off.</summary>
        public const int MaxShown = 4;

        private readonly List<AlertEntry> entries = new List<AlertEntry>();
        private readonly float showSeconds;
        private int nextId = 1;

        public AlertFeedModel(float showSeconds) => this.showSeconds = showSeconds;

        /// <summary>Shown alerts, oldest first.</summary>
        public IReadOnlyList<AlertEntry> Entries => entries;

        /// <summary>Raised when the shown alerts changed.</summary>
        public event Action Changed;

        /// <summary>Raised for an UnderAttack alert: ping this tile on the minimap.</summary>
        public event Action<int2> PingRequested;

        /// <summary>Adds an alert with its text, shown from <paramref name="now"/>.</summary>
        public void Add(in Alert alert, string text, float now)
        {
            entries.Add(new AlertEntry { Id = nextId++, Kind = alert.Kind, Text = text, Tile = alert.Tile, Expires = now + showSeconds });
            while (entries.Count > MaxShown) entries.RemoveAt(0);
            if (alert.Kind == AlertKind.UnderAttack) PingRequested?.Invoke(alert.Tile);
            Changed?.Invoke();
        }

        /// <summary>Removes an alert (its × button).</summary>
        public void Dismiss(int id)
        {
            if (entries.RemoveAll(e => e.Id == id) > 0) Changed?.Invoke();
        }

        /// <summary>Removes alerts whose time is up.</summary>
        public void Expire(float now)
        {
            if (entries.RemoveAll(e => e.Expires <= now) > 0) Changed?.Invoke();
        }

        /// <summary>
        /// The feed text for an alert. <paramref name="nameOf"/> gives a player's public nickname;
        /// <paramref name="nearBuilding"/> names the receiver's own building nearest the attack, or null.
        /// No text ever names an attacker.
        /// </summary>
        public static string Text(in Alert alert, Func<int, string> nameOf, string nearBuilding) => alert.Kind switch
        {
            AlertKind.UnderAttack => nearBuilding != null ? $"Under attack near your {nearBuilding}" : "Your units are under attack",
            AlertKind.UpkeepUnpaid => "Upkeep unpaid: units are decaying",
            AlertKind.GiftReceived => $"{nameOf(alert.OtherOwnerId)} gifted you {alert.Amount:N0}",
            AlertKind.VisionSharedWithYou => $"{nameOf(alert.OtherOwnerId)} shares vision with you",
            AlertKind.VisionUnshared => $"{nameOf(alert.OtherOwnerId)} stopped sharing vision with you",
            _ => "",
        };

        /// <summary>A building type as players know it.</summary>
        public static string BuildingName(BuildingType type) => type switch
        {
            BuildingType.Base => "HQ",
            BuildingType.SmallUnitSpawner => "Spawner",
            BuildingType.Miner => "Miner",
            _ => null,
        };
    }
}

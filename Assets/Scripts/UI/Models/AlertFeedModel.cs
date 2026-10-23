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
        public float Added;
        public float Expires;
    }

    /// <summary>
    /// The alert feed: the newest alerts (at most <see cref="MaxShown"/>), each shown until dismissed or
    /// <c>Alerts/ShowSeconds</c> pass. UnderAttack, BombTriggered and EnemySpotted alerts also ask for a minimap ping.
    /// </summary>
    public sealed class AlertFeedModel
    {
        /// <summary>Alerts shown at once; older ones drop off.</summary>
        public const int MaxShown = 4;

        /// <summary>How long a new alert keeps its border in its stripe colour (Figma <c>HUD – alerts and squad hint</c>).</summary>
        public const float HighlightSeconds = 3f;

        private readonly List<AlertEntry> entries = new List<AlertEntry>();
        private readonly float showSeconds;
        private int nextId = 1;

        public AlertFeedModel(float showSeconds) => this.showSeconds = showSeconds;

        /// <summary>Shown alerts, oldest first.</summary>
        public IReadOnlyList<AlertEntry> Entries => entries;

        /// <summary>Raised when the shown alerts changed.</summary>
        public event Action Changed;

        /// <summary>Raised for an alert the minimap pings: its tile and kind (the ring's colour).</summary>
        public event Action<int2, AlertKind> PingRequested;

        /// <summary>True for the alerts the minimap pings.</summary>
        public static bool Pings(AlertKind kind) => kind == AlertKind.UnderAttack || kind == AlertKind.BombTriggered || kind == AlertKind.EnemySpotted;

        /// <summary>Adds an alert with its text, shown from <paramref name="now"/>.</summary>
        public void Add(in Alert alert, string text, float now)
        {
            entries.Add(new AlertEntry { Id = nextId++, Kind = alert.Kind, Text = text, Tile = alert.Tile, Added = now, Expires = now + showSeconds });
            while (entries.Count > MaxShown) entries.RemoveAt(0);
            if (Pings(alert.Kind)) PingRequested?.Invoke(alert.Tile, alert.Kind);
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
            AlertKind.ConstructionFinished => nearBuilding != null ? $"Construction finished: {nearBuilding}" : "Construction finished",
            AlertKind.BombTriggered => "Bomb triggered",
            AlertKind.EnemySpotted => "Enemy spotted by Radar",
            AlertKind.RepairsStopped => "Out of resources: repairs stopped",
            _ => "",
        };

        /// <summary>A building type as players know it.</summary>
        public static string BuildingName(BuildingType type) => type switch
        {
            BuildingType.Base => "HQ",
            BuildingType.Barracks => "Barracks",
            BuildingType.HeavyFactory => "Heavy Factory",
            BuildingType.SiegeWorkshop => "Siege Workshop",
            BuildingType.Miner => "Miner",
            BuildingType.Bomb => "Bomb",
            BuildingType.Radar => "Radar",
            BuildingType.GunTurret => "Gun turret",
            BuildingType.Jammer => "Jammer",
            _ => null,
        };

        /// <summary>A unit type as players know it: its name with spaces ("Suicide bomber").</summary>
        public static string UnitName(UnitType type) => Spaced(type.ToString());

        /// <summary>A PascalCase name with spaces, later words in lower case ("SiegeWorkshop" → "Siege workshop").</summary>
        public static string Spaced(string name)
        {
            var text = new System.Text.StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && char.IsUpper(c)) text.Append(' ').Append(char.ToLowerInvariant(c));
                else text.Append(c);
            }
            return text.ToString();
        }
    }
}

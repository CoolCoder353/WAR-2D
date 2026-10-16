using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WAR2D.Net.Replay;

namespace WAR2D.UI
{
    /// <summary>One row of the Replays list, formatted for display.</summary>
    public sealed class ReplayRow
    {
        public string Path, DateText, SummaryText, DurationText, SizeText;
        /// <summary>The players' names and colour indices, in the recording's order.</summary>
        public (string Name, int Colour)[] Players;
    }

    /// <summary>The Replays screen's data: the local Replays folder as rows, newest first.</summary>
    public sealed class ReplaysModel
    {
        private readonly Func<IReadOnlyList<ReplaySummary>> source;

        public IReadOnlyList<ReplayRow> Rows { get; private set; } = Array.Empty<ReplayRow>();
        public bool Empty => Rows.Count == 0;
        public event Action Changed;

        /// <summary>Reads the Replays folder, or <paramref name="source"/> (tests).</summary>
        public ReplaysModel(Func<IReadOnlyList<ReplaySummary>> source = null) { this.source = source ?? ReplayLibrary.List; }

        public void Refresh()
        {
            Rows = source().OrderByDescending(s => s.Utc).Select(ToRow).ToList();
            Changed?.Invoke();
        }

        public static ReplayRow ToRow(ReplaySummary s)
        {
            ReplayHeader h = s.Header ?? new ReplayHeader();
            var players = (h.Players ?? new List<ReplayPlayerInfo>()).Select(p => (string.IsNullOrEmpty(p.Nickname) ? "Player" : p.Nickname, p.Colour)).ToArray();
            string mode = h.Settings.Mode == MatchMode.Teams ? "Teams" : "Free for all";
            return new ReplayRow
            {
                Path = s.Path,
                DateText = s.Utc.ToLocalTime().ToString("d MMM yyyy · HH:mm", CultureInfo.InvariantCulture),
                SummaryText = $"Map {h.MapSize} · {mode}" + (players.Length > 0 ? " · " + string.Join(", ", players.Select(p => p.Item1)) : ""),
                DurationText = Duration(s.DurationSeconds),
                SizeText = Size(s.Bytes),
                Players = players,
            };
        }

        /// <summary><c>m:ss</c> (or <c>h:mm:ss</c>); <c>–:––</c> when unknown.</summary>
        public static string Duration(float seconds)
        {
            if (seconds < 0f || float.IsNaN(seconds)) return "–:––";
            int total = (int)Math.Round(seconds);
            return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60}:{total % 60:00}";
        }

        /// <summary><c>x.y MB</c>.</summary>
        public static string Size(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }
}

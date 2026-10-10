using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>How the match ended for the local player.</summary>
    public enum EndResult { Victory, Defeat, Draw }

    /// <summary>
    /// Binds the end screen (<c>Assets/UI/Menus/EndScreen.uxml</c>, shown in the match): Victory, Defeat or
    /// Draw, who won and how long it took, and the statistics table once the server sends it at game over.
    /// </summary>
    public sealed class EndScreenController
    {
        private static readonly string[] Columns = { "Units built", "Lost", "Killed", "Buildings", "Lost", "Mined", "Spent", "Gifted out", "Gifted in", "Peak army" };
        private readonly VisualElement screen, stats;
        private readonly Label title, subtitle;

        public event Action MainMenuClicked, QuitClicked;

        public EndScreenController(VisualElement root)
        {
            screen = root.Q("end-screen");
            stats = root.Q("end-stats");
            title = root.Q<Label>("end-title");
            subtitle = root.Q<Label>("end-subtitle");
            root.Q<Button>("end-main-menu").clicked += () => MainMenuClicked?.Invoke();
            root.Q<Button>("end-quit").clicked += () => QuitClicked?.Invoke();
        }

        public bool IsShown => screen.ClassListContains("overlay-screen--visible");

        /// <summary>Shows the result (the table follows when the statistics arrive).</summary>
        public void Show(EndResult result, string line)
        {
            title.text = result == EndResult.Victory ? "Victory" : result == EndResult.Defeat ? "Defeat" : "Draw";
            title.EnableInClassList("end-screen__title--lose", result == EndResult.Defeat);
            title.EnableInClassList("end-screen__title--draw", result == EndResult.Draw);
            subtitle.text = line;
            screen.AddToClassList("overlay-screen--visible");
            stats.EnableInClassList("hidden", stats.childCount == 0);
        }

        /// <summary>Fills the statistics table: one row per player, with their result.</summary>
        public void ShowStats(MatchResult result, Func<int, string> nameOf, Func<int, int> colourOf)
        {
            stats.Clear();
            var header = new VisualElement();
            header.AddToClassList("stats-row");
            header.AddToClassList("stats-row--header");
            header.Add(Cell("Player", "stats-cell__head", player: true));
            for (int c = 0; c < Columns.Length; c++) header.Add(Cell(Columns[c], "stats-cell__head", narrow: c == 1 || c == 2 || c == 4));
            stats.Add(header);
            var winners = new HashSet<int>(result.Winners ?? new int[0]);
            for (int i = 0; i < result.Owners.Length && i < result.Stats.Length; i++)
            {
                int owner = result.Owners[i];
                PlayerStats s = result.Stats[i];
                var row = new VisualElement { name = "stats-" + owner };
                row.AddToClassList("stats-row");
                var who = new VisualElement();
                who.AddToClassList("stats-cell");
                who.AddToClassList("stats-cell--player");
                var swatch = new VisualElement();
                swatch.AddToClassList("swatch");
                swatch.AddToClassList("player-" + (colourOf(owner) % 8 + 1));
                var name = new Label(nameOf(owner));
                name.AddToClassList("stats-cell__name");
                bool won = winners.Contains(owner);
                var outcome = new Label(winners.Count == 0 ? "Draw" : won ? "Win" : "Lose");
                outcome.AddToClassList("stats-cell__result");
                outcome.EnableInClassList("stats-cell__result--win", won);
                who.Add(swatch);
                who.Add(name);
                who.Add(outcome);
                row.Add(who);
                string[] values =
                {
                    s.UnitsBuilt.ToString("N0"), s.UnitsLost.ToString("N0"), s.UnitsKilled.ToString("N0"), s.BuildingsBuilt.ToString("N0"),
                    s.BuildingsLost.ToString("N0"), s.Mined.ToString("N0"), s.Spent.ToString("N0"), s.GiftedOut.ToString("N0"),
                    s.GiftedIn.ToString("N0"), s.PeakArmy.ToString("N0"),
                };
                for (int c = 0; c < values.Length; c++) row.Add(Cell(values[c], "stats-cell__value", narrow: c == 1 || c == 2 || c == 4));
                stats.Add(row);
            }
            stats.RemoveFromClassList("hidden");
        }

        private static VisualElement Cell(string text, string labelClass, bool player = false, bool narrow = false)
        {
            var cell = new VisualElement();
            cell.AddToClassList("stats-cell");
            if (player) cell.AddToClassList("stats-cell--player");
            if (narrow) cell.AddToClassList("stats-cell--narrow");
            var label = new Label(text);
            label.AddToClassList(labelClass);
            cell.Add(label);
            return cell;
        }

        /// <summary>"Oscar and Claude win · 24:16", "You were eliminated · 12:03" or "Everyone fell together · 8:40".</summary>
        public static string Line(EndResult result, IReadOnlyList<string> winners, float seconds)
        {
            string time = $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";
            if (result == EndResult.Draw) return $"Everyone fell together · {time}";
            if (winners == null || winners.Count == 0) return result == EndResult.Defeat ? $"You were eliminated · {time}" : time;
            string names = winners.Count == 1 ? winners[0] : string.Join(", ", new List<string>(winners).GetRange(0, winners.Count - 1)) + " and " + winners[winners.Count - 1];
            return $"{names} {(winners.Count == 1 ? "wins" : "win")} · {time}";
        }
    }
}

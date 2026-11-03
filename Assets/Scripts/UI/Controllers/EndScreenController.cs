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
        private readonly Label title, subtitle, replayLabel;
        private readonly VisualElement replay, replayBar, replayFill, tabs, graphs, timeline, result;
        private SummaryChart chart;
        private int tab;

        public event Action MainMenuClicked, QuitClicked, RematchClicked;
        private readonly Button rematch, mainMenu, quit;
        private readonly Label rematchNote;
        private bool returning;

        public EndScreenController(VisualElement root)
        {
            screen = root.Q("end-screen");
            stats = root.Q("end-stats");
            title = root.Q<Label>("end-title");
            subtitle = root.Q<Label>("end-subtitle");
            replay = root.Q("replay-progress");
            replayLabel = root.Q<Label>("replay-progress-label");
            replayBar = root.Q("replay-progress-bar");
            replayFill = root.Q("replay-progress-fill");
            tabs = root.Q("end-tabs");
            graphs = root.Q("end-graphs");
            timeline = root.Q("end-timeline");
            result = screen.Q(className: "end-screen__result");
            mainMenu = root.Q<Button>("end-main-menu");
            quit = root.Q<Button>("end-quit");
            rematch = root.Q<Button>("end-rematch");
            rematchNote = root.Q<Label>("end-rematch-note");
            mainMenu.clicked += () => MainMenuClicked?.Invoke();
            quit.clicked += () => QuitClicked?.Invoke();
            rematch.clicked += () => RematchClicked?.Invoke();
        }

        /// <summary>Shows the Summary, Graphs and Timeline tabs (v0.18, Figma E1, E2) once the graphs and timeline arrive.</summary>
        public void ShowSummary(in SummaryData summary, Func<int, string> nameOf, Func<int, UnityEngine.Color32> colourOf, int local)
        {
            if (tabs == null) return;
            chart ??= new SummaryChart(graphs);
            chart.Show(summary, nameOf, colourOf, local);
            timeline.Clear();
            foreach (TimelineEntry entry in TimelineText.Entries(summary, nameOf, local)) timeline.Add(TimelineRow(entry, colourOf));
            result?.AddToClassList("end-screen__result--tabs");
            stats.AddToClassList("end-screen__stats--tabs");
            SelectTab(tab);
        }

        private void SelectTab(int chosen)
        {
            tab = chosen;
            tabs.Clear();
            string[] names = { "Summary", "Graphs", "Timeline" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                tabs.Add(Widgets.Option("end-tab-" + names[i].ToLowerInvariant(), names[i], i == tab, true, () => SelectTab(index)));
            }
            stats.EnableInClassList("hidden", tab != 0 || stats.childCount == 0);
            graphs.EnableInClassList("hidden", tab != 1);
            timeline.EnableInClassList("hidden", tab != 2);
        }

        private static VisualElement TimelineRow(in TimelineEntry entry, Func<int, UnityEngine.Color32> colourOf)
        {
            var row = new VisualElement();
            row.AddToClassList("timeline-row");
            var time = new Label(SummaryModel.Time(entry.Time));
            time.AddToClassList("timeline-row__time");
            var mark = new VisualElement();
            mark.AddToClassList("timeline-row__mark");
            mark.style.backgroundImage = new StyleBackground(WAR2D.Art.PixelIcons.Texture(entry.Fight ? "MarkFight" : "MarkElimination", 2));
            var text = new Label(entry.Text);
            text.AddToClassList("timeline-row__text");
            var players = new VisualElement();
            players.AddToClassList("timeline-row__players");
            foreach (int owner in entry.Owners)
            {
                var swatch = new VisualElement();
                swatch.AddToClassList("swatch");
                swatch.style.backgroundColor = (UnityEngine.Color)colourOf(owner);
                players.Add(swatch);
            }
            row.Add(time);
            row.Add(mark);
            row.Add(text);
            row.Add(players);
            return row;
        }

        /// <summary>
        /// The button row (v0.18, Figma E2). The host: Rematch (off while the replay saves), Main menu and Quit. Everyone else:
        /// Main menu and Quit, with "Waiting for the host…".
        /// </summary>
        public void ShowRematch(bool host, bool replayPending)
        {
            if (returning) return;
            rematch.EnableInClassList("hidden", !host);
            rematch.SetEnabled(!replayPending);
            mainMenu.EnableInClassList("button--primary", !host);
            rematchNote.text = "Waiting for the host…";
            rematchNote.EnableInClassList("hidden", host);
        }

        /// <summary>The host pressed Rematch: the screen says so until the scene changes.</summary>
        public void ShowReturning()
        {
            returning = true;
            rematch.AddToClassList("hidden");
            mainMenu.AddToClassList("hidden");
            quit.AddToClassList("hidden");
            rematchNote.text = "Returning to the lobby";
            rematchNote.RemoveFromClassList("hidden");
        }

        /// <summary>The replay line under the buttons: hidden for null text; the bar shows only with a fraction.</summary>
        public void ShowReplay(string text, float? fraction)
        {
            if (replay == null) return;
            replay.EnableInClassList("end-screen__replay--visible", text != null);
            if (text == null) return;
            replayLabel.text = text;
            replayBar.style.display = fraction.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
            if (fraction.HasValue) replayFill.style.width = Length.Percent(UnityEngine.Mathf.Clamp01(fraction.Value) * 100f);
        }

        /// <summary>The replay line's text and bar for the current state (only the host records, so a joined player has none).</summary>
        public static (string text, float? fraction) ReplayState(bool host, WAR2D.Net.Replay.MatchRecorder recorder)
        {
            const string saved = "Replay saved · watch it from Replays";
            if (host)
            {
                if (recorder == null) return (null, null);
                return recorder.Finished ? (saved, null) : ("Saving replay…", null);
            }
            return (null, null);
        }

        public bool IsShown => screen.ClassListContains("overlay-screen--visible");

        /// <summary>Shows the result (the table follows when the statistics arrive).</summary>
        public void Show(EndResult result, string line)
        {
            title.text = result == EndResult.Victory ? "Victory" : result == EndResult.Defeat ? "Defeat" : "Draw";
            // A short hit as the screen appears, while the music crossfades into the stinger under it.
            if (!IsShown) WAR2D.Audio.AudioService.Ui(result == EndResult.Victory ? "victory" : "defeat");
            WAR2D.Audio.AudioService.Music(result == EndResult.Victory ? WAR2D.Audio.MusicCue.Victory : WAR2D.Audio.MusicCue.Defeat);
            title.EnableInClassList("end-screen__title--lose", result == EndResult.Defeat);
            title.EnableInClassList("end-screen__title--draw", result == EndResult.Draw);
            subtitle.text = line;
            if (!IsShown) { MenuMotion.ApplySetting(screen.panel?.visualTree); MenuMotion.Enter(screen, "overlay-screen--enter"); } // v0.16 (Figma M1)
            screen.AddToClassList("overlay-screen--visible");
            stats.EnableInClassList("hidden", stats.childCount == 0);
        }

        /// <summary>Fills the statistics table: one row per player, with their result.</summary>
        /// <param name="tagOf">An AI's end-screen tag ("AI · Hard · Turtle (random)"); null for a human.</param>
        public void ShowStats(MatchResult result, Func<int, string> nameOf, Func<int, int> colourOf, Func<int, string> tagOf = null)
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
                VisualElement name = PlayerNameLabel.Build(nameOf(owner), tagOf?.Invoke(owner), "stats-cell__name");
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
            stats.EnableInClassList("hidden", tab != 0);
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

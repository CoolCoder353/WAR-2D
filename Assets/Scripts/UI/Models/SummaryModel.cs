using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.UI
{
    /// <summary>Which line chart the end screen's Graphs tab shows.</summary>
    public enum SummaryMetric { ArmyValue, Income, UnitsKilled, UnitsLost }

    /// <summary>The numbers behind the end screen's charts (v0.18, Figma E1, R1). Pure.</summary>
    public static class SummaryModel
    {
        /// <summary>The button text of each chart.</summary>
        public static string Title(SummaryMetric metric) => metric switch
        {
            SummaryMetric.ArmyValue => "Army value",
            SummaryMetric.Income => "Income (per minute)",
            SummaryMetric.UnitsKilled => "Units killed",
            _ => "Units lost",
        };

        /// <summary>One player's value at sample <paramref name="i"/>.</summary>
        public static float Value(in SummaryPlayer player, SummaryMetric metric, int i) => metric switch
        {
            SummaryMetric.ArmyValue => player.Army[i],
            SummaryMetric.Income => player.Income[i],
            SummaryMetric.UnitsKilled => player.Killed[i],
            _ => player.Lost[i],
        };

        /// <summary>The chart's top value: a round number at or over every player's peak.</summary>
        public static float NiceMax(in SummaryData data, SummaryMetric metric)
        {
            float peak = 1f;
            foreach (SummaryPlayer player in data.Players)
                for (int i = 0; i < data.Times.Length; i++) peak = math.max(peak, Value(player, metric, i));
            float unit = math.pow(10f, math.floor(math.log10(peak)));
            foreach (float step in new[] { 1f, 2f, 2.5f, 5f, 10f })
                if (step * unit >= peak) return step * unit;
            return 10f * unit;
        }

        /// <summary>The sample at or just before a time, or -1 before the first.</summary>
        public static int SampleAt(float[] times, float time)
        {
            int found = -1;
            for (int i = 0; i < times.Length && times[i] <= time; i++) found = i;
            return found;
        }

        /// <summary>The time a player's line ends: their elimination, or the match's end.</summary>
        public static float EndOf(in SummaryData data, int owner)
        {
            foreach (SummaryElimination e in data.Eliminations) if (e.Owner == owner) return e.Time;
            return data.Times.Length > 0 ? data.Times[data.Times.Length - 1] : 0f;
        }

        /// <summary>Each shown player's value at a time, highest first: the chart's hover read-out. Players already out are left off.</summary>
        public static List<(int owner, float value)> ReadOut(in SummaryData data, SummaryMetric metric, float time, ICollection<int> hidden)
        {
            var rows = new List<(int, float)>();
            int sample = SampleAt(data.Times, time);
            if (sample < 0) return rows;
            foreach (SummaryPlayer player in data.Players)
            {
                if (hidden != null && hidden.Contains(player.Owner) || time > EndOf(data, player.Owner)) continue;
                rows.Add((player.Owner, Value(player, metric, sample)));
            }
            rows.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return rows;
        }

        /// <summary>A value as the chart's axis and read-out show it: 12.5k, 800.</summary>
        public static string Short(float value)
        {
            if (value >= 1000f) return (value / 1000f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k";
            return value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Seconds as m:ss, as the end screen's other times read.</summary>
        public static string Time(float seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";
    }

    /// <summary>One line of the end screen's Timeline tab.</summary>
    public struct TimelineEntry
    {
        public float Time;
        public bool Fight;
        public string Text;
        public int[] Owners;
    }

    /// <summary>The Timeline tab's words (v0.18, Figma E2): eliminations and big fights in time order. Pure.</summary>
    public static class TimelineText
    {
        /// <summary>A fight this close to a player's HQ is "near their base".</summary>
        public const float NearBaseTiles = 24f;

        /// <summary>Every event in time order. <paramref name="local"/> reads as "you".</summary>
        public static List<TimelineEntry> Entries(in SummaryData data, Func<int, string> nameOf, int local)
        {
            var entries = new List<TimelineEntry>();
            foreach (SummaryElimination e in data.Eliminations)
                entries.Add(new TimelineEntry { Time = e.Time, Text = Elimination(e, nameOf, local), Owners = e.By != 0 ? new[] { e.Owner, e.By } : new[] { e.Owner } });
            foreach (SummaryFight f in data.Fights)
                entries.Add(new TimelineEntry { Time = f.Start, Fight = true, Text = Fight(f, data, nameOf, local), Owners = f.Owners });
            entries.Sort((a, b) => a.Time.CompareTo(b.Time));
            return entries;
        }

        private static string Who(int owner, Func<int, string> nameOf, int local) => owner == local ? "you" : nameOf(owner);

        private static string Elimination(in SummaryElimination e, Func<int, string> nameOf, int local)
        {
            string who = e.Owner == local ? "You" : nameOf(e.Owner);
            string be = e.Owner == local ? "are" : "is";
            return e.Cause switch
            {
                EliminationCause.Surrendered => $"{who} surrendered",
                EliminationCause.Left => $"{who} left",
                EliminationCause.TimedOut => $"{who} {be} out: connection timed out",
                _ => e.By != 0 && e.By != e.Owner ? $"{who} {be} out: HQ destroyed by {Who(e.By, nameOf, local)}" : $"{who} {be} out: HQ destroyed",
            };
        }

        private static string Fight(in SummaryFight f, in SummaryData data, Func<int, string> nameOf, int local)
        {
            var losses = new List<(int owner, float worth)>();
            for (int i = 0; i < f.Owners.Length; i++) if (f.WorthLost[i] > 0f) losses.Add((f.Owners[i], f.WorthLost[i]));
            losses.Sort((a, b) => b.worth.CompareTo(a.worth));
            var parts = new List<string>();
            foreach ((int owner, float worth) in losses)
                parts.Add($"{Who(owner, nameOf, local)} lost {worth:N0}");
            return $"Big fight {Place(f.CentreX, f.CentreY, data, nameOf, local)}, {SummaryModel.Time(f.End - f.Start)}: {string.Join(", ", parts)}";
        }

        /// <summary>Where on the map: near a player's base, else the middle or a compass point. Never coordinates.</summary>
        public static string Place(float x, float y, in SummaryData data, Func<int, string> nameOf, int local)
        {
            float best = NearBaseTiles * NearBaseTiles;
            int near = 0;
            for (int i = 0; i < data.HqOwners.Length; i++)
            {
                float dx = data.HqX[i] - x, dy = data.HqY[i] - y, d = dx * dx + dy * dy;
                if (d <= best) { best = d; near = data.HqOwners[i]; }
            }
            if (near != 0) return near == local ? "near your base" : $"near {nameOf(near)}'s base";
            if (data.MapSize <= 0) return "on the map";
            float third = data.MapSize / 3f;
            int column = x < third ? 0 : x < 2f * third ? 1 : 2, row = y < third ? 0 : y < 2f * third ? 1 : 2;
            if (column == 1 && row == 1) return "in the middle";
            string ns = row == 2 ? "north" : row == 0 ? "south" : "";
            string ew = column == 2 ? "east" : column == 0 ? "west" : "";
            return "in the " + (ns != "" && ew != "" ? ns + "-" + ew : ns + ew);
        }
    }
}

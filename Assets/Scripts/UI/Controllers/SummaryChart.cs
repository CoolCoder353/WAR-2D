using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// The end screen's line chart (v0.18, Figma E1; the replay viewer's too, R1): metric buttons, one line per player in their
    /// colour with the local player's thicker, a legend that toggles players (the axis doesn't rescale, so the other lines stay
    /// put), a hover line and read-out, and each elimination marked on the time axis. Drawn with <c>Painter2D</c>.
    /// </summary>
    public sealed class SummaryChart
    {
        private const float Left = 58f, Right = 12f, Top = 10f, Bottom = 26f;
        private static readonly string[] Skull = { ".#######.", "#########", "##..#..##", "##..#..##", "#########", ".###.###.", "..#.#.#.." };

        private readonly VisualElement root, chart, legend, metrics, readout, labels;
        private readonly Label readoutTime;
        private readonly VisualElement readoutRows;
        private readonly HashSet<int> hidden = new HashSet<int>();
        private SummaryData data;
        private SummaryMetric metric = SummaryMetric.ArmyValue;
        private Func<int, string> nameOf;
        private Func<int, Color32> colourOf;
        private int local;
        private float hoverTime = -1f;
        private float yMax = 1f;

        /// <summary>The playback time's line (the replay viewer), or negative for none.</summary>
        public float PlaybackTime { get => playback; set { if (Mathf.Approximately(playback, value)) return; playback = value; chart.MarkDirtyRepaint(); } }
        private float playback = -1f;

        /// <summary>Puts the chart inside <paramref name="parent"/>.</summary>
        public SummaryChart(VisualElement parent)
        {
            root = parent;
            metrics = new VisualElement { name = "summary-metrics" };
            metrics.AddToClassList("summary-metrics");
            root.Add(metrics);
            chart = new VisualElement { name = "summary-chart" };
            chart.AddToClassList("summary-chart");
            chart.generateVisualContent += Draw;
            chart.RegisterCallback<PointerMoveEvent>(e => Hover(e.localPosition.x));
            chart.RegisterCallback<PointerLeaveEvent>(_ => Hover(-1f));
            chart.RegisterCallback<GeometryChangedEvent>(_ => Layout());
            root.Add(chart);
            labels = new VisualElement { pickingMode = PickingMode.Ignore };
            labels.style.position = Position.Absolute;
            labels.style.left = 0; labels.style.top = 0; labels.style.right = 0; labels.style.bottom = 0;
            chart.Add(labels);
            readout = new VisualElement { name = "summary-readout", pickingMode = PickingMode.Ignore };
            readout.AddToClassList("summary-chart__readout");
            readoutTime = new Label();
            readoutTime.AddToClassList("summary-chart__readout-time");
            readout.Add(readoutTime);
            readoutRows = new VisualElement();
            readout.Add(readoutRows);
            readout.style.display = DisplayStyle.None;
            chart.Add(readout);
            legend = new VisualElement { name = "summary-legend" };
            legend.AddToClassList("summary-legend");
            root.Add(legend);
        }

        /// <summary>Shows a match's numbers.</summary>
        public void Show(in SummaryData summary, Func<int, string> names, Func<int, Color32> colours, int localOwner)
        {
            data = summary;
            nameOf = names;
            colourOf = colours;
            local = localOwner;
            hidden.Clear();
            BuildMetrics();
            BuildLegend();
            Layout();
        }

        private void BuildMetrics()
        {
            metrics.Clear();
            foreach (SummaryMetric m in Enum.GetValues(typeof(SummaryMetric)))
            {
                SummaryMetric chosen = m;
                metrics.Add(Widgets.Option("metric-" + m, SummaryModel.Title(m), m == metric, true, () =>
                {
                    metric = chosen;
                    BuildMetrics();
                    Layout();
                }));
            }
        }

        private void BuildLegend()
        {
            legend.Clear();
            if (data.Players == null) return;
            foreach (SummaryPlayer player in data.Players)
            {
                int owner = player.Owner;
                var entry = new VisualElement { name = "legend-" + owner };
                entry.AddToClassList("summary-legend__entry");
                entry.EnableInClassList("summary-legend__entry--off", hidden.Contains(owner));
                var swatch = new VisualElement();
                swatch.AddToClassList("swatch");
                swatch.style.backgroundColor = (Color)colourOf(owner);
                if (hidden.Contains(owner)) swatch.style.backgroundColor = StyleKeyword.Null;
                var name = new Label(nameOf(owner));
                name.AddToClassList("summary-legend__name");
                entry.Add(swatch);
                entry.Add(name);
                entry.RegisterCallback<ClickEvent>(_ => Toggle(owner));
                legend.Add(entry);
            }
        }

        private void Toggle(int owner)
        {
            if (!hidden.Remove(owner)) hidden.Add(owner);
            BuildLegend();
            chart.MarkDirtyRepaint();
            Hover(hoverTime < 0f ? -1f : XOf(hoverTime));
        }

        private float PlotWidth => Mathf.Max(1f, chart.resolvedStyle.width - Left - Right);
        private float PlotHeight => Mathf.Max(1f, chart.resolvedStyle.height - Top - Bottom);
        private float Duration => data.Times != null && data.Times.Length > 0 ? Mathf.Max(1f, data.Times[data.Times.Length - 1]) : 1f;
        private float XOf(float time) => Left + time / Duration * PlotWidth;
        private float YOf(float value) => Top + PlotHeight - Mathf.Min(1f, value / yMax) * PlotHeight;

        /// <summary>Axis labels and the top value, for the current size and metric.</summary>
        private void Layout()
        {
            labels.Clear();
            if (data.Times == null || data.Times.Length == 0 || float.IsNaN(chart.resolvedStyle.width)) return;
            yMax = SummaryModel.NiceMax(data, metric);
            for (int i = 0; i <= 4; i++)
            {
                float v = yMax * i / 4f;
                var label = new Label(SummaryModel.Short(v));
                label.AddToClassList("summary-chart__axis");
                label.style.left = 2f;
                label.style.top = YOf(v) - 8f;
                labels.Add(label);
            }
            float minutes = Duration / 60f, step = minutes <= 10f ? 60f : minutes <= 40f ? 300f : 600f;
            for (float t = 0f; t <= Duration; t += step)
            {
                var label = new Label(SummaryModel.Time(t));
                label.AddToClassList("summary-chart__time");
                label.style.left = XOf(t) - 14f;
                label.style.top = chart.resolvedStyle.height - Bottom + 6f;
                labels.Add(label);
            }
            chart.MarkDirtyRepaint();
        }

        private void Hover(float x)
        {
            if (x < Left || data.Times == null || data.Times.Length == 0)
            {
                hoverTime = -1f;
                readout.style.display = DisplayStyle.None;
                chart.MarkDirtyRepaint();
                return;
            }
            hoverTime = Mathf.Clamp((x - Left) / PlotWidth, 0f, 1f) * Duration;
            readout.style.display = DisplayStyle.Flex;
            readoutTime.text = SummaryModel.Time(hoverTime);
            readoutRows.Clear();
            foreach ((int owner, float value) in SummaryModel.ReadOut(data, metric, hoverTime, hidden))
            {
                var row = new VisualElement();
                row.AddToClassList("summary-readout-row");
                var swatch = new VisualElement();
                swatch.AddToClassList("swatch");
                swatch.style.backgroundColor = (Color)colourOf(owner);
                var name = new Label(nameOf(owner));
                name.AddToClassList("summary-readout-row__name");
                name.EnableInClassList("summary-readout-row__name--you", owner == local);
                var number = new Label(SummaryModel.Short(value));
                number.AddToClassList("summary-readout-row__value");
                row.Add(swatch);
                row.Add(name);
                row.Add(number);
                readoutRows.Add(row);
            }
            // Beside the line; near the right edge it flips to the left of it.
            const float ReadoutWidth = 190f;
            float at = XOf(hoverTime);
            readout.style.left = at + ReadoutWidth + 16f > chart.resolvedStyle.width ? at - ReadoutWidth - 8f : at + 8f;
            readout.style.top = Top + 8f;
            chart.MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            if (data.Times == null || data.Times.Length == 0) return;
            Painter2D p = context.painter2D;
            p.lineJoin = LineJoin.Round;
            p.lineWidth = 1f;
            p.strokeColor = new Color(0.204f, 0.251f, 0.298f);
            for (int i = 0; i <= 4; i++)
            {
                float y = YOf(yMax * i / 4f);
                p.BeginPath();
                p.MoveTo(new Vector2(Left, y));
                p.LineTo(new Vector2(Left + PlotWidth, y));
                p.Stroke();
            }
            foreach (SummaryPlayer player in data.Players)
            {
                if (hidden.Contains(player.Owner)) continue;
                float end = SummaryModel.EndOf(data, player.Owner);
                p.strokeColor = colourOf(player.Owner);
                p.lineWidth = player.Owner == local ? 3f : 1.5f;
                p.BeginPath();
                bool started = false;
                for (int i = 0; i < data.Times.Length && data.Times[i] <= end; i++)
                {
                    var at = new Vector2(XOf(data.Times[i]), YOf(SummaryModel.Value(player, metric, i)));
                    if (started) p.LineTo(at); else p.MoveTo(at);
                    started = true;
                }
                // An eliminated player's army falls to nothing on the mark; the counts keep their last value.
                if (started && end < Duration && metric != SummaryMetric.UnitsKilled && metric != SummaryMetric.UnitsLost) p.LineTo(new Vector2(XOf(end), YOf(0f)));
                p.Stroke();
            }
            foreach (SummaryElimination e in data.Eliminations)
            {
                float x = XOf(e.Time);
                p.fillColor = new Color(0.682f, 0.733f, 0.796f, 0.25f);
                Rect(p, x, Top, 1f, PlotHeight - 8f);
                p.fillColor = new Color(0.682f, 0.733f, 0.796f);
                float top = Top + PlotHeight - 7f;
                for (int y = 0; y < Skull.Length; y++)
                    for (int cell = 0; cell < Skull[y].Length; cell++)
                        if (Skull[y][cell] == '#') Rect(p, x - 4f + cell, top + y, 1f, 1f);
            }
            if (hoverTime >= 0f)
            {
                p.fillColor = new Color(0.941f, 0.957f, 0.973f);
                Rect(p, XOf(hoverTime), Top, 1f, PlotHeight);
            }
            if (playback >= 0f)
            {
                p.fillColor = new Color(0.247f, 0.816f, 1f);
                Rect(p, XOf(playback), Top, 2f, PlotHeight);
            }
        }

        private static void Rect(Painter2D p, float x, float y, float w, float h)
        {
            p.BeginPath();
            p.MoveTo(new Vector2(x, y));
            p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h));
            p.LineTo(new Vector2(x, y + h));
            p.ClosePath();
            p.Fill();
        }
    }
}

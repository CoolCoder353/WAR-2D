using System;
using UnityEngine;
using UnityEngine.UIElements;
using WAR2D.Client.Replay;
using WAR2D.Net.Replay;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the replay viewer's HUD (<c>Assets/UI/Menus/ReplayViewer.uxml</c>) to <see cref="ReplayPlayer"/>:
    /// the players' numbers from the timeline (four times a second), play/pause, speed, the seek bar (click or
    /// drag to seek), the time and Leave.
    /// </summary>
    public sealed class ReplayViewerController
    {
        public static readonly float[] Speeds = { 0.5f, 1f, 2f, 4f, 8f };
        private const float PlayersRefreshSeconds = 0.25f;

        private readonly VisualElement players, speed, seek, seekFill, seekKnob, marks, graphsPanel, viewList, markerTip;
        private readonly Button graphsButton, viewButton;
        private readonly Label markerText, unavailableNote;
        private SummaryChart chart;
        private System.Collections.Generic.List<ReplayMarker> markers;
        private readonly Button play;
        private readonly Label time;
        private float playersTimer;
        private bool dragging;
        /// <summary>Where a drag points (seconds) while a backward seek waits for the release, else -1.</summary>
        private float preview = -1f;

        public event Action LeaveClicked;

        public ReplayViewerController(VisualElement root)
        {
            players = root.Q("replay-players");
            speed = root.Q("replay-speed");
            seek = root.Q("replay-seek");
            seekFill = root.Q("replay-seek-fill");
            seekKnob = root.Q("replay-seek-knob");
            play = root.Q<Button>("replay-play");
            time = root.Q<Label>("replay-time");
            marks = root.Q("replay-seek-marks");
            graphsPanel = root.Q("replay-graphs-panel");
            graphsButton = root.Q<Button>("replay-graphs");
            viewButton = root.Q<Button>("replay-view");
            viewList = root.Q("replay-view-list");
            markerTip = root.Q("replay-marker-tip");
            markerText = root.Q<Label>("replay-marker-text");
            unavailableNote = root.Q<Label>("replay-view-note");
            graphsButton.clicked += ToggleGraphs;
            viewButton.clicked += ToggleViewList;
            play.clicked += () => { if (ReplayPlayer.Instance != null) ReplayPlayer.Instance.Playing = !ReplayPlayer.Instance.Playing; };
            root.Q<Button>("replay-leave").clicked += () => LeaveClicked?.Invoke();
            seek.RegisterCallback<PointerDownEvent>(e => { dragging = true; seek.CapturePointer(e.pointerId); SeekTo(e.localPosition.x, release: false); });
            seek.RegisterCallback<PointerMoveEvent>(e => { if (dragging) SeekTo(e.localPosition.x, release: false); });
            seek.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return;
                dragging = false;
                seek.ReleasePointer(e.pointerId);
                SeekTo(e.localPosition.x, release: true);
            });
            BuildSpeeds(1f);
        }

        /// <summary>
        /// Seeks to the bar position. Forward seeks apply while dragging (they only decode ahead); a backward seek
        /// replays from the start, so during a drag it is only previewed and applied once, on release.
        /// </summary>
        private void SeekTo(float x, bool release)
        {
            ReplayPlayer p = ReplayPlayer.Instance;
            float width = seek.resolvedStyle.width;
            if (p == null || !(width > 0f)) return;
            float target = Mathf.Clamp01(x / width) * p.DurationSeconds;
            if (!release && target < p.Seconds) { preview = target; return; }
            preview = -1f;
            if (release && Mathf.Approximately(target, p.Seconds)) return;
            p.Seek(target);
        }

        private bool markersBuilt;

        private string NameOf(int owner)
        {
            ReplayHeader h = ReplayPlayer.Instance?.Header;
            ReplayPlayerInfo info = h?.Players.Find(x => x.OwnerId == owner);
            return info == null || string.IsNullOrEmpty(info.Nickname) ? "Player" : info.Nickname;
        }

        private Color32 ColourOf(int owner)
        {
            ReplayHeader h = ReplayPlayer.Instance?.Header;
            ReplayPlayerInfo info = h?.Players.Find(x => x.OwnerId == owner);
            return PlayerPalette.Of(info != null ? info.Colour : -1);
        }

        /// <summary>Lays the skulls and crossed swords on the seek bar, once its width is known (Figma R1).</summary>
        private void BuildMarkers(ReplayPlayer p)
        {
            if (!(p.DurationSeconds > 0f)) return;
            markersBuilt = true;
            marks.Clear();
            if (!p.Summary.HasValue) return;
            markers = ReplayView.Markers(p.Summary.Value, NameOf);
            float lead = Config.ConfigLoader.LoadConfig().Replays.MarkerLeadSeconds;
            foreach (ReplayMarker marker in markers)
            {
                ReplayMarker m = marker;
                var mark = new VisualElement { name = "replay-mark" };
                mark.AddToClassList("replay-viewer__mark");
                mark.style.left = Length.Percent(Mathf.Clamp01(m.Time / p.DurationSeconds) * 100f);
                var icon = new VisualElement();
                icon.AddToClassList("replay-viewer__mark-icon");
                icon.style.backgroundImage = new StyleBackground(WAR2D.Art.PixelIcons.Texture(m.Fight ? "MarkFight" : "MarkElimination", 1));
                mark.Add(icon);
                if (m.Fight)
                {
                    var bar = new VisualElement { pickingMode = PickingMode.Ignore };
                    bar.AddToClassList("replay-viewer__mark-bar");
                    bar.style.width = Length.Percent(0f);
                    float width = Mathf.Clamp01(m.Length / p.DurationSeconds);
                    mark.RegisterCallback<GeometryChangedEvent>(_ => bar.style.width = Mathf.Max(6f, width * seek.resolvedStyle.width));
                    mark.Add(bar);
                }
                mark.RegisterCallback<PointerEnterEvent>(_ =>
                {
                    markerText.text = m.Text;
                    markerTip.RemoveFromClassList("hidden");
                });
                mark.RegisterCallback<PointerLeaveEvent>(_ => markerTip.AddToClassList("hidden"));
                mark.RegisterCallback<PointerDownEvent>(e =>
                {
                    e.StopPropagation(); // not a seek-bar drag
                    p.Seek(ReplayView.SeekTime(m, lead));
                    p.CentreOn(m.At);
                });
                marks.Add(mark);
            }
        }

        private void ToggleGraphs()
        {
            ReplayPlayer p = ReplayPlayer.Instance;
            if (p == null || !p.Summary.HasValue) return;
            bool show = graphsPanel.ClassListContains("hidden");
            graphsPanel.EnableInClassList("hidden", !show);
            graphsButton.EnableInClassList("button--primary", show);
            if (!show) return;
            chart ??= new SummaryChart(graphsPanel);
            chart.Show(p.Summary.Value, NameOf, ColourOf, p.Viewer);
        }

        private void ToggleViewList()
        {
            ReplayPlayer p = ReplayPlayer.Instance;
            if (p == null) return;
            if (!p.HasFog)
            {
                unavailableNote.EnableInClassList("hidden", !unavailableNote.ClassListContains("hidden"));
                return;
            }
            bool show = viewList.ClassListContains("hidden");
            viewList.EnableInClassList("hidden", !show);
            if (!show) return;
            viewList.Clear();
            viewList.Add(ViewEntry(p, 0, "Everything"));
            foreach (ReplayPlayerInfo info in p.Header.Players) viewList.Add(ViewEntry(p, info.OwnerId, string.IsNullOrEmpty(info.Nickname) ? "Player" : info.Nickname));
        }

        private Button ViewEntry(ReplayPlayer p, int owner, string label)
        {
            Button b = Widgets.Option("replay-view-" + owner, label, p.Viewer == owner, true, () =>
            {
                p.SetViewer(owner);
                viewButton.text = "View: " + label;
                viewButton.EnableInClassList("button--primary", owner != 0);
                viewList.AddToClassList("hidden");
                playersTimer = 0f;
            });
            return b;
        }

        private void BuildSpeeds(float current)
        {
            speed.Clear();
            foreach (float s in Speeds)
            {
                float value = s;
                speed.Add(Widgets.Option("speed-" + s.ToString(System.Globalization.CultureInfo.InvariantCulture), s.ToString(System.Globalization.CultureInfo.InvariantCulture) + "×", Mathf.Approximately(s, current), true, () =>
                {
                    if (ReplayPlayer.Instance == null) return;
                    ReplayPlayer.Instance.Speed = value;
                    BuildSpeeds(value);
                }));
            }
        }

        /// <summary>Called every frame while the viewer shows.</summary>
        public void Tick(float deltaSeconds)
        {
            ReplayPlayer p = ReplayPlayer.Instance;
            if (p == null) return;
            play.text = p.Playing ? "Pause" : "Play";
            float duration = Mathf.Max(0.001f, p.DurationSeconds);
            float t = Mathf.Clamp01((dragging && preview >= 0f ? preview : p.Seconds) / duration);
            seekFill.style.width = Length.Percent(t * 100f);
            seekKnob.style.left = Length.Percent(t * 100f);
            time.text = $"{ReplaysModel.Duration(p.Seconds)} / {ReplaysModel.Duration(p.DurationSeconds)}";
            if (!markersBuilt) BuildMarkers(p);
            if (chart != null) chart.PlaybackTime = p.Seconds;
            playersTimer -= deltaSeconds;
            if (playersTimer > 0f) return;
            playersTimer = PlayersRefreshSeconds;
            RenderPlayers(p);
        }

        /// <summary>Resets the controls for a newly opened replay.</summary>
        public void Opened()
        {
            BuildSpeeds(ReplayPlayer.Instance != null ? ReplayPlayer.Instance.Speed : 1f);
            playersTimer = 0f;
            markersBuilt = false;
            graphsPanel.AddToClassList("hidden");
            graphsButton.RemoveFromClassList("button--primary");
            viewList.AddToClassList("hidden");
            markerTip.AddToClassList("hidden");
            ReplayPlayer p = ReplayPlayer.Instance;
            graphsButton.SetEnabled(p != null && p.Summary.HasValue);
            viewButton.SetEnabled(p != null && p.HasFog);
            viewButton.text = "View: Everything";
            viewButton.RemoveFromClassList("button--primary");
            unavailableNote.text = "Per-player view unavailable\nThis replay reached the size limit, so its fog was not kept.";
            unavailableNote.EnableInClassList("hidden", true);
        }

        private void RenderPlayers(ReplayPlayer p)
        {
            players.Clear();
            ReplayHeader header = p.Header;
            if (header == null) return;
            foreach (ReplayPlayerInfo info in header.Players)
            {
                float resources = 0f; int units = 0;
                foreach (var row in p.LatestTimeline)
                    if (row.owner == info.OwnerId) { resources = row.resources; units = row.units; }
                var chip = new VisualElement { name = "replay-player-" + info.OwnerId };
                chip.AddToClassList("replay-viewer__player");
                chip.EnableInClassList("replay-viewer__player--viewed", p.Viewer == info.OwnerId);
                var swatch = new VisualElement();
                swatch.AddToClassList("lobby-row__swatch");
                swatch.AddToClassList("player-" + (info.Colour % 8 + 1));
                chip.Add(swatch);
                // The same AI tag as in the match (decision 14); a replay never carries an AI's style.
                VisualElement name = PlayerNameLabel.Build(string.IsNullOrEmpty(info.Nickname) ? "Player" : info.Nickname,
                    info.IsAi ? PlayerNameLabel.TagText(info.Difficulty) : null, "text");
                chip.Add(name);
                var numbers = new Label($"{resources:N0} res · {units:N0} units");
                numbers.AddToClassList("replay-row__meta");
                chip.Add(numbers);
                players.Add(chip);
            }
        }
    }
}

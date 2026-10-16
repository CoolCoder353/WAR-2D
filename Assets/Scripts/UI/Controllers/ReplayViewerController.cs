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

        private readonly VisualElement players, speed, seek, seekFill, seekKnob;
        private readonly Button play;
        private readonly Label time;
        private float playersTimer;
        private bool dragging;

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
            play.clicked += () => { if (ReplayPlayer.Instance != null) ReplayPlayer.Instance.Playing = !ReplayPlayer.Instance.Playing; };
            root.Q<Button>("replay-leave").clicked += () => LeaveClicked?.Invoke();
            seek.RegisterCallback<PointerDownEvent>(e => { dragging = true; seek.CapturePointer(e.pointerId); SeekTo(e.localPosition.x); });
            seek.RegisterCallback<PointerMoveEvent>(e => { if (dragging) SeekTo(e.localPosition.x); });
            seek.RegisterCallback<PointerUpEvent>(e => { dragging = false; seek.ReleasePointer(e.pointerId); });
            BuildSpeeds(1f);
        }

        private void SeekTo(float x)
        {
            ReplayPlayer p = ReplayPlayer.Instance;
            float width = seek.resolvedStyle.width;
            if (p == null || !(width > 0f)) return;
            p.Seek(Mathf.Clamp01(x / width) * p.DurationSeconds);
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
            float t = Mathf.Clamp01(p.Seconds / duration);
            seekFill.style.width = Length.Percent(t * 100f);
            seekKnob.style.left = Length.Percent(t * 100f);
            time.text = $"{ReplaysModel.Duration(p.Seconds)} / {ReplaysModel.Duration(p.DurationSeconds)}";
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
                var swatch = new VisualElement();
                swatch.AddToClassList("lobby-row__swatch");
                swatch.AddToClassList("player-" + (info.Colour % 8 + 1));
                chip.Add(swatch);
                var name = new Label(string.IsNullOrEmpty(info.Nickname) ? "Player" : info.Nickname);
                name.AddToClassList("text");
                chip.Add(name);
                var numbers = new Label($"{resources:N0} res · {units:N0} units");
                numbers.AddToClassList("replay-row__meta");
                chip.Add(numbers);
                players.Add(chip);
            }
        }
    }
}

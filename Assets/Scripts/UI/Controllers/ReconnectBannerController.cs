using System;
using System.Collections.Generic;
using Config;
using Mirror;
using UnityEngine;
using UnityEngine.UIElements;
using WAR2D.Art;

namespace WAR2D.UI
{
    /// <summary>
    /// The others' side of reconnect (item 19, Figma frame E1 124:15401 and detail 135:1450): four times a second it reads
    /// every other human's <c>ClientPlayer.isAway</c>/<c>awayUntil</c>, greys their top-bar entry with a timer
    /// (<see cref="TopBarController.SetAway"/>), shows the banners under the top bar (counting down, then "returned" or
    /// "did not return" for 4 s) and adds a "disconnected" / "returned" row to the alert feed. It also remembers who
    /// hosts the match (<see cref="RejoinMatch"/>) so the Play screen can name it if this client drops.
    /// Styles: <c>Assets/UI/Reconnect.uss</c>; container <c>reconnect-banners</c> in <c>Hud.uxml</c>.
    /// </summary>
    public sealed class ReconnectBannerController : IDisposable
    {
        private const long TickMilliseconds = 250;

        private readonly VisualElement container;
        private readonly TopBarController topBar;
        private readonly AlertFeedModel alerts;
        private readonly ReconnectModel model = new ReconnectModel();
        private readonly IVisualElementScheduledItem tick;
        private readonly List<AwaySighting> sightings = new List<AwaySighting>();
        private readonly HashSet<int> awayShown = new HashSet<int>();
        private readonly List<int> scratch = new List<int>();
        private readonly float window;

        /// <summary>The banners' model (tests and the QA smoke read it).</summary>
        public ReconnectModel Model => model;

        public ReconnectBannerController(VisualElement root, TopBarController topBar, AlertFeedModel alerts)
        {
            this.topBar = topBar;
            this.alerts = alerts;
            window = ConfigLoader.LoadConfig().Match.ReconnectSeconds;
            container = root.Q("reconnect-banners");
            if (container == null)
            {
                container = new VisualElement { name = "reconnect-banners", pickingMode = PickingMode.Ignore };
                container.AddToClassList("reconnect-banners");
                (root.Q("hud") ?? root).Add(container);
            }
            model.Disconnected += (owner, name) => Alert(AlertKind.PlayerDisconnected, owner, name);
            model.Returned += (owner, name) => Alert(AlertKind.PlayerReturned, owner, name);
            tick = root.schedule.Execute(Tick).Every(TickMilliseconds);
            Tick();
        }

        private void Tick()
        {
            sightings.Clear();
            if (NetworkClient.active)
            {
                double now = NetworkTime.time;
                foreach (ClientPlayer player in UnityEngine.Object.FindObjectsByType<ClientPlayer>())
                {
                    if (player == null || player.isAi) continue;
                    if (player.isServerOwner && !player.isLocalPlayer)
                    {
                        RejoinMatch.HostName = player.nickname;
                        RejoinMatch.HostColour = player.colourIndex;
                    }
                    else if (player.isServerOwner) { RejoinMatch.HostName = null; RejoinMatch.HostColour = -1; }
                    if (player.isLocalPlayer) continue;
                    sightings.Add(new AwaySighting
                    {
                        OwnerId = player.OwnerId,
                        Name = player.nickname,
                        ColourIndex = player.colourIndex,
                        IsAway = player.isAway,
                        SecondsLeft = player.isAway ? (float)Math.Max(0d, player.awayUntil - now) : 0f,
                    });
                }
            }
            model.Update(sightings, Time.unscaledTime);
            ShowTopBar();
            Show();
        }

        /// <summary>Greys each away player's top-bar entry with their timer, and clears it when they are back or gone.</summary>
        private void ShowTopBar()
        {
            scratch.Clear();
            foreach (AwaySighting s in sightings)
                if (s.IsAway)
                {
                    topBar?.SetAway(s.OwnerId, ReconnectText.Clock(s.SecondsLeft));
                    scratch.Add(s.OwnerId);
                }
            foreach (int owner in awayShown)
                if (!scratch.Contains(owner)) topBar?.SetAway(owner, null);
            awayShown.Clear();
            foreach (int owner in scratch) awayShown.Add(owner);
        }

        private void Alert(AlertKind kind, int owner, string name)
        {
            if (alerts == null) return;
            var alert = new Alert { Kind = kind, OtherOwnerId = owner };
            alerts.Add(alert, AlertFeedModel.Text(alert, _ => name, null), Time.unscaledTime);
            if (SettingsStore.Current.SoundsAlert(kind)) WAR2D.Audio.AudioService.Ui(AlertSounds.SoundOf(kind));
        }

        /// <summary>Rebuilds the banners (a handful of elements, four times a second).</summary>
        private void Show()
        {
            container.Clear();
            foreach (ReconnectBanner banner in model.Banners) container.Add(Build(banner, window));
        }

        /// <summary>One banner (Figma <c>reconnect-banner</c>, 135:1454 / 135:1467 / 135:1483).</summary>
        public static VisualElement Build(ReconnectBanner banner, float window)
        {
            bool danger = banner.Kind == ReconnectBannerKind.DidNotReturn ||
                          (banner.Kind == ReconnectBannerKind.Away && ReconnectText.IsDanger(banner.SecondsLeft));
            var root = new VisualElement { name = "reconnect-banner-" + banner.OwnerId, pickingMode = PickingMode.Ignore };
            root.AddToClassList("reconnect-banner");
            root.AddToClassList(banner.Kind == ReconnectBannerKind.Returned ? "reconnect--success" : danger ? "reconnect--danger" : "reconnect--warning");

            var stripe = new VisualElement { pickingMode = PickingMode.Ignore };
            stripe.AddToClassList("reconnect-banner__stripe");
            root.Add(stripe);
            root.Add(Icon());

            var body = new VisualElement { pickingMode = PickingMode.Ignore };
            body.AddToClassList("reconnect-banner__body");
            var line = new VisualElement { pickingMode = PickingMode.Ignore };
            line.AddToClassList("reconnect-banner__line");
            var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
            swatch.AddToClassList("swatch");
            swatch.AddToClassList("player-" + (Math.Max(0, banner.ColourIndex) % 8 + 1));
            line.Add(swatch);
            var title = new Label(ReconnectText.Title(banner.Kind, banner.Name)) { name = "reconnect-title", pickingMode = PickingMode.Ignore };
            title.AddToClassList("reconnect-banner__title");
            line.Add(title);
            string tail = ReconnectText.Tail(banner.Kind, banner.SecondsLeft);
            if (tail != null)
            {
                var time = new Label(tail) { name = "reconnect-time", pickingMode = PickingMode.Ignore };
                time.AddToClassList("reconnect-banner__time");
                line.Add(time);
            }
            body.Add(line);
            var detail = new Label(ReconnectText.Detail(banner.Kind)) { pickingMode = PickingMode.Ignore };
            detail.AddToClassList("reconnect-banner__detail");
            body.Add(detail);
            if (banner.Kind == ReconnectBannerKind.Away) body.Add(Bar(ReconnectText.Fraction(banner.SecondsLeft, window)));
            root.Add(body);
            return root;
        }

        /// <summary>The 32 px reconnect icon (Figma Icon/px/Reconnect 117:8, <see cref="PixelIcons"/> at 2×).</summary>
        public static VisualElement Icon()
        {
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("reconnect-icon");
            icon.style.backgroundImage = new StyleBackground(PixelIcons.Texture("IconReconnect", 2));
            return icon;
        }

        /// <summary>The 16 px reconnect icon of a top-bar away tag.</summary>
        public static VisualElement SmallIcon()
        {
            var icon = new VisualElement { name = "player-away-icon", pickingMode = PickingMode.Ignore };
            icon.AddToClassList("reconnect-icon--small");
            icon.style.backgroundImage = new StyleBackground(PixelIcons.Texture("IconReconnect"));
            return icon;
        }

        /// <summary>The 3 px countdown bar, filled to <paramref name="fraction"/>.</summary>
        public static VisualElement Bar(float fraction)
        {
            var bar = new VisualElement { name = "reconnect-bar", pickingMode = PickingMode.Ignore };
            bar.AddToClassList("reconnect-bar");
            var fill = new VisualElement { name = "reconnect-bar-fill", pickingMode = PickingMode.Ignore };
            fill.AddToClassList("reconnect-bar__fill");
            fill.style.width = Length.Percent(fraction * 100f);
            bar.Add(fill);
            return bar;
        }

        public void Dispose()
        {
            tick?.Pause();
            foreach (int owner in awayShown) topBar?.SetAway(owner, null);
            awayShown.Clear();
            container?.Clear();
        }
    }
}

using System;
using System.Collections.Generic;

namespace WAR2D.UI
{
    /// <summary>What a reconnect banner (item 19, Figma 135:1450) reports.</summary>
    public enum ReconnectBannerKind
    {
        /// <summary>The player dropped out and their seat is held: a countdown with a draining bar (warning, danger under 10 s).</summary>
        Away,
        /// <summary>The player came back in time (success; closes after <see cref="ReconnectModel.ResultSeconds"/>).</summary>
        Returned,
        /// <summary>The window ran out and the player is out (danger; closes after <see cref="ReconnectModel.ResultSeconds"/>).</summary>
        DidNotReturn,
    }

    /// <summary>One banner under the top bar.</summary>
    public sealed class ReconnectBanner
    {
        public int OwnerId;
        public string Name;
        public int ColourIndex;
        public ReconnectBannerKind Kind;
        /// <summary>Seconds left to return (Away only).</summary>
        public float SecondsLeft;
        /// <summary>When a Returned or DidNotReturn banner closes (the model's clock).</summary>
        public float ClosesAt;
    }

    /// <summary>What the client sees of one other human player this tick (from their <c>ClientPlayer</c>).</summary>
    public struct AwaySighting
    {
        public int OwnerId;
        public string Name;
        public int ColourIndex;
        public bool IsAway;
        /// <summary>Seconds left in their window (their <c>awayUntil</c> less network time); ignored when not away.</summary>
        public float SecondsLeft;
    }

    /// <summary>
    /// The others' side of reconnect (item 19, frame E1): turns each tick's sightings of the other players into banners and
    /// "disconnected" / "returned" events. A player who goes away gets a counting-down banner; coming back swaps it for a
    /// 4 s "returned" banner; vanishing while away (the server removed them when the window closed) swaps it for a 4 s
    /// "did not return" banner. Pure: the caller supplies the clock.
    /// </summary>
    public sealed class ReconnectModel
    {
        /// <summary>How long the "returned" and "did not return" banners stay (Figma 135:1450).</summary>
        public const float ResultSeconds = 4f;

        private sealed class Track
        {
            public string Name;
            public int ColourIndex;
            public bool Away;
        }

        private readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        private readonly List<ReconnectBanner> banners = new List<ReconnectBanner>();
        private readonly HashSet<int> seen = new HashSet<int>();
        private readonly List<int> gone = new List<int>();
        private bool primed;

        /// <summary>The banners to show, oldest first.</summary>
        public IReadOnlyList<ReconnectBanner> Banners => banners;

        /// <summary>Raised when a player drops out (not for players already away when the HUD opened): owner, name.</summary>
        public event Action<int, string> Disconnected;

        /// <summary>Raised when an away player comes back: owner, name.</summary>
        public event Action<int, string> Returned;

        /// <summary>Raised when an away player's window ran out: owner, name.</summary>
        public event Action<int, string> TimedOut;

        /// <summary>Applies one tick's sightings of the other human players at <paramref name="now"/>.</summary>
        public void Update(IReadOnlyList<AwaySighting> sightings, float now)
        {
            seen.Clear();
            foreach (AwaySighting s in sightings)
            {
                seen.Add(s.OwnerId);
                if (!tracks.TryGetValue(s.OwnerId, out Track track))
                {
                    track = new Track();
                    tracks[s.OwnerId] = track;
                }
                bool wasAway = track.Away;
                track.Name = s.Name;
                track.ColourIndex = s.ColourIndex;
                track.Away = s.IsAway;

                if (s.IsAway)
                {
                    ReconnectBanner banner = Find(s.OwnerId);
                    if (banner == null || banner.Kind != ReconnectBannerKind.Away)
                    {
                        Remove(s.OwnerId);
                        banner = new ReconnectBanner { OwnerId = s.OwnerId, Kind = ReconnectBannerKind.Away };
                        banners.Add(banner);
                        if (primed && !wasAway) Disconnected?.Invoke(s.OwnerId, s.Name);
                    }
                    banner.Name = s.Name;
                    banner.ColourIndex = s.ColourIndex;
                    banner.SecondsLeft = Math.Max(0f, s.SecondsLeft);
                }
                else if (wasAway)
                {
                    Close(s.OwnerId, track, ReconnectBannerKind.Returned, now);
                    Returned?.Invoke(s.OwnerId, track.Name);
                }
            }

            gone.Clear();
            foreach (KeyValuePair<int, Track> pair in tracks) if (!seen.Contains(pair.Key)) gone.Add(pair.Key);
            foreach (int owner in gone)
            {
                Track track = tracks[owner];
                tracks.Remove(owner);
                if (!track.Away) continue; // left normally (or was never away): nothing to say
                Close(owner, track, ReconnectBannerKind.DidNotReturn, now);
                TimedOut?.Invoke(owner, track.Name);
            }

            banners.RemoveAll(b => b.Kind != ReconnectBannerKind.Away && b.ClosesAt <= now);
            primed = true;
        }

        private void Close(int owner, Track track, ReconnectBannerKind kind, float now)
        {
            Remove(owner);
            banners.Add(new ReconnectBanner { OwnerId = owner, Name = track.Name, ColourIndex = track.ColourIndex, Kind = kind, ClosesAt = now + ResultSeconds });
        }

        private ReconnectBanner Find(int owner)
        {
            foreach (ReconnectBanner b in banners) if (b.OwnerId == owner) return b;
            return null;
        }

        private void Remove(int owner) => banners.RemoveAll(b => b.OwnerId == owner);
    }

    /// <summary>The reconnect texts and countdown rules (item 19, Figma 135:1450), shared by the banners, the top bar and the Play screen.</summary>
    public static class ReconnectText
    {
        /// <summary>Below this many whole seconds left the countdown turns to the danger style.</summary>
        public const int DangerBelowSeconds = 10;

        /// <summary>Whole seconds shown for a time left: rounded up, so 0:00 only when the time is really up.</summary>
        public static int WholeSeconds(float seconds) =>
            float.IsNaN(seconds) || seconds <= 0f ? 0 : (int)Math.Min(int.MaxValue / 2, Math.Ceiling(seconds));

        /// <summary>A time left as m:ss ("0:42").</summary>
        public static string Clock(float seconds)
        {
            int s = WholeSeconds(seconds);
            return $"{s / 60}:{s % 60:00}";
        }

        /// <summary>True in the last 10 s (the danger state).</summary>
        public static bool IsDanger(float seconds) => WholeSeconds(seconds) < DangerBelowSeconds;

        /// <summary>How full the countdown bar is (1 at the start of the window, 0 at the end).</summary>
        public static float Fraction(float seconds, float window) =>
            window <= 0f || float.IsNaN(seconds) ? 0f : Math.Clamp(seconds / window, 0f, 1f);

        /// <summary>The banner's headline for a player.</summary>
        public static string Title(ReconnectBannerKind kind, string name) => kind switch
        {
            ReconnectBannerKind.Away => $"{name} disconnected",
            ReconnectBannerKind.Returned => $"{name} returned",
            _ => $"{name} did not return",
        };

        /// <summary>The coloured tail after the headline ("· 0:42 to return", "· eliminated"); null for none.</summary>
        public static string Tail(ReconnectBannerKind kind, float seconds) => kind switch
        {
            ReconnectBannerKind.Away => $"· {Clock(seconds)} to return",
            ReconnectBannerKind.DidNotReturn => "· eliminated",
            _ => null,
        };

        /// <summary>The banner's second line: what the player's army does.</summary>
        public static string Detail(ReconnectBannerKind kind) => kind switch
        {
            ReconnectBannerKind.Away => "Their units hold position and stop producing; their upkeep is paused.",
            ReconnectBannerKind.Returned => "Their units take orders again.",
            _ => "Eliminated exactly as before; their units and buildings are removed.",
        };

        /// <summary>The Play screen's match name ("Oscar’s match"; "Your match" when the host is unknown).</summary>
        public static string MatchOf(string host) => string.IsNullOrEmpty(host) ? "Your match" : $"{host}’s match";

        /// <summary>The Play screen's countdown ("· 0:42 left to rejoin").</summary>
        public static string RejoinLeft(float seconds) => $"· {Clock(seconds)} left to rejoin";

        /// <summary>The Play screen card's headline, idle or rejoining.</summary>
        public static string RejoinTitle(bool rejoining, string host) =>
            rejoining ? (string.IsNullOrEmpty(host) ? "Rejoining your match…" : $"Rejoining {host}’s match…") : "Your match is still running";
    }

    /// <summary>
    /// Who hosted the match this client is in, remembered while it plays so the Play screen can name it after a drop
    /// (the player objects are gone by then). Client-side only.
    /// </summary>
    public static class RejoinMatch
    {
        /// <summary>The host's nickname, or null when unknown.</summary>
        public static string HostName;

        /// <summary>The host's palette colour (0–7), or -1 when unknown.</summary>
        public static int HostColour = -1;
    }
}

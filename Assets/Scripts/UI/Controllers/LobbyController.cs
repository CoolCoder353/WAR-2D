using System;
using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using UnityEngine.UIElements;
using WAR2D.Client;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the lobby (<c>Assets/UI/Menus/Lobby.uxml</c>): the players with their colour, team and ready
    /// state, the local player's colour and team pickers, the host's match settings (read-only for
    /// everyone else), the map preview (terrain only: start points are private), seed and Reroll, and Leave / Ready / Start.
    /// Raises events only; <see cref="MenuController"/> sends the commands.
    /// </summary>
    public sealed class LobbyController : IDisposable
    {
        private readonly LobbyModel model;
        private readonly MapPreview preview;
        private readonly LobbyConfig lobby;
        private readonly MapConfig map;
        private readonly Func<string> hostAddress;
        private readonly VisualElement rows, colourPicker, teamSection, teamPicker;
        private readonly VisualElement modeOptions, sizeOptions, resourceOptions, diplomacySlot, startOptions, pickTimes;
        private readonly VisualElement previewImage, markers;
        private readonly Label subtitle, playersTitle, settingsTitle, status, startOptionsHint;
        private readonly TextField seed;
        private readonly Button reroll, ready, start;

        /// <summary>The host changed a setting (the whole new settings).</summary>
        public event Action<MatchSettings> SettingsChanged;
        public event Action RerollClicked, StartClicked, LeaveClicked;
        /// <summary>The local player picked a colour.</summary>
        public event Action<int> ColourClicked;
        /// <summary>The local player picked their own team (<see cref="TeamRules.NoTeam"/> = Solo).</summary>
        public event Action<int> TeamClicked;
        /// <summary>The host cycled a player's team: (owner, new team).</summary>
        public event Action<int, int> HostTeamClicked;
        /// <summary>The local player readied up (true) or cancelled (false).</summary>
        public event Action<bool> ReadyClicked;

        private readonly Button invite;

        /// <summary>The host pressed Invite friends.</summary>
        public event Action InviteClicked;

        /// <summary>Shows or hides Invite friends (only a host with a Steam lobby has it).</summary>
        public void SetInviteVisible(bool visible) => invite.EnableInClassList("hidden", !visible);

        public LobbyController(VisualElement root, LobbyModel model, MapPreview preview, LobbyConfig lobby, MapConfig map, Func<string> hostAddress)
        {
            this.map = map;
            this.model = model;
            this.preview = preview;
            this.lobby = lobby;
            this.hostAddress = hostAddress;
            rows = root.Q("player-rows");
            colourPicker = root.Q("colour-picker");
            teamSection = root.Q("team-section");
            teamPicker = root.Q("team-picker");
            modeOptions = root.Q("mode-options");
            sizeOptions = root.Q("size-options");
            resourceOptions = root.Q("resource-options");
            startOptions = root.Q("start-options-row");
            pickTimes = root.Q("pick-time-row");
            startOptionsHint = root.Q<Label>("start-options-hint");
            diplomacySlot = root.Q("diplomacy-switch-slot");
            previewImage = root.Q("preview-image");
            markers = root.Q("preview-markers");
            subtitle = root.Q<Label>("lobby-subtitle");
            playersTitle = root.Q<Label>("players-title");
            settingsTitle = root.Q<Label>("settings-title");
            status = root.Q<Label>("lobby-status");
            seed = root.Q<TextField>("seed-field");
            reroll = root.Q<Button>("reroll-button");
            ready = root.Q<Button>("ready-button");
            start = root.Q<Button>("start-button");

            reroll.clicked += () => RerollClicked?.Invoke();
            start.clicked += () => StartClicked?.Invoke();
            root.Q<Button>("leave-button").clicked += () => LeaveClicked?.Invoke();
            invite = root.Q<Button>("invite-button");
            invite.clicked += () => InviteClicked?.Invoke();
            ready.clicked += () => ReadyClicked?.Invoke(!model.Local.Ready);
            seed.RegisterCallback<FocusOutEvent>(_ => SubmitSeed());
            seed.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) SubmitSeed();
            });

            model.Changed += Show;
            preview.Ready += ShowPreview;
            Show();
            ShowPreview();
        }

        private void Show()
        {
            MatchSettings s = model.Settings;
            bool host = model.LocalIsHost;
            bool teams = s.Mode == MatchMode.Teams;
            LobbyPlayer me = model.Local;

            string hostName = "the host";
            foreach (LobbyPlayer p in model.Players) if (p.IsHost) hostName = p.Nickname;
            subtitle.text = host ? $"You are hosting · {hostAddress()}" : $"Hosted by {hostName} · you are {me.Nickname}";
            playersTitle.text = $"Players ({model.Players.Count})";
            settingsTitle.text = host ? "Match settings" : "Match settings (host only)";

            ShowRows(host, teams);
            ShowPickers(host, teams, me);
            ShowSettings(s, host);

            if (seed.focusController?.focusedElement != seed) seed.SetValueWithoutNotify(s.Seed.ToString());
            seed.SetEnabled(host);
            reroll.SetEnabled(host);

            ready.text = me.Ready ? "Cancel ready" : "Ready";
            ready.EnableInClassList("button--primary", !me.Ready);
            start.EnableInClassList("hidden", !host);
            bool fits = Fits(s.StartOptions);
            start.SetEnabled(model.CanStart && fits);
            status.text = host && model.CanStart && !fits ? "Too many options per player for this map" : model.Status;
            status.EnableInClassList("text--warning", host && !(model.CanStart && fits));
            status.EnableInClassList("text--muted", !host);
            markers.Clear(); // start points are private (v0.11): the preview shows terrain only
        }

        private void ShowRows(bool host, bool teams)
        {
            rows.Clear();
            foreach (LobbyPlayer p in model.Players)
            {
                var row = new VisualElement { name = "lobby-row-" + p.OwnerId };
                row.AddToClassList("lobby-row");
                row.EnableInClassList("lobby-row--ready", p.Ready);
                var swatch = new VisualElement();
                swatch.AddToClassList("lobby-row__swatch");
                swatch.AddToClassList("player-" + (p.ColourIndex % LobbyRules.Colours + 1));
                var name = new Label(p.IsHost ? p.Nickname + "  (Host)" : p.Nickname);
                name.AddToClassList("lobby-row__name");
                row.Add(swatch);
                row.Add(name);
                if (teams)
                {
                    string team = TeamName(p.Team);
                    if (host)
                    {
                        int owner = p.OwnerId, next = NextTeam(p.Team);
                        var pill = new Button(() => HostTeamClicked?.Invoke(owner, next)) { text = team + " ▾", name = "team-" + owner };
                        pill.AddToClassList("lobby-row__team");
                        row.Add(pill);
                    }
                    else
                    {
                        var pill = new Label(team);
                        pill.AddToClassList("lobby-row__team");
                        row.Add(pill);
                    }
                }
                var readyLabel = new Label(p.Ready ? "Ready" : "Not ready");
                readyLabel.AddToClassList("lobby-row__ready");
                readyLabel.EnableInClassList("lobby-row__ready--yes", p.Ready);
                row.Add(readyLabel);
                rows.Add(row);
            }
        }

        private void ShowPickers(bool host, bool teams, LobbyPlayer me)
        {
            colourPicker.Clear();
            for (int c = 0; c < LobbyRules.Colours; c++)
            {
                int colour = c;
                bool taken = model.IsColourTaken(c);
                var chip = new Button(() => ColourClicked?.Invoke(colour)) { name = "colour-" + c, text = taken ? "×" : "" };
                chip.AddToClassList("colour-chip");
                chip.AddToClassList("player-" + (c + 1));
                chip.EnableInClassList("colour-chip--mine", me.ColourIndex == c);
                chip.SetEnabled(!taken);
                colourPicker.Add(chip);
            }
            teamSection.EnableInClassList("hidden", !teams);
            teamPicker.Clear();
            if (!teams) return;
            for (int t = 0; t < LobbyRules.PickableTeams; t++)
            {
                int team = t;
                teamPicker.Add(Widgets.Option("own-team-" + t, TeamName(t), me.Team == t, true, () => TeamClicked?.Invoke(team)));
            }
            teamPicker.Add(Widgets.Option("own-team-solo", "Solo", me.Team == TeamRules.NoTeam, true, () => TeamClicked?.Invoke(TeamRules.NoTeam)));
        }

        private void ShowSettings(MatchSettings s, bool host)
        {
            modeOptions.Clear();
            modeOptions.Add(Widgets.Option("mode-ffa", "Free-for-all", s.Mode == MatchMode.FreeForAll, host, () => Change(s, x => x.Mode = MatchMode.FreeForAll)));
            modeOptions.Add(Widgets.Option("mode-teams", "Teams", s.Mode == MatchMode.Teams, host, () => Change(s, x => x.Mode = MatchMode.Teams)));
            diplomacySlot.Clear();
            diplomacySlot.Add(Widgets.Switch("diplomacy-switch", s.Diplomacy, host, () => Change(s, x => x.Diplomacy = !s.Diplomacy), "Diplomacy"));
            sizeOptions.Clear();
            foreach (int size in lobby.MapSizes)
            {
                int value = size;
                sizeOptions.Add(Widgets.Option("size-" + size, size.ToString(), s.MapSize == size, host, () => Change(s, x => x.MapSize = value)));
            }
            resourceOptions.Clear();
            foreach (float amount in lobby.StartingResources)
            {
                float value = amount;
                resourceOptions.Add(Widgets.Option("resources-" + amount, amount.ToString("0"), s.StartingResources.Equals(amount), host, () => Change(s, x => x.StartingResources = value)));
            }
            ShowStartSettings(s, host);
        }

        /// <summary>
        /// Options per player (values that don't fit the players and map size are disabled, with one hint naming the
        /// site count) and the HQ pick time (Figma: Menu / Lobby (host) – start points).
        /// </summary>
        private void ShowStartSettings(MatchSettings s, bool host)
        {
            startOptions.Clear();
            int unfit = 0;
            for (int n = lobby.StartOptionsMin; n <= lobby.StartOptionsMax; n++)
            {
                int value = n;
                bool fits = Fits(n);
                if (!fits && (unfit == 0 || n == s.StartOptions)) unfit = n;
                startOptions.Add(Widgets.Option("start-options-" + n, n.ToString(), s.StartOptions == n, host && fits, () => Change(s, x => x.Value.StartOptions = value)));
            }
            if (!Fits(s.StartOptions)) unfit = s.StartOptions;
            int players = Math.Max(1, model.Players.Count);
            startOptionsHint.text = unfit == 0 ? "" :
                $"{unfit} options need {players * unfit + lobby.SpareStartSites} start sites for {players} {(players == 1 ? "player" : "players")}; a {s.MapSize} map has {Sites(s.MapSize)}.";
            startOptionsHint.EnableInClassList("hidden", unfit == 0);

            pickTimes.Clear();
            foreach (int seconds in lobby.StartPickSeconds)
            {
                int value = seconds;
                pickTimes.Add(Widgets.Option("pick-time-" + seconds, seconds == 0 ? "Random" : $"{seconds} s", s.StartPickSeconds == seconds, host, () => Change(s, x => x.Value.StartPickSeconds = value)));
            }
            pickTimes.Add(Widgets.Option("pick-time-unlimited", "Unlimited", s.StartPickSeconds == StartPickRules.Unlimited, host,
                () => Change(s, x => x.Value.StartPickSeconds = StartPickRules.Unlimited)));
        }

        /// <summary>True when the lobby's players can each be dealt <paramref name="options"/> on the chosen map (the server checks the same at Start).</summary>
        private bool Fits(int options)
        {
            MatchSettings s = model.Settings;
            s.StartOptions = options;
            return LobbyRules.FitsMap(Math.Max(1, model.Players.Count), s, map, lobby.SpareStartSites);
        }

        private int Sites(int size) => map != null && size > 0 ? WAR2D.World.RegionMapGenerator.Counts(map, size).starts : 0;

        /// <summary>Sends the settings with one change, filling in list values the server would otherwise reject (the defaults may come from outside the lists).</summary>
        private void Change(MatchSettings s, Action<Box> edit)
        {
            var box = new Box { Value = s };
            edit(box);
            MatchSettings next = box.Value;
            if (Array.IndexOf(lobby.MapSizes, next.MapSize) < 0 && lobby.MapSizes.Length > 0) next.MapSize = lobby.MapSizes[lobby.MapSizes.Length - 1];
            bool listed = false;
            foreach (float r in lobby.StartingResources) listed |= r.Equals(next.StartingResources);
            if (!listed && lobby.StartingResources.Length > 0) next.StartingResources = lobby.StartingResources[Math.Min(1, lobby.StartingResources.Length - 1)];
            SettingsChanged?.Invoke(next);
        }

        /// <summary>A mutable holder so option callbacks can edit a copy of the settings struct.</summary>
        private sealed class Box
        {
            public MatchSettings Value;
            public MatchMode Mode { set => Value.Mode = value; }
            public bool Diplomacy { set => Value.Diplomacy = value; }
            public int MapSize { set => Value.MapSize = value; }
            public float StartingResources { set => Value.StartingResources = value; }
        }

        private void SubmitSeed()
        {
            if (!model.LocalIsHost) return;
            if (!uint.TryParse(seed.value?.Trim(), out uint value) || value == 0 || value == model.Settings.Seed)
            {
                seed.SetValueWithoutNotify(model.Settings.Seed.ToString());
                return;
            }
            MatchSettings s = model.Settings;
            Change(s, x => x.Value.Seed = value);
        }

        private void ShowPreview()
        {
            previewImage.style.backgroundImage = preview.Texture != null ? new StyleBackground(preview.Texture) : new StyleBackground();
            markers.Clear(); // start points are private (v0.11): the preview shows terrain only
        }

        private static string TeamName(int team) => team == TeamRules.NoTeam ? "Solo" : $"Team {team + 1}";

        /// <summary>Team 1 → 2 → 3 → 4 → Solo → Team 1.</summary>
        private static int NextTeam(int team) => team == TeamRules.NoTeam ? 0 : team + 1 >= LobbyRules.PickableTeams ? TeamRules.NoTeam : team + 1;

        /// <summary>Redraws the lobby (the host's address line changes without the model changing).</summary>
        public void Refresh() => Show();

        public void Dispose()
        {
            model.Changed -= Show;
            preview.Ready -= ShowPreview;
        }
    }
}

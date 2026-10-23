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
        private readonly VisualElement visibilityOptions, addAiRow;
        private readonly Label visibilityHint, addAiHint;
        private readonly Button addAi;
        /// <summary>Set when the host clicked Private with another human connected: the hint says so until the selection or the players change.</summary>
        private bool privateRefused;
        private int refusedAtPlayers = -1;
        private LobbyVisibility refusedAtVisibility;
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
        /// <summary>The host pressed Add AI (the server picks Normal, Random, the first free colour and the emptiest team).</summary>
        public event Action AddAiClicked;
        /// <summary>The host changed an AI's row: (owner, difficulty, style, colour, team).</summary>
        public event Action<int, AiDifficulty, AiStyle, int, int> AiChanged;
        /// <summary>The host removed an AI (its ×).</summary>
        public event Action<int> AiRemoved;

        /// <summary>Hint texts under Visibility (Figma 93:94, 93:343, 94:618).</summary>
        public const string FriendsHint = "Friends: your Steam friends see this game and can join. Public: anyone can find it. Private: single-player.";
        public const string PublicHint = "Public: anyone on Steam can find this game and join. Friends: only your Steam friends. Private: single-player.";
        public const string PrivateHint = "Private: single-player, nobody can join. No Steam lobby is made; Invite and the join address are hidden.";
        public const string PrivateRefusedHint = "Remove the other players first";

        private readonly Button invite;

        /// <summary>The host pressed Invite friends.</summary>
        public event Action InviteClicked;

        /// <summary>Shows or hides Invite friends (only a host with a Steam lobby has it).</summary>
        public void SetInviteVisible(bool visible) => invite.EnableInClassList("hidden", !visible || model.Settings.Visibility == LobbyVisibility.Private);

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
            visibilityOptions = root.Q("visibility-row");
            visibilityHint = root.Q<Label>("visibility-hint");
            addAiRow = root.Q("add-ai-row");
            addAiHint = root.Q<Label>("add-ai-hint");
            addAi = root.Q<Button>("add-ai-button");
            addAi.clicked += () => AddAiClicked?.Invoke();
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
            bool isPrivate = s.Visibility == LobbyVisibility.Private;
            // Private hides the join line (the Direct addresses): nobody can join (Figma 93:343).
            subtitle.text = host ? (isPrivate ? "You are hosting a private match · single-player: nobody can join" : $"You are hosting · {hostAddress()}")
                : $"Hosted by {hostName} · you are {me.Nickname}";
            playersTitle.text = $"Players ({model.Players.Count} of {LobbyRules.MaxPlayers})";
            if (isPrivate) invite.AddToClassList("hidden");
            settingsTitle.text = host ? "Match settings" : "Match settings (host only)";

            ShowRows(host, teams);
            ShowAddAi(host, isPrivate);
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
            status.text = host && model.CanStart && !fits ? "Too many options per player for this map"
                : host && isPrivate && model.CanStart ? "Single-player: start when you like" : model.Status;
            status.EnableInClassList("text--warning", host && !(model.CanStart && fits));
            status.EnableInClassList("text--muted", !host);
            markers.Clear(); // start points are private (v0.11): the preview shows terrain only
        }

        private void ShowRows(bool host, bool teams)
        {
            rows.Clear();
            foreach (LobbyPlayer p in model.Players)
            {
                if (p.IsAi) { rows.Add(AiRow(p, host, teams)); continue; }
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

        /// <summary>
        /// An AI's row (Figma 93:94 host, 93:592 joined). Host: swatch (click: the next free colour), callsign, the tag
        /// ("AI"), Difficulty and Style dropdowns, Team (Teams mode), Ready, ×. Everyone else: callsign with "AI · Hard",
        /// the style as text (Random stays "Random"), team and Ready.
        /// </summary>
        private VisualElement AiRow(LobbyPlayer p, bool host, bool teams)
        {
            int owner = p.OwnerId;
            var row = new VisualElement { name = "lobby-row-" + owner };
            row.AddToClassList("lobby-row");
            row.AddToClassList("ai-row");
            row.AddToClassList("lobby-row--ready");
            string colourClass = "player-" + (p.ColourIndex % LobbyRules.Colours + 1);
            if (host)
            {
                var swatch = new Button(() => AiChanged?.Invoke(owner, p.Difficulty, p.Style, model.NextFreeColour(owner, p.ColourIndex), p.Team))
                    { name = "ai-colour-" + owner, tooltip = "Change colour" };
                swatch.AddToClassList("lobby-row__swatch-button");
                swatch.AddToClassList(colourClass);
                row.Add(swatch);
            }
            else
            {
                var swatch = new VisualElement();
                swatch.AddToClassList("lobby-row__swatch");
                swatch.AddToClassList(colourClass);
                row.Add(swatch);
            }
            VisualElement name = PlayerNameLabel.Build(p.Nickname, host ? "AI" : PlayerNameLabel.TagText(p.Difficulty), "lobby-row__name");
            name.AddToClassList("lobby-row__name-box");
            row.Add(name);
            if (host)
            {
                var difficulty = Pill("ai-difficulty-" + owner, AiLobbyRules.DifficultyName(p.Difficulty) + " ▾");
                difficulty.clicked += () => Menu(difficulty, new[] { AiDifficulty.Easy, AiDifficulty.Normal, AiDifficulty.Hard }, p.Difficulty, AiLobbyRules.DifficultyName,
                    d => AiChanged?.Invoke(owner, d, p.Style, p.ColourIndex, p.Team));
                row.Add(difficulty);
                var style = Pill("ai-style-" + owner, AiLobbyRules.StyleName(p.Style) + " ▾");
                style.clicked += () => Menu(style, new[] { AiStyle.Random, AiStyle.Rusher, AiStyle.Turtle, AiStyle.Expander, AiStyle.Balanced }, p.Style, AiLobbyRules.StyleName,
                    st => AiChanged?.Invoke(owner, p.Difficulty, st, p.ColourIndex, p.Team));
                row.Add(style);
                if (teams)
                {
                    int next = NextTeam(p.Team);
                    var team = new Button(() => AiChanged?.Invoke(owner, p.Difficulty, p.Style, p.ColourIndex, next)) { text = TeamName(p.Team) + " ▾", name = "team-" + owner };
                    team.AddToClassList("lobby-row__pill");
                    row.Add(team);
                }
            }
            else
            {
                var style = new Label(AiLobbyRules.StyleName(p.Style)) { name = "ai-style-" + owner };
                style.AddToClassList("lobby-row__text");
                row.Add(style);
                if (teams)
                {
                    var team = new Label(TeamName(p.Team)) { name = "team-" + owner };
                    team.AddToClassList("lobby-row__text");
                    row.Add(team);
                }
            }
            var ready = new Label("Ready");
            ready.AddToClassList("lobby-row__ready");
            ready.AddToClassList("lobby-row__ready--yes");
            row.Add(ready);
            if (host)
            {
                var remove = new Button(() => AiRemoved?.Invoke(owner)) { text = "×", name = "ai-remove-" + owner, tooltip = "Remove" };
                remove.AddToClassList("lobby-row__remove");
                row.Add(remove);
            }
            return row;
        }

        private static Button Pill(string name, string text)
        {
            var pill = new Button { name = name, text = text };
            pill.AddToClassList("lobby-row__pill");
            return pill;
        }

        /// <summary>The dropdown under an AI row's pill: one item per value, the current one checked.</summary>
        private static void Menu<T>(VisualElement anchor, T[] values, T current, Func<T, string> label, Action<T> picked)
        {
            var menu = new GenericDropdownMenu();
            foreach (T value in values)
            {
                T v = value;
                menu.AddItem(label(v), Equals(v, current), () => { if (!Equals(v, current)) picked(v); });
            }
            menu.DropDown(anchor.worldBound, anchor, false);
        }

        /// <summary>Add AI and its hint: the host's, while a seat is free (decision 4).</summary>
        private void ShowAddAi(bool host, bool isPrivate)
        {
            addAiRow.EnableInClassList("hidden", !host || !model.HasFreeSeat);
            addAiHint.text = isPrivate ? "Add AIs to play against; you need two teams to start." : "AIs are always ready. Shown while a seat is free (8 at most).";
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
            ShowVisibility(s, host);
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
        /// Visibility, the first setting (Figma 93:94): Public / Friends / Private with one hint for the selected value;
        /// Private's in the warning colour. Going Private with another human connected changes nothing and says
        /// "Remove the other players first" until the selection or the players change (94:618; the server refuses it too).
        /// </summary>
        private void ShowVisibility(MatchSettings s, bool host)
        {
            if (privateRefused && (model.Players.Count != refusedAtPlayers || s.Visibility != refusedAtVisibility)) privateRefused = false;
            visibilityOptions.Clear();
            foreach (LobbyVisibility v in new[] { LobbyVisibility.Public, LobbyVisibility.Friends, LobbyVisibility.Private })
            {
                LobbyVisibility value = v;
                visibilityOptions.Add(Widgets.Option("visibility-" + v.ToString().ToLowerInvariant(), v.ToString(), s.Visibility == v, host, () => PickVisibility(s, value)));
            }
            bool warn = privateRefused || s.Visibility == LobbyVisibility.Private;
            visibilityHint.text = privateRefused ? PrivateRefusedHint
                : s.Visibility == LobbyVisibility.Private ? PrivateHint
                : s.Visibility == LobbyVisibility.Public ? PublicHint : FriendsHint;
            visibilityHint.EnableInClassList("lobby__visibility-hint--warning", warn);
        }

        private void PickVisibility(MatchSettings s, LobbyVisibility value)
        {
            if (value == s.Visibility) return;
            if (!LobbyRules.MayChangeVisibility(s.Visibility, value, model.Humans))
            {
                privateRefused = true;
                refusedAtPlayers = model.Players.Count;
                refusedAtVisibility = s.Visibility;
                Show();
                return;
            }
            Change(s, x => x.Value.Visibility = value);
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

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
        private readonly VisualElement root, rows, board;
        private readonly List<VisualElement> teamRows = new List<VisualElement>();
        /// <summary>The card being dragged by its handle, or null; redraws wait until it is dropped.</summary>
        private VisualElement dragging;
        private bool redrawPending;
        private readonly VisualElement modeOptions, sizeOptions, resourceOptions, diplomacySlot, startOptions, pickTimes;
        private readonly VisualElement visibilityOptions, addAiRow;
        private readonly Label visibilityHint, addAiHint;
        private readonly Button addAi;
        private readonly Label resourcesHint;
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
        /// <summary>The host pressed a team row's + Add AI (a Normal, Random AI into that team; <see cref="TeamRules.NoTeam"/> = Solo).</summary>
        public event Action<int> AddAiToTeamClicked;
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
        private readonly Button resetButton;
        private readonly Label mapCaption;

        // Host tools (playtest 2026-11-02, Figma K1/K2).
        private readonly VisualElement serverNameBox, banConfirm;
        private readonly TextField serverNameField;
        private readonly Label banTitle, banText;
        private readonly WAR2D.UI.HoverCard hover;
        private int banTarget;

        /// <summary>The host renamed the server ("" restores the default).</summary>
        public event Action<string> ServerNameChanged;

        /// <summary>The host forced a player ready.</summary>
        public event Action<int> ForceReadyClicked;

        /// <summary>The host kicked (false) or banned (true) a player.</summary>
        public event Action<int, bool> RemoveClicked;

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
            this.root = root;
            rows = root.Q("player-rows");
            board = root.Q("team-board");
            modeOptions = root.Q("mode-options");
            sizeOptions = root.Q("size-options");
            resourceOptions = root.Q("resource-options");
            startOptions = root.Q("start-options-row");
            pickTimes = root.Q("pick-time-row");
            resourcesHint = root.Q<Label>("resources-hint");
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
            mapCaption = root.Q<Label>("lobby-map-caption");
            serverNameBox = root.Q("server-name");
            serverNameField = root.Q<TextField>("server-name-field");
            banConfirm = root.Q("lobby-ban-confirm");
            banTitle = root.Q<Label>("lobby-ban-title");
            banText = root.Q<Label>("lobby-ban-text");
            root.Q<Button>("lobby-ban-cancel").clicked += () => banConfirm.RemoveFromClassList("overlay-screen--visible");
            root.Q<Button>("lobby-ban-confirm-button").clicked += () =>
            {
                banConfirm.RemoveFromClassList("overlay-screen--visible");
                RemoveClicked?.Invoke(banTarget, true);
            };
            serverNameField.RegisterCallback<FocusOutEvent>(_ => SubmitServerName());
            serverNameField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) SubmitServerName();
            });
            hover = new WAR2D.UI.HoverCard(root);
            AttachSettingCards(root);
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
            resetButton = root.Q<Button>("lobby-reset");
            if (resetButton != null) resetButton.clicked += () => GameCore.Instance?.Cmd_ResetLobby();
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
            if (dragging != null)
            {
                redrawPending = true; // rebuilding now would drop the card from under the pointer
                return;
            }
            MatchSettings s = model.Settings;
            bool host = model.LocalIsHost;
            resetButton?.EnableInClassList("hidden", !host);
            bool teams = s.Mode == MatchMode.Teams;
            LobbyPlayer me = model.Local;

            string hostName = "the host";
            foreach (LobbyPlayer p in model.Players) if (p.IsHost) hostName = p.Nickname;
            bool isPrivate = s.Visibility == LobbyVisibility.Private;
            // Private hides the join line (the Direct addresses): nobody can join (Figma 93:343).
            string server = WAR2D.Net.ServerNames.Display(model.ServerName, hostName);
            subtitle.text = host ? (isPrivate ? "You are hosting a private match · single-player: nobody can join" : $"{server} · you are hosting · {hostAddress()}")
                : $"{server} · hosted by {hostName} · you are {me.Nickname}";
            serverNameBox.EnableInClassList("hidden", !host || isPrivate);
            if (serverNameField.focusController?.focusedElement != serverNameField) serverNameField.SetValueWithoutNotify(server);
            playersTitle.text = $"Players ({model.Players.Count} of {LobbyRules.MaxPlayers})";
            if (isPrivate) invite.AddToClassList("hidden");
            settingsTitle.text = host ? "Match settings" : "Match settings (host only)";

            root.Q("lobby")?.EnableInClassList("lobby--ffa", !teams);
            ShowRows(host, teams);
            ShowAddAi(host, isPrivate, teams);
            ShowSettings(s, host);

            if (seed.focusController?.focusedElement != seed) seed.SetValueWithoutNotify(s.Seed.ToString());
            mapCaption.text = $"{s.MapSize} × {s.MapSize} · seed {s.Seed}"; // v0.16 (Figma M2)
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

        /// <summary>
        /// The players (playtest 2026-11-05, Figma L1–L3). Teams mode: the team board, one row per team (Team 1–4, then
        /// Solo) with its cards side by side and "+ Add AI" at the end. Free-for-all: the cards two per row, no teams.
        /// </summary>
        private void ShowRows(bool host, bool teams)
        {
            rows.Clear();
            board.Clear();
            teamRows.Clear();
            Display(board, teams);
            Display(rows, !teams);
            if (!teams)
            {
                foreach (LobbyPlayer p in model.Players) rows.Add(PlayerCard(p, host, false));
                return;
            }
            for (int t = 0; t < LobbyRules.PickableTeams; t++) board.Add(TeamRow(t, host));
            board.Add(TeamRow(TeamRules.NoTeam, host));
        }

        /// <summary>One team's row: its name and player count, its cards, an empty drop zone when it has none, and the host's + Add AI.</summary>
        private VisualElement TeamRow(int team, bool host)
        {
            var row = new VisualElement { name = team == TeamRules.NoTeam ? "team-row-solo" : "team-row-" + team, userData = team };
            row.AddToClassList("lobby-team");
            var header = new VisualElement();
            header.AddToClassList("lobby-team__header");
            var title = new Label(TeamName(team));
            title.AddToClassList("lobby-team__title");
            header.Add(title);
            int count = 0;
            foreach (LobbyPlayer p in model.Players) if (p.Team == team) count++;
            var players = new Label(count == 1 ? "1 player" : $"{count} players");
            players.AddToClassList("lobby-team__count");
            header.Add(players);
            if (team == TeamRules.NoTeam)
            {
                var note = new Label("everyone is an enemy");
                note.AddToClassList("lobby-team__count");
                header.Add(note);
            }
            row.Add(header);
            var cards = new VisualElement();
            cards.AddToClassList("lobby-team__cards");
            foreach (LobbyPlayer p in model.Players) if (p.Team == team) cards.Add(PlayerCard(p, host, true));
            if (count == 0)
            {
                var empty = new Label("Empty");
                empty.AddToClassList("lobby-team__empty");
                cards.Add(empty);
            }
            if (host && model.HasFreeSeat)
            {
                int target = team;
                var add = new Button(() => AddAiToTeamClicked?.Invoke(target)) { text = "+ Add AI", name = team == TeamRules.NoTeam ? "add-ai-solo" : "add-ai-team-" + team };
                add.AddToClassList("lobby-team__add");
                cards.Add(add);
            }
            row.Add(cards);
            teamRows.Add(row);
            return row;
        }

        /// <summary>
        /// A player's card (Figma <c>Lobby / Player card</c>): colour strip; the drag handle (when this viewer may move them: the whole card drags),
        /// name, ready and the remove ×; a second line (AI: difficulty ▾ and style ▾; you: colour ▾; another human, for the
        /// host: ⋯); and, in Teams mode, the team dropdown, live for whoever may move them (the host: anyone; a joined
        /// player: themselves). Named <c>lobby-row-&lt;owner&gt;</c>.
        /// </summary>
        private VisualElement PlayerCard(LobbyPlayer p, bool host, bool teams)
        {
            int owner = p.OwnerId;
            bool you = owner == model.Local.OwnerId;
            bool movable = teams && (host || you) && !(p.IsAi && p.Difficulty == AiDifficulty.Scripted);
            var card = new VisualElement { name = "lobby-row-" + owner };
            card.AddToClassList("lobby-card");
            card.EnableInClassList("lobby-card--you", you);
            card.EnableInClassList("ai-row", p.IsAi);
            if (p.IsAi && host)
            {
                var swatch = new Button(() => AiChanged?.Invoke(owner, p.Difficulty, p.Style, model.NextFreeColour(owner, p.ColourIndex), p.Team))
                    { name = "ai-colour-" + owner, tooltip = "Change colour" };
                swatch.AddToClassList("lobby-card__strip");
                swatch.AddToClassList("player-" + (p.ColourIndex % LobbyRules.Colours + 1));
                card.Add(swatch);
            }
            else card.Add(Strip(p.ColourIndex));

            var body = new VisualElement();
            body.AddToClassList("lobby-card__body");
            card.Add(body);
            var top = new VisualElement();
            top.AddToClassList("lobby-card__top");
            body.Add(top);
            if (movable)
            {
                var handle = new Label("⠿") { name = "drag-" + owner, tooltip = "Drag onto another team" };
                handle.AddToClassList("lobby-card__handle");
                top.Add(handle);
                MakeDraggable(card, p);
            }
            var name = new Label(p.Nickname + (you ? " (you)" : ""));
            name.AddToClassList("lobby-card__name");
            top.Add(name);
            top.Add(ReadyLabel(p.IsAi || p.Ready));
            if (p.IsAi && host)
            {
                var remove = new Button(() => AiRemoved?.Invoke(owner)) { text = "×", name = "ai-remove-" + owner, tooltip = "Remove" };
                remove.AddToClassList("lobby-card__remove");
                top.Add(remove);
            }

            var sub = new VisualElement();
            sub.AddToClassList("lobby-card__sub");
            body.Add(sub);
            if (p.IsAi && host)
            {
                sub.Add(PlayerNameLabel.Tag("AI"));
                var difficulty = Pill("ai-difficulty-" + owner, AiLobbyRules.DifficultyName(p.Difficulty) + " ▾");
                difficulty.clicked += () => Menu(difficulty, new[] { AiDifficulty.Easy, AiDifficulty.Normal, AiDifficulty.Hard }, p.Difficulty, AiLobbyRules.DifficultyName,
                    d => AiChanged?.Invoke(owner, d, p.Style, p.ColourIndex, p.Team));
                sub.Add(difficulty);
                var style = Pill("ai-style-" + owner, AiLobbyRules.StyleName(p.Style) + " ▾");
                style.clicked += () => Menu(style, new[] { AiStyle.Random, AiStyle.Rusher, AiStyle.Turtle, AiStyle.Expander, AiStyle.Balanced }, p.Style, AiLobbyRules.StyleName,
                    st => AiChanged?.Invoke(owner, p.Difficulty, st, p.ColourIndex, p.Team));
                sub.Add(style);
            }
            else if (p.IsAi)
            {
                sub.Add(PlayerNameLabel.Tag(PlayerNameLabel.TagText(p.Difficulty)));
                sub.Add(SubText("· " + AiLobbyRules.StyleName(p.Style), "ai-style-" + owner));
            }
            else
            {
                sub.Add(SubText(you ? (p.IsHost ? "You · host" : "You") : p.IsHost ? "Host" : "Joined"));
                if (you) sub.Add(ColourPill());
                else if (host && !p.IsHost) sub.Add(PlayerMenu(p));
            }

            if (teams)
            {
                var team = new Button { name = "team-" + owner, text = TeamName(p.Team) + " ▾" };
                team.AddToClassList("lobby-card__team");
                if (movable) team.clicked += () => TeamMenu(team, p);
                else
                {
                    team.SetEnabled(false);
                    team.pickingMode = PickingMode.Ignore;
                }
                body.Add(team);
            }
            return card;
        }

        /// <summary>The team dropdown: Team 1–4 and Solo, the current one checked.</summary>
        private void TeamMenu(VisualElement anchor, LobbyPlayer p)
        {
            var teams = new int[LobbyRules.PickableTeams + 1];
            for (int t = 0; t < LobbyRules.PickableTeams; t++) teams[t] = t;
            teams[LobbyRules.PickableTeams] = TeamRules.NoTeam;
            Menu(anchor, teams, p.Team, TeamName, t => MoveToTeam(p, t));
        }

        /// <summary>Moves a player to a team: yourself through your own team choice, an AI through its settings, another human as the host.</summary>
        private void MoveToTeam(LobbyPlayer p, int team)
        {
            if (p.Team == team) return;
            if (p.OwnerId == model.Local.OwnerId) TeamClicked?.Invoke(team);
            else if (!model.LocalIsHost) return;
            else if (p.IsAi) AiChanged?.Invoke(p.OwnerId, p.Difficulty, p.Style, p.ColourIndex, team);
            else HostTeamClicked?.Invoke(p.OwnerId, team);
        }

        /// <summary>Your colour ▾ on your own card: every palette colour, the ones other humans hold disabled (an AI gives its colour up).</summary>
        private Button ColourPill()
        {
            var pill = Pill("own-colour", "colour ▾");
            pill.clicked += () =>
            {
                var menu = new GenericDropdownMenu();
                for (int c = 0; c < LobbyRules.Colours; c++)
                {
                    int colour = c;
                    string label = $"Colour {c + 1}" + (model.Local.ColourIndex == c ? " (yours)" : "");
                    if (model.IsColourTakenByHuman(c) && model.Local.ColourIndex != c) menu.AddDisabledItem(label + " (taken)", false);
                    else menu.AddItem(label, model.Local.ColourIndex == c, () => { if (colour != model.Local.ColourIndex) ColourClicked?.Invoke(colour); });
                }
                menu.DropDown(pill.worldBound, pill, false);
            };
            return pill;
        }

        /// <summary>Pixels the pointer moves before a press on a card becomes a drag (a shorter move is still a click).</summary>
        private const float DragThreshold = 4f;

        /// <summary>
        /// Drag a card by any part of it but its buttons (playtest 2026-11-06: only the ⠿ handle worked). Once the pointer
        /// has moved <see cref="DragThreshold"/>, the card lifts out of the scrolling board onto the lobby's top layer, so no
        /// row or panel draws over it, and follows the pointer; the row under it lights up, and letting go over another
        /// team's row moves the player there (the same as the dropdown). Redraws wait until the drag ends.
        /// </summary>
        private void MakeDraggable(VisualElement card, LobbyPlayer p)
        {
            UnityEngine.Vector2 start = default, grab = default;
            int pointer = -1;
            bool pressed = false, lifted = false, lifting = false;
            card.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0 || dragging != null || InButton(e.target as VisualElement, card)) return;
                pressed = true;
                lifted = false;
                pointer = e.pointerId;
                start = e.position;
                grab = (UnityEngine.Vector2)e.position - card.worldBound.position;
                card.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            card.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!pressed || e.pointerId != pointer) return;
                UnityEngine.Vector2 at = e.position;
                if (!lifted)
                {
                    if ((at - start).sqrMagnitude < DragThreshold * DragThreshold) return;
                    Lift();
                }
                Place(at);
                VisualElement over = RowAt(at);
                foreach (VisualElement row in teamRows) row.EnableInClassList("lobby-team--drop", row == over && (int)row.userData != p.Team);
            });
            void Lift()
            {
                float width = card.resolvedStyle.width, height = card.resolvedStyle.height;
                lifted = true;
                dragging = card;
                lifting = true; // leaving the board drops the capture: that isn't the end of the drag
                card.RemoveFromHierarchy();
                root.Add(card);
                card.style.position = Position.Absolute;
                card.style.width = width;
                card.style.height = height;
                card.pickingMode = PickingMode.Position;
                card.AddToClassList("lobby-card--dragging");
                card.CapturePointer(pointer);
                lifting = false;
            }
            void Place(UnityEngine.Vector2 at)
            {
                UnityEngine.Vector2 local = root.WorldToLocal(at - grab);
                card.style.left = local.x;
                card.style.top = local.y;
            }
            void End(UnityEngine.Vector2 at, bool drop)
            {
                if (!pressed || lifting) return;
                pressed = false;
                if (card.HasPointerCapture(pointer)) card.ReleasePointer(pointer);
                if (!lifted) return; // a click, not a drag
                lifted = false;
                dragging = null;
                VisualElement over = drop ? RowAt(at) : null;
                foreach (VisualElement row in teamRows) row.RemoveFromClassList("lobby-team--drop");
                card.RemoveFromHierarchy();
                redrawPending = false;
                if (over != null && (int)over.userData != p.Team) MoveToTeam(p, (int)over.userData);
                Show(); // the board is rebuilt with the card back in its row (or in its new one once the server agrees)
            }
            card.RegisterCallback<PointerUpEvent>(e => { if (e.pointerId == pointer) End(e.position, true); });
            card.RegisterCallback<PointerCaptureOutEvent>(e => End(default, false));
        }

        /// <summary>True when <paramref name="element"/> is a button (or inside one) on the card: those keep their clicks.</summary>
        private static bool InButton(VisualElement element, VisualElement card)
        {
            for (VisualElement e = element; e != null && e != card; e = e.parent)
                if (e is Button) return true;
            return false;
        }

        private VisualElement RowAt(UnityEngine.Vector2 position)
        {
            foreach (VisualElement row in teamRows) if (row.worldBound.Contains(position)) return row;
            return null;
        }

        private static void Display(VisualElement element, bool shown)
        {
            if (element != null) element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static VisualElement Strip(int colourIndex)
        {
            var strip = new VisualElement();
            strip.AddToClassList("lobby-card__strip");
            strip.AddToClassList("player-" + (colourIndex % LobbyRules.Colours + 1));
            return strip;
        }

        private static Label SubText(string text, string name = null)
        {
            var label = new Label(text) { name = name };
            label.AddToClassList("lobby-card__sub-text");
            return label;
        }

        private static Label ReadyLabel(bool ready)
        {
            var label = new Label(ready ? "Ready" : "Not ready");
            label.AddToClassList("lobby-row__ready");
            label.EnableInClassList("lobby-row__ready--yes", ready);
            return label;
        }

        /// <summary>
        /// The host's ⋯ button on another human's card (Figma K1): Force ready (while not ready), Kick, and Ban, which asks
        /// first (K2).
        /// </summary>
        private Button PlayerMenu(LobbyPlayer p)
        {
            int owner = p.OwnerId;
            string name = p.Nickname;
            var button = new Button { name = "player-menu-" + owner, text = "⋯", tooltip = "Host actions" };
            button.AddToClassList("lobby-card__menu");
            button.clicked += () =>
            {
                var menu = new GenericDropdownMenu();
                if (p.Ready) menu.AddDisabledItem("Force ready (already ready)", false);
                else menu.AddItem("Force ready", false, () => ForceReadyClicked?.Invoke(owner));
                menu.AddItem("Kick", false, () => RemoveClicked?.Invoke(owner, false));
                menu.AddItem("Ban…", false, () => AskBan(owner, name));
                menu.DropDown(button.worldBound, button, false);
            };
            return button;
        }

        /// <summary>Opens the Ban confirmation for a player.</summary>
        private void AskBan(int owner, string name)
        {
            banTarget = owner;
            banTitle.text = $"Ban {name}?";
            banText.text = $"{name} leaves the lobby and can't rejoin {WAR2D.Net.ServerNames.Display(model.ServerName, HostName())} while it is open. Kick instead lets them come back.";
            banConfirm.AddToClassList("overlay-screen--visible");
        }

        private string HostName()
        {
            foreach (LobbyPlayer p in model.Players) if (p.IsHost) return p.Nickname;
            return null;
        }

        private void SubmitServerName()
        {
            if (!model.LocalIsHost) return;
            string typed = serverNameField.value?.Trim() ?? "";
            string current = WAR2D.Net.ServerNames.Display(model.ServerName, HostName());
            if (typed == current) return;
            ServerNameChanged?.Invoke(typed == WAR2D.Net.ServerNames.Display(null, HostName()) ? "" : typed);
        }

        /// <summary>A hover card on every match setting (playtest 2026-11-02, Figma K1), with <see cref="LobbySettingHelp"/>'s words.</summary>
        private void AttachSettingCards(VisualElement root)
        {
            void Attach(VisualElement target, Func<WAR2D.UI.HoverCardContent> content)
            {
                if (target != null) hover.Attach(target, content, WAR2D.UI.HoverCard.Side.Left);
            }
            Attach(sizeOptions?.parent, LobbySettingHelp.MapSize);
            Attach(seed?.parent, LobbySettingHelp.Seed);
            Attach(resourceOptions?.parent, LobbySettingHelp.Resources);
            Attach(startOptions?.parent, LobbySettingHelp.StartOptions);
            Attach(pickTimes?.parent, LobbySettingHelp.PickTime);
            Attach(visibilityOptions?.parent, LobbySettingHelp.Visibility);
            Attach(modeOptions?.parent, LobbySettingHelp.Mode);
            Attach(diplomacySlot, LobbySettingHelp.Diplomacy);
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
        private void ShowAddAi(bool host, bool isPrivate, bool teams)
        {
            addAiRow.EnableInClassList("hidden", !host || !model.HasFreeSeat || teams); // Teams mode: + Add AI on each row instead
            addAiHint.text = isPrivate ? "Add AIs to play against; you need two teams to start." : "AIs are always ready. Shown while a seat is free (8 at most).";
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
            if (host || !s.InfiniteResources)
                foreach (float amount in lobby.StartingResources)
                {
                    float value = amount;
                    resourceOptions.Add(Widgets.Option("resources-" + amount, amount.ToString("0"), !s.InfiniteResources && s.StartingResources.Equals(amount), host, () => Change(s, x => { x.StartingResources = value; x.InfiniteResources = false; })));
                }
            if (host || s.InfiniteResources) resourceOptions.Add(Widgets.Option("resources-infinite", "Infinite", s.InfiniteResources, host, () => Change(s, x => x.InfiniteResources = true)));
            resourcesHint?.EnableInClassList("hidden", !s.InfiniteResources);
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
            public bool InfiniteResources { set => Value.InfiniteResources = value; }
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

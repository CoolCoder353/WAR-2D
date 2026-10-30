using WAR2D.Dev.Logging;
using System.Linq;
using Config;
using Mirror;
using UnityEngine;
using UnityEngine.UIElements;
using WAR2D.Client;
using WAR2D.Net;

namespace WAR2D.UI
{
    /// <summary>
    /// The Main_Menu scene's UI (<c>Assets/UI/Menus/Menu.uxml</c>): shows the main menu, the Play screen
    /// (host or join, with the connecting and failed states) or the lobby, from the network state, and
    /// sends the lobby's commands. Lives on the <see cref="UIDocument"/> in Main_Menu.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MenuController : MonoBehaviour
    {
        private const float LobbyRefreshSeconds = 0.1f;

        private enum Screen { Main, Play, Lobby, Replays, Viewer, None }

        private VisualElement mainScreen, playScreen, lobbyScreen, replaysScreen, viewerScreen;
        private ReplaysController replays;
        private ReplayViewerController viewer;
        private bool wantsReplays;
        private MainMenuController main;
        private HostJoinController play;
        private LobbyController lobby;
        private SettingsController settings;
        private VisualElement root;
        private VisualElement hoverRoot; // the root the hover sounds are registered on (OnEnable can run again)
        private readonly MapPreview preview = new MapPreview();
        private bool wantsPlay;
        private LobbyListController list;
        private WAR2D.Net.Steam.SteamJoinFlow joinFlow;
        private string joiningAddress;
        private bool wasConnected;
        private float lobbyTimer;
        private int reachabilityVersion;

        /// <summary>The lobby's data.</summary>
        public LobbyModel Lobby { get; } = new LobbyModel();

        /// <summary>The Replays screen's data.</summary>
        public ReplaysModel Replays { get; } = new ReplaysModel();

        /// <summary>The map preview (tests read its texture).</summary>
        public MapPreview Preview => preview;

        /// <summary>The settings screen.</summary>
        public SettingsController Settings => settings;

        /// <summary>Raised by the main menu's Settings button.</summary>
        public event System.Action SettingsRequested;

        private void OnEnable()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            if (hoverRoot != root) { Widgets.HoverSounds(root); hoverRoot = root; }
            Palettes.ApplyTo(root);
            Palettes.Changed += ApplyPalette;
            root.Query<TemplateContainer>().ForEach(t => t.pickingMode = PickingMode.Ignore);
            VisualElement GetScreen(string instanceName, string innerName)
            {
                TemplateContainer instance = root.Q<TemplateContainer>(instanceName);
                if (instance != null && instance.contentContainer != null)
                {
                    VisualElement inner = instance.contentContainer.Q<VisualElement>(innerName);
                    if (inner != null) return inner;
                }
                return root.Q<VisualElement>(innerName) ?? root.Q<VisualElement>(instanceName);
            }

            mainScreen = GetScreen("main-menu-instance", "main-menu");
            playScreen = GetScreen("host-join-instance", "host-join");
            lobbyScreen = GetScreen("lobby-instance", "lobby");
            replaysScreen = GetScreen("replays-instance", "replays");
            viewerScreen = GetScreen("replay-viewer-instance", "replay-viewer");
            if (mainScreen == null || playScreen == null || lobbyScreen == null)
            {
                WarLog.Error(LogCategory.UI, $"MenuController: failed to bind UI screens. root={root.name}; main={mainScreen != null}; play={playScreen != null}; lobby={lobbyScreen != null}; childNames={string.Join(", ", root.Children().Select(c => c.name))}");
            }
            else
            {
                WarLog.Info(LogCategory.UI, $"MenuController: bound screens main={mainScreen.name}, play={playScreen.name}, lobby={lobbyScreen.name}");
            }

            main = new MainMenuController(root);
            main.PlayClicked += () => { wantsPlay = true; wantsReplays = false; Show(Screen.Play); };
            main.ReplaysClicked += () => { wantsReplays = true; replays.ShowError(null); Replays.Refresh(); Show(Screen.Replays); };
            replays = new ReplaysController(root, Replays);
            replays.BackClicked += () => wantsReplays = false;
            replays.DeleteConfirmed += path => { WAR2D.Net.Replay.ReplayLibrary.Delete(path); Replays.Refresh(); };
            replays.WatchClicked += path =>
            {
                if (WAR2D.Client.Replay.ReplayPlayer.Open(path, out string error) == null) { replays.ShowError("Can't play this replay: " + error); return; }
                replays.ShowError(null);
                viewer.Opened();
                Show(Screen.Viewer);
            };
            viewer = new ReplayViewerController(root);
            viewer.LeaveClicked += () => { WAR2D.Client.Replay.ReplayPlayer.Instance?.Close(); wantsReplays = true; Replays.Refresh(); };
            settings = new SettingsController(root);
            main.SettingsClicked += () => { settings.Open(); SettingsRequested?.Invoke(); };
            main.QuitClicked += () => GameManager.Instance?.QuitGame();

            play = new HostJoinController(root);
            play.BackClicked += () => { wantsPlay = false; play.ShowIdle(); Show(Screen.Main); };
            play.HostClicked += Host;
            play.JoinClicked += Join;
            play.SetDirectAvailable(GameManager.DirectAllowed);
            play.SetDirectExpanded(!WAR2D.Net.Steam.SteamServices.Available);
            joinFlow = new WAR2D.Net.Steam.SteamJoinFlow(WAR2D.Net.Steam.SteamServices.Backend);
            list = new LobbyListController(play, joinFlow);
            list.RouteChosen += JoinRoute;
            if (WAR2D.Net.Steam.SteamServices.Available)
                joinFlow.ListenForInvites(lobbyId =>
                {
                    if (NetworkClient.active || NetworkServer.active) { WarLog.Info(LogCategory.UI, "invite ignored: already in a game."); return; }
                    wantsPlay = true;
                    list.Join(lobbyId, "your friend");
                });
            play.CancelClicked += () => { joiningAddress = null; GameManager.Instance?.StopClient(); play.ShowIdle(); };
            play.RejoinClicked += Rejoin;
            play.RejoinCancelClicked += () => { rejoining = false; GameManager.Instance?.StopClient(); };
            play.RejoinDismissClicked += WAR2D.Net.Rejoin.RejoinTicket.DismissRefusal;
            rejoinOffered = false; // a menu that opens with a Rejoin on offer (the connection just dropped) goes straight to Play

            GameConfigData config = ConfigLoader.LoadConfig();
            string lan = MenuModel.LocalAddress();
            lobby = new LobbyController(root, Lobby, preview, config.Lobby, config.Match.Map, () => HostReachability.Describe(lan));
            lobby.SettingsChanged += s => GameCore.Instance?.Cmd_SetMatchSettings(s);
            lobby.RerollClicked += () => GameCore.Instance?.Cmd_RerollMap();
            lobby.ColourClicked += c => GameCore.Instance?.Cmd_SetColour((byte)c);
            lobby.TeamClicked += t => GameCore.Instance?.Cmd_SetOwnTeam(t);
            lobby.HostTeamClicked += (owner, team) => GameCore.Instance?.Cmd_SetTeam((uint)owner, team);
            lobby.ReadyClicked += r => GameCore.Instance?.Cmd_SetReady(r);
            lobby.StartClicked += () => GameCore.Instance?.Cmd_StartGame();
            lobby.AddAiClicked += () => GameCore.Instance?.Cmd_AddAi(AiDifficulty.Normal, AiStyle.Random);
            lobby.AiChanged += (owner, difficulty, style, colour, team) => GameCore.Instance?.Cmd_SetAi(owner, difficulty, style, colour, team);
            lobby.AiRemoved += owner => GameCore.Instance?.Cmd_RemoveAi(owner);
            lobby.InviteClicked += WAR2D.Net.Steam.SteamHostLobby.OpenInvite;
            lobby.LeaveClicked += () => { wantsPlay = true; GameManager.Instance?.LeaveLobby(); };
            Show(Screen.Main);
        }

        private void ApplyPalette() => Palettes.ApplyTo(root);

        private void OnDisable()
        {
            Palettes.Changed -= ApplyPalette;
            joinFlow?.StopListening();
            settings?.Dispose();
            lobby?.Dispose();
            preview.Dispose();
        }

        private void Host()
        {
            MenuBattle.Instance?.TearDown(); // never shares a simulation with a real match
            joiningAddress = null;
            GameManager manager = GameManager.Instance;
            if (manager == null) return;
            manager.HostServer();
            // Open the port on the router so players on other networks can join (external playtests).
            if (NetworkServer.active && !WAR2D.Net.Steam.SteamServices.Available) HostReachability.Start(manager.DefaultPort);
            if (NetworkServer.active && GameCore.Instance != null)
                WAR2D.Net.Steam.SteamHostLobby.Open(MenuModel.Nickname, GameCore.Instance.Settings, 1);
        }

        private void Join(string address)
        {
            MenuBattle.Instance?.TearDown();
            joiningAddress = address;
            wasConnected = false;
            play.ShowConnecting(address);
            GameManager.Instance?.ConnectToServer(address);
        }

        private void JoinRoute(WAR2D.Net.Steam.ConnectionRoute route, string label)
        {
            MenuBattle.Instance?.TearDown();
            joiningAddress = route.ToString();
            wasConnected = false;
            play.ShowConnecting(label);
            GameManager.Instance?.Connect(route);
        }

        // v0.15 item 19: the Rejoin card's state.
        private bool rejoining, rejoinOffered;

        private void Rejoin()
        {
            GameManager manager = GameManager.Instance;
            if (manager == null || NetworkClient.active || NetworkServer.active) return;
            MenuBattle.Instance?.TearDown();
            play.ShowRejoinFailed(null);
            rejoining = manager.Rejoin() && NetworkClient.active;
            if (!rejoining) play.ShowRejoinFailed("Couldn't reconnect. Try again while the timer runs.");
        }

        /// <summary>Shows or hides the Rejoin card; the first frame a Rejoin is on offer opens the Play screen.</summary>
        private void UpdateRejoin()
        {
            GameManager manager = GameManager.Instance;
            bool available = manager != null && manager.CanRejoin && !NetworkServer.active;
            if (available && !rejoinOffered) { wantsPlay = true; wantsReplays = false; }
            rejoinOffered = available;
            // An attempt that ended before the match came back failed (the server refused the ticket, or nobody answered).
            if (rejoining && !NetworkClient.active)
            {
                rejoining = false;
                if (available) play.ShowRejoinFailed("Couldn't get back into the match. Try again while the timer runs.");
            }
            // v0.15.1: the server refused the ticket and said why; Rejoin is no longer offered.
            if (!available && WAR2D.Net.Rejoin.RejoinTicket.Refusal is WAR2D.Net.Rejoin.RejoinRefusal refusal)
            {
                play.ShowRejoinRefused(ReconnectText.RefusalTail(refusal), RejoinMatch.HostName, RejoinMatch.HostColour);
                return;
            }
            play.ShowRejoin(available, available ? manager.RejoinSecondsLeft : 0f, WAR2D.Net.Rejoin.RejoinTicket.Window,
                rejoining, RejoinMatch.HostName, RejoinMatch.HostColour);
        }

        private void Update()
        {
            UpdateRejoin();
            if (playScreen.ClassListContains("menu-screen--visible") && !NetworkClient.active) list.Tick(Time.unscaledDeltaTime);
            lobby.SetInviteVisible(NetworkServer.active && WAR2D.Net.Steam.SteamHostLobby.CanInvite);
            bool connected = NetworkClient.isConnected;
            if (connected) wasConnected = true;

            // A join that ended before reaching the lobby failed: nobody answered, or the server closed the
            // connection (its match has started or it is full).
            if (joiningAddress != null && !NetworkClient.active)
            {
                if (!wasConnected) play.ShowFailed(joiningAddress);
                else play.ShowRefused(joiningAddress);
                joiningAddress = null;
            }

            GameCore core = GameCore.Instance;
            if (core != null && NetworkServer.active)
                WAR2D.Net.Steam.SteamHostLobby.Current?.Update(core.CurrentState, core.Settings, core.SeatCount);
            if (WAR2D.Client.Replay.ReplayPlayer.Instance != null)
            {
                viewer.Tick(Time.unscaledDeltaTime);
                Show(Screen.Viewer);
                return;
            }
            bool inLobby = connected && core != null && core.CurrentState == GameState.Lobby && NetworkClient.localPlayer != null;
            bool inMatch = connected && core != null && core.CurrentState != GameState.Lobby;
            if (inLobby)
            {
                joiningAddress = null;
                lobbyTimer -= Time.unscaledDeltaTime;
                if (lobbyTimer <= 0f)
                {
                    lobbyTimer = LobbyRefreshSeconds;
                    Lobby.Pull();
                    MatchSettings s = Lobby.Settings;
                    preview.Request(s.MapSize, s.Seed, ConfigLoader.LoadConfig().Match.Map.GemChance);
                }
                preview.Update();
                if (reachabilityVersion != HostReachability.Version)
                {
                    reachabilityVersion = HostReachability.Version;
                    lobby.Refresh();
                }
                Show(Screen.Lobby);
            }
            else if (inMatch) Show(Screen.None); // actual match flow; hide menu overlays
            else if (wantsPlay || joiningAddress != null) Show(Screen.Play);
            else if (wantsReplays) Show(Screen.Replays);
            else Show(Screen.Main);
        }

        private void Show(Screen screen)
        {
            if (mainScreen == null || playScreen == null || lobbyScreen == null)
            {
                WarLog.Error(LogCategory.UI, $"MenuController: cannot show screen {screen}; some menu elements are null. main={mainScreen != null}; play={playScreen != null}; lobby={lobbyScreen != null}");
                return;
            }

            string joinValue = joiningAddress ?? "<null>";
            string coreState = GameCore.Instance != null ? GameCore.Instance.CurrentState.ToString() : "<null>";
            WarLog.Debug(LogCategory.UI, $"MenuController: changing screen to {screen} (wantsPlay={wantsPlay}, joiningAddress={joinValue}, connected={NetworkClient.isConnected}, active={NetworkClient.active}, coreState={coreState})");
            mainScreen.EnableInClassList("menu-screen--visible", screen == Screen.Main);
            playScreen.EnableInClassList("menu-screen--visible", screen == Screen.Play);
            lobbyScreen.EnableInClassList("menu-screen--visible", screen == Screen.Lobby);
            replaysScreen?.EnableInClassList("menu-screen--visible", screen == Screen.Replays);
            viewerScreen?.EnableInClassList("menu-screen--visible", screen == Screen.Viewer);
        }
    }
}

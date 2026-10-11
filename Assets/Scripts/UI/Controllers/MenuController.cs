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

        private enum Screen { Main, Play, Lobby, None }

        private VisualElement mainScreen, playScreen, lobbyScreen;
        private MainMenuController main;
        private HostJoinController play;
        private LobbyController lobby;
        private SettingsController settings;
        private VisualElement root;
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

        /// <summary>The map preview (tests read its texture).</summary>
        public MapPreview Preview => preview;

        /// <summary>The settings screen.</summary>
        public SettingsController Settings => settings;

        /// <summary>Raised by the main menu's Settings button.</summary>
        public event System.Action SettingsRequested;

        private void OnEnable()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            Palettes.ApplyTo(root);
            Palettes.Changed += ApplyPalette;
            root.Query<TemplateContainer>().ForEach(t => t.pickingMode = PickingMode.Ignore);
            mainScreen = root.Q("main-menu");
            playScreen = root.Q("host-join");
            lobbyScreen = root.Q("lobby");

            main = new MainMenuController(root);
            main.PlayClicked += () => wantsPlay = true;
            settings = new SettingsController(root);
            main.SettingsClicked += () => { settings.Open(); SettingsRequested?.Invoke(); };
            main.QuitClicked += () => GameManager.Instance?.QuitGame();

            play = new HostJoinController(root);
            play.BackClicked += () => { wantsPlay = false; play.ShowIdle(); };
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
                    if (NetworkClient.active || NetworkServer.active) { Debug.Log("[Steam] invite ignored: already in a game."); return; }
                    wantsPlay = true;
                    list.Join(lobbyId, "your friend");
                });
            play.CancelClicked += () => { joiningAddress = null; GameManager.Instance?.StopClient(); play.ShowIdle(); };

            GameConfigData config = ConfigLoader.LoadConfig();
            string lan = MenuModel.LocalAddress();
            lobby = new LobbyController(root, Lobby, preview, config.Lobby, () => HostReachability.Describe(lan));
            lobby.SettingsChanged += s => GameCore.Instance?.Cmd_SetMatchSettings(s);
            lobby.RerollClicked += () => GameCore.Instance?.Cmd_RerollMap();
            lobby.ColourClicked += c => GameCore.Instance?.Cmd_SetColour((byte)c);
            lobby.StartSiteClicked += site => GameCore.Instance?.Cmd_SetStartSite(site);
            lobby.TeamClicked += t => GameCore.Instance?.Cmd_SetOwnTeam(t);
            lobby.HostTeamClicked += (owner, team) => GameCore.Instance?.Cmd_SetTeam((uint)owner, team);
            lobby.ReadyClicked += r => GameCore.Instance?.Cmd_SetReady(r);
            lobby.StartClicked += () => GameCore.Instance?.Cmd_StartGame();
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

        private void Update()
        {
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
                WAR2D.Net.Steam.SteamHostLobby.Current?.Update(core.CurrentState, core.Settings, NetworkServer.connections.Count);
            bool inLobby = connected && core != null && core.CurrentState == GameState.Lobby && NetworkClient.localPlayer != null;
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
            else if (connected || (NetworkClient.active && joiningAddress == null)) Show(Screen.None); // starting or leaving a match
            else Show(wantsPlay || joiningAddress != null ? Screen.Play : Screen.Main);
        }

        private void Show(Screen screen)
        {
            mainScreen.EnableInClassList("menu-screen--visible", screen == Screen.Main);
            playScreen.EnableInClassList("menu-screen--visible", screen == Screen.Play);
            lobbyScreen.EnableInClassList("menu-screen--visible", screen == Screen.Lobby);
        }
    }
}

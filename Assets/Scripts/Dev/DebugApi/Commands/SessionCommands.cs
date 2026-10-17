using Mirror;
using Newtonsoft.Json.Linq;
using WAR2D.Net.Steam;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>Host, join, start, leave and quit.</summary>
    public static class SessionCommands
    {
        public static void Register(DebugRegistry r)
        {
            r.Add("host", DebugRole.Any, "host: host a Direct (KCP) lobby", _ =>
            {
                if (NetworkServer.active || NetworkClient.active) throw new DebugError("already hosting or joined");
                Manager().HostServer();
                return Wait(() => GameCore.Instance != null ? "hosting" : null, 10);
            });
            r.Add("join", DebugRole.Any, "join route: join 'host:port' or 'steam:<id>'", a =>
            {
                if (NetworkClient.active) throw new DebugError("already joined");
                string route = DebugArgs.Str(a, "route");
                if (!ConnectionRoute.TryParse(route, GameManager.Instance != null ? GameManager.Instance.DefaultPort : (ushort)7778, out ConnectionRoute parsed)) throw new DebugError($"'{route}' is not a route");
                Manager().Connect(parsed);
                return Wait(() => NetworkClient.isConnected && NetworkClient.localPlayer != null ? "joined" : null, 15);
            });
            r.Add("leave", DebugRole.Any, "leave: stop hosting or leave", _ => { Manager().LeaveLobby(); return "left"; });
            r.Add("start", DebugRole.Server, "start [size] [seed] [mode] [options] [pick] [team]: set match settings (pick: seconds, 0 random, -1 unlimited; team: put every player on that team) and start the match", a =>
            {
                GameCore core = GameCore.Instance ?? throw new DebugError("no lobby");
                if (core.CurrentState != GameState.Lobby) throw new DebugError("not in the lobby");
                MatchSettings s = core.Settings;
                if (DebugArgs.Has(a, "size")) s.MapSize = DebugArgs.Int(a, "size");
                if (DebugArgs.Has(a, "seed")) s.Seed = (uint)DebugArgs.Int(a, "seed");
                if (DebugArgs.Has(a, "mode")) s.Mode = DebugArgs.Enum<MatchMode>(a, "mode");
                if (DebugArgs.Has(a, "options")) s.StartOptions = DebugArgs.Int(a, "options");
                if (DebugArgs.Has(a, "pick")) s.StartPickSeconds = DebugArgs.Int(a, "pick");
                if (DebugArgs.Has(a, "team"))
                {
                    if (s.Mode != MatchMode.Teams) throw new DebugError("team needs mode=Teams");
                    foreach (NetworkIdentity identity in core.ServerPlayers.Keys) if (identity != null) identity.GetComponent<ClientPlayer>().lobbyTeam = DebugArgs.Int(a, "team");
                }
                if (!MatchSettingsRules.IsValid(s, Config.ConfigLoader.LoadConfig().Lobby)) throw new DebugError($"size must be one of {string.Join(", ", Config.ConfigLoader.LoadConfig().Lobby.MapSizes)}, seed non-zero, options and pick from the lobby's lists");
                if (!Config.ConfigLoader.IsValid) throw new DebugError("GameConfig.xml is invalid");
                core.DevStartMatch(s);
                return Wait(() => WorldStateManager.Instance != null && WorldStateManager.Instance.Map != null ? "placing HQs" : null, 30);
            });
            r.Add("quit", DebugRole.Any, "quit: quit the process (refused in the editor)", _ =>
            {
                if (UnityEngine.Application.isEditor) throw new DebugError("won't quit the editor");
                UnityEngine.Application.Quit();
                return "quitting";
            });
        }

        internal static DebugWait Wait(System.Func<object> poll, float seconds) => new DebugWait { Poll = poll, TimeoutSeconds = seconds };

        private static GameManager Manager() => GameManager.Instance ?? throw new DebugError("no GameManager (not in a scene with one)");
    }
}

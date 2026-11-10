using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mirror;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using WAR2D.Dev.Logging;
using WAR2D.Client;
using WAR2D.Net.Replication;
using WAR2D.Sim;
using WAR2D.World;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>Read-only API commands. Client processes answer only from what the client holds.</summary>
    public static class ReadCommands
    {
        public static void Register(DebugRegistry r)
        {
            r.Add("info", DebugRole.Any, "info: role, version, scene, game state, tick", _ => new
            {
                version = UnityEngine.Application.version,
                editor = UnityEngine.Application.isEditor,
                server = NetworkServer.active,
                client = NetworkClient.active,
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                state = GameCore.Instance != null ? GameCore.Instance.CurrentState.ToString() : null,
                tick = SimContext.Current != null ? SimContext.Current.Clock.Tick : -1,
                paused = SimContext.Current?.Pause.Paused ?? false,
                logFile = LogFile.LastPath,
            });
            r.Add("state", DebugRole.Any, "state: game state, countdown, map", _ => new
            {
                state = GameCore.Instance?.CurrentState.ToString(),
                map = WorldStateManager.Instance == null ? null : new { size = WorldStateManager.Instance.MapSize, seed = WorldStateManager.Instance.MapSeed },
                tick = SimContext.Current?.Clock.Tick ?? -1,
                units = SimContext.Current?.Clock.UnitCount ?? ClientWorld.Instance?.KnownCount ?? 0,
            });
            r.Add("players", DebugRole.Server, "players: every player's state, team, colour and resources", _ => Players());
            r.Add("units", DebugRole.Any, "units [owner] [type] [box] [limit=200]: units (server: all; client: what it knows); count is every match, truncated says the list stops at limit", a =>
                NetworkServer.active ? Units(Require(SettledSnapshot.Current), a) : ClientUnits(a));
            r.Add("unit", DebugRole.Server, "unit id: one unit's full state", a => Unit(Require(SettledSnapshot.Current), DebugArgs.Int(a, "id")));
            r.Add("buildings", DebugRole.Any, "buildings [owner] [type]: buildings (server: all, with queued = placements waiting for the next tick; client: what it knows)", a => Buildings(a));
            r.Add("tiles", DebugRole.Any, "tiles box: ASCII map of a box (. ground, # rock, * gem, B border, W wall, w blueprint)", a =>
            {
                MapGrid grid = WorldStateManager.Instance?.Map?.Grid ?? throw new DebugError("no map loaded");
                var (min, max) = DebugArgs.Box(a, "box");
                return AsciiTiles(grid, min, max);
            });
            r.Add("fog", DebugRole.Server, "fog owner box: an owner's fog grid, one character per fog cell (# visible, + explored, space unknown)", a =>
            {
                SimContext sim = SimContext.Current ?? throw new DebugError("no match");
                var (min, max) = DebugArgs.Box(a, "box");
                return AsciiFog(sim.Data, sim.VisionOf(DebugArgs.Int(a, "owner")), min, max);
            });
            r.Add("orders", DebugRole.Server, "orders: live move orders, their goals and follower counts", _ =>
            {
                SimContext sim = SimContext.Current ?? throw new DebugError("no match");
                sim.World.EntityManager.CompleteAllTrackedJobs(); // dev command only: the order arrays are job-written
                SimData data = sim.Data;
                var list = new List<object>();
                for (int o = 0; o < data.OrderLive.Length; o++)
                    if (data.OrderLive[o] != 0)
                        list.Add(new { order = o, goal = new[] { data.OrderGoal[o].x, data.OrderGoal[o].y }, followers = data.OrderFollowers[o] });
                return list;
            });
            r.Add("perf", DebugRole.Server, "perf: last tick and replication timings", _ => new
            {
                tickMs = SimTiming.LastMainThreadMs,
                boundaryWaitMs = SimTiming.LastBoundaryWaitMs,
                replicationMs = ReplicationService.LastMilliseconds,
                aiMs = SimTiming.LastAiMs,
                aiP95Ms = WorldStateManager.Instance?.AiRunner?.RecentMs().p95 ?? 0,
                aiMaxMs = WorldStateManager.Instance?.AiRunner?.RecentMs().max ?? 0,
            });
            r.Add("log", DebugRole.Any, "log [max] [category] [level] [after]: recent log entries", a =>
            {
                LogCategory? cat = DebugArgs.Has(a, "category") ? DebugArgs.Enum<LogCategory>(a, "category") : (LogCategory?)null;
                return WarLog.Tail(DebugArgs.Int(a, "max", 100), cat, DebugArgs.Enum<LogLevel>(a, "level", LogLevel.Debug), DebugArgs.Int(a, "after", 0))
                    .Select(e => new { e.Seq, time = e.Utc.ToString("HH:mm:ss.fff"), e.Tick, category = e.Category.ToString(), level = e.Level.ToString(), e.Message });
            });
            r.Add("loglevel", DebugRole.Any, "loglevel category level: set a category's level", a =>
            {
                WarLog.SetLevel(DebugArgs.Enum<LogCategory>(a, "category"), DebugArgs.Enum<LogLevel>(a, "level"));
                return "ok";
            });
        }

        private static SettledSnapshot Require(SettledSnapshot s) => s ?? throw new DebugError("no settled match data yet");

        internal static object Units(SettledSnapshot s, JObject a)
        {
            int? owner = DebugArgs.Has(a, "owner") ? DebugArgs.Int(a, "owner") : (int?)null;
            UnitType? type = DebugArgs.Has(a, "type") ? DebugArgs.Enum<UnitType>(a, "type") : (UnitType?)null;
            bool boxed = DebugArgs.Has(a, "box");
            var (min, max) = boxed ? DebugArgs.Box(a, "box") : (int2.zero, int2.zero);
            int limit = DebugArgs.Int(a, "limit", 200);
            var list = new List<object>();
            int count = 0;
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Health[i] <= 0f) continue;
                if (owner.HasValue && s.Owner[i] != owner.Value) continue;
                if (type.HasValue && s.Type[i] != (byte)type.Value) continue;
                if (boxed && (math.any(s.Position[i] < (float2)min) || math.any(s.Position[i] >= (float2)(max + 1)))) continue;
                count++;
                if (list.Count < limit)
                    list.Add(new { id = s.Id[i], owner = s.Owner[i], type = ((UnitType)s.Type[i]).ToString(), pos = new[] { s.Position[i].x, s.Position[i].y }, health = s.Health[i], stance = s.Stance[i], target = s.Target[i] });
            }
            return new { tick = s.Tick, count, truncated = count > list.Count, units = list };
        }

        private static object Unit(SettledSnapshot s, int id)
        {
            for (int i = 0; i < s.Count; i++)
                if (s.Id[i] == id)
                    return new { id, owner = s.Owner[i], type = ((UnitType)s.Type[i]).ToString(), pos = new[] { s.Position[i].x, s.Position[i].y }, health = s.Health[i], maxHealth = s.MaxHealth[i], stance = s.Stance[i], target = s.Target[i], targetKind = s.TargetKind[i], order = s.OrderSlot[i] };
            throw new DebugError($"no live unit {id}");
        }

        private static object ClientUnits(JObject a)
        {
            ClientWorld world = ClientWorld.Instance ?? throw new DebugError("no client world");
            int limit = DebugArgs.Int(a, "limit", 200);
            var ids = new List<int>();
            // Playtest 2026-11-10: the default box (±100,000 tiles) found nothing on a joined client; it is the map's own.
            var map = WorldStateManager.Instance?.Map;
            int2 mapMax = map != null ? new int2(map.Grid.Width - 1, map.Grid.Height - 1) : new int2(4095);
            var (min, max) = DebugArgs.Has(a, "box") ? DebugArgs.Box(a, "box") : (int2.zero, mapMax);
            world.QueryBox((float2)min, (float2)(max + 1), DebugArgs.Int(a, "owner", 0), ids);
            return new { count = ids.Count, truncated = ids.Count > limit, units = ids.Take(limit).Select(id => world.TryGet(id, out ClientUnitView v) ? (object)new { id, owner = v.OwnerId, type = v.Type.ToString(), pos = new[] { v.Position.x, v.Position.y }, health = v.Health01 } : null) };
        }

        private static object Players()
        {
            GameCore core = GameCore.Instance ?? throw new DebugError("no GameCore");
            var list = new List<object>();
            foreach (var p in core.ServerPlayers)
            {
                ClientPlayer cp = p.Key != null ? p.Key.GetComponent<ClientPlayer>() : null;
                int owner = BuildingData.UIntToInt(p.Key.netId);
                list.Add(new { owner, nickname = cp?.nickname, colour = cp?.colourIndex, team = core.TeamOf(owner), state = StateOf(core, p.Value), resources = ResourcesOf(core, p.Value), bot = false });
            }
            foreach (var b in core.AiPlayers) list.Add(new { owner = b.Key, nickname = b.Value.Callsign, colour = (int?)b.Value.Colour, team = core.TeamOf(b.Key), state = StateOf(core, b.Value.Player), resources = ResourcesOf(core, b.Value.Player), bot = true, difficulty = b.Value.Difficulty.ToString() });
            return list;
        }

        // Playtest 2026-11-10: in the lobby every seat read "Playing" with 1,000 resources while the lobby was set to 2,500.
        private static string StateOf(GameCore core, ServerPlayer p) => core.CurrentState == GameState.Lobby ? "Lobby" : p.state.ToString();
        private static float ResourcesOf(GameCore core, ServerPlayer p) =>
            core.CurrentState == GameState.Lobby ? (core.Settings.InfiniteResources ? ServerPlayer.InfiniteBalance : core.Settings.StartingResources) : p.Resources;

        private static object Buildings(JObject a)
        {
            int? owner = DebugArgs.Has(a, "owner") ? DebugArgs.Int(a, "owner") : (int?)null;
            BuildingType? type = DebugArgs.Has(a, "type") ? DebugArgs.Enum<BuildingType>(a, "type") : (BuildingType?)null;
            var list = new List<object>();
            // A host's own client holds only what its team sees, but the server answers for the whole match, as units does.
            if (NetworkServer.active && WorldStateManager.Instance != null && SimContext.Current != null)
            {
                var views = new List<BuildingView>();
                WorldStateManager.Instance.CollectBuildingViews(views);
                foreach (BuildingView v in views)
                {
                    if ((owner.HasValue && v.OwnerId != owner.Value) || (type.HasValue && v.Type != type.Value)) continue;
                    list.Add(new { id = v.Id, owner = v.OwnerId, type = v.Type.ToString(), pos = new[] { (float)v.Anchor.x, v.Anchor.y }, ghost = false });
                }
                return new { count = list.Count, queued = SimContext.Current.Commands.CountOf(SimCommandKind.CreateBuilding), paused = SimContext.Current.Pause.Paused, buildings = list };
            }
            foreach (ClientBuildings.Entry e in ClientBuildings.Current.Entries.Values)
            {
                if ((owner.HasValue && e.Data.ownerId != owner.Value) || (type.HasValue && e.Data.buildingType != type.Value)) continue;
                list.Add(new { id = e.Data.id, owner = e.Data.ownerId, type = e.Data.buildingType.ToString(), pos = new[] { e.Data.position.x, e.Data.position.y }, ghost = e.Ghost });
            }
            return new { count = list.Count, buildings = list };
        }

        internal static string AsciiTiles(MapGrid grid, int2 min, int2 max)
        {
            max = math.min(max, new int2(grid.Width - 1, grid.Height - 1));
            min = math.max(min, int2.zero);
            if (math.any(max - min > 255)) throw new DebugError("box larger than 256 tiles a side");
            var sb = new StringBuilder();
            for (int y = max.y; y >= min.y; y--)
            {
                for (int x = min.x; x <= max.x; x++)
                {
                    TileType t = (TileType)grid.Tiles[grid.Index(new int2(x, y))];
                    sb.Append(t switch { TileType.Ground => '.', TileType.Wall => '#', TileType.Gem => '*', TileType.Border => 'B', TileType.PlayerWall => 'W', TileType.WallBlueprint => 'w', TileType.RichGem => 'r', _ => '?' });
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        internal static string AsciiFog(SimData data, int grid, int2 min, int2 max)
        {
            if (grid < 0) throw new DebugError("that owner has no vision grid");
            var sb = new StringBuilder();
            int cell = data.FogCellSize;
            // Playtest 2026-11-10: a row per fog cell (not per tile) with no labels, so the grid couldn't be lined up with tiles.
            sb.Append($"one character per fog cell of {cell}x{cell} tiles; top row y={max.y}, left column x={min.x}, each row and column steps {cell} tiles\n");
            for (int y = max.y; y >= min.y; y -= cell)
            {
                for (int x = min.x; x <= max.x; x += cell)
                {
                    float2 p = new float2(x + 0.5f, y + 0.5f);
                    int c = data.FogCellOf(p);
                    bool visible = data.Visible[grid * data.FogCells + c] != 0;
                    bool explored = data.Explored[grid * data.FogCells + c] != 0;
                    sb.Append(visible ? '#' : explored ? '+' : ' ');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}

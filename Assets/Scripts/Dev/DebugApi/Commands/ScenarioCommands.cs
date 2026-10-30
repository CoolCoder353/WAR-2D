using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using WAR2D.Sim;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>Scenario set-up for tests and bug hunts: bots, spawns, buildings, resources, orders, pause and step.</summary>
    public static class ScenarioCommands
    {
        public static void Register(DebugRegistry r)
        {
            r.Add("bots", DebugRole.Server, "bots n [resources]: add n Scripted AI seats (placing or playing)", a =>
            {
                GameCore core = GameCore.Instance ?? throw new DebugError("no GameCore");
                int n = math.clamp(DebugArgs.Int(a, "n"), 1, 7);
                float res = DebugArgs.Float(a, "resources", 1000f);
                var added = new System.Collections.Generic.List<int>();
                for (int i = 0; i < n; i++)
                {
                    int id = AiOwnerIds.For(0);
                    for (int k = 0; core.AiPlayers.ContainsKey(id) && k < AiOwnerIds.Count - 1; k++) id = AiOwnerIds.For(k + 1);
                    if (core.DevAddScriptedAi(id, res) == null) throw new DebugError("could not add a Scripted AI seat");
                    added.Add(id);
                }
                return added;
            });
            r.Add("ai", DebugRole.Server, "ai difficulty [style] [team] [colour]: add a lobby AI seat (Easy, Normal, Hard; Random, Rusher, Turtle, Expander, Balanced)", a =>
            {
                GameCore core = GameCore.Instance ?? throw new DebugError("no GameCore");
                if (core.CurrentState != GameState.Lobby) throw new DebugError("not in the lobby");
                AiDifficulty difficulty = DebugArgs.Enum<AiDifficulty>(a, "difficulty");
                AiStyle style = DebugArgs.Has(a, "style") ? DebugArgs.Enum<AiStyle>(a, "style") : AiStyle.Random;
                AiSeat seat = core.AddAi(difficulty, style, DebugArgs.Int(a, "colour", -1), DebugArgs.Int(a, "team", TeamRules.NoTeam));
                if (seat == null) throw new DebugError("refused (Scripted, a full lobby, a taken colour or a team the mode doesn't offer)");
                return new { owner = seat.OwnerId, callsign = seat.Callsign, difficulty = seat.Difficulty.ToString(), style = seat.Style.ToString(), colour = seat.Colour };
            });
            r.Add("aistate", DebugRole.Server, "aistate [owner]: what each AI is doing (its resolved style, plans and counts; dev only)", a =>
            {
                GameCore core = GameCore.Instance ?? throw new DebugError("no GameCore");
                var list = new System.Collections.Generic.List<object>();
                foreach (AiSeat seat in core.AiPlayers.Values)
                {
                    if (DebugArgs.Has(a, "owner") && seat.OwnerId != DebugArgs.Int(a, "owner")) continue;
                    var status = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["owner"] = seat.OwnerId, ["callsign"] = seat.Callsign, ["difficulty"] = seat.Difficulty.ToString(),
                        ["style"] = seat.ResolvedStyle.ToString(), ["state"] = seat.Player.state.ToString(), ["resources"] = seat.Player.Resources,
                        ["income"] = seat.Player.IncomeLastSecond, ["upkeep"] = seat.Player.UpkeepLastSecond, ["unpaidSeconds"] = seat.Player.UnpaidSeconds,
                    };
                    if (seat.Brain is IAiBrainStatus brain)
                        foreach (var entry in brain.Status()) status[entry.Key] = entry.Value;
                    list.Add(status);
                }
                return list;
            });
            r.Add("timescale", DebugRole.Server, "timescale [n]: run the match n times faster (the tick runs up to n times per frame; dev only); no n answers the current scale", a =>
            {
                if (!DevApi.Allowed) throw new DebugError("dev only (-perf, the editor or a dev build)");
                if (!DebugArgs.Has(a, "n")) return UnityEngine.Time.timeScale;
                float n = DebugArgs.Float(a, "n");
                if (!(n >= 0.1f && n <= 32f)) throw new DebugError("n must be in [0.1, 32]");
                UnityEngine.Time.timeScale = n;
                // A frame's scaled time may then cover several ticks: let the catch-up run them (a slow frame never runs more than n).
                UnityEngine.Time.maximumDeltaTime = math.max(1f / 3f, 0.05f * n);
                return n;
            });
            r.Add("spawn", DebugRole.Server, "spawn owner type tile [count]: free units at a tile", a =>
            {
                WorldStateManager w = World();
                int owner = DebugArgs.Int(a, "owner");
                UnitType type = DebugArgs.Enum<UnitType>(a, "type");
                int2 tile = DebugArgs.Int2(a, "tile");
                int count = math.clamp(DebugArgs.Int(a, "count", 1), 1, 10000);
                var ids = new System.Collections.Generic.List<int>();
                int placed = 0;
                for (int ring = 0; placed < count && ring < 64; ring++)
                    for (int dx = -ring; dx <= ring && placed < count; dx++)
                        for (int dy = -ring; dy <= ring && placed < count; dy++)
                        {
                            if (math.max(math.abs(dx), math.abs(dy)) != ring) continue;
                            int2 t = tile + new int2(dx, dy);
                            if (!w.Map.Grid.Contains(t) || !w.Map.IsWalkable(t)) continue;
                            if (w.DevSpawnUnit(owner, (float2)t + 0.5f, id => ids.Add(id), type)) placed++;
                        }
                return SessionCommands.Wait(() => ids.Count >= placed ? ids.ToArray() : null, 10);
            });
            r.Add("build", DebugRole.Server, "build owner type tile [rot] [complete]: a free building", a =>
            {
                bool ok = World().DevPlaceBuilding(DebugArgs.Int(a, "owner"), DebugArgs.Enum<BuildingType>(a, "type"), DebugArgs.Int2(a, "tile"), 0f, DebugArgs.Float(a, "rot", 0f));
                if (!ok) throw new DebugError("placement refused (footprint not free, or off the map)");
                return "queued";
            });
            r.Add("resources", DebugRole.Server, "resources owner amount: set a player's resources", a =>
            {
                ServerPlayer p = GameCore.Instance?.GetServerPlayerById(DebugArgs.Int(a, "owner")) ?? throw new DebugError("no such player");
                float amount = DebugArgs.Float(a, "amount");
                if (amount < 0f) throw new DebugError("amount must be ≥ 0");
                p.DevSetResources(amount);
                return p.Resources;
            });
            r.Add("order", DebugRole.Server, "order owner (ids|box) kind [goal] [queue]: order units as their owner", a =>
            {
                SimContext sim = Sim();
                var cmd = new SimCommand
                {
                    Kind = SimCommandKind.OrderUnits,
                    OwnerId = DebugArgs.Int(a, "owner"),
                    Order = DebugArgs.Enum<OrderKind>(a, "kind"),
                    Queue = DebugArgs.Bool(a, "queue", false),
                };
                if (DebugArgs.Has(a, "goal")) cmd.Tile = DebugArgs.Int2(a, "goal");
                if (a["ids"] is JArray ids) cmd.Ids = ids.Select(t => (int)t).OrderBy(i => i).Distinct().ToArray();
                else { var (min, max) = DebugArgs.Box(a, "box"); cmd.BoxMin = min; cmd.BoxMax = max; }
                sim.Commands.Enqueue(cmd);
                return "queued";
            });
            r.Add("diplomacy", DebugRole.Server, "diplomacy from to (attack|vision) on: set a diplomacy bit", a =>
            {
                string what = DebugArgs.Str(a, "what");
                if (what != "attack" && what != "vision") throw new DebugError("what must be attack or vision");
                Sim().Commands.Enqueue(new SimCommand
                {
                    Kind = what == "attack" ? SimCommandKind.SetAttack : SimCommandKind.SetShareVision,
                    OwnerId = DebugArgs.Int(a, "from"), TargetOwnerId = DebugArgs.Int(a, "to"), Flag = DebugArgs.Bool(a, "on"),
                });
                return "queued";
            });
            r.Add("kill", DebugRole.Server, "kill ids: kill units", a =>
            {
                if (!(a["ids"] is JArray ids)) throw new DebugError("ids must be a list");
                Sim().Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillUnits, Ids = ids.Select(t => (int)t).ToArray() });
                return "queued";
            });
            r.Add("pause", DebugRole.Server, "pause: stop the tick (and every match timer, as the one-human menu pause does)", _ => { DevPause(true); return "paused"; });
            r.Add("resume", DebugRole.Server, "resume: run the tick again", _ => { DevPause(false); return "running"; });
            r.Add("step", DebugRole.Server, "step [ticks]: run ticks while paused, answer when done", a =>
            {
                SimContext sim = Sim();
                if (!sim.Pause.Paused) throw new DebugError("pause first");
                int ticks = math.clamp(DebugArgs.Int(a, "ticks", 1), 1, 2000);
                int target = sim.Clock.Tick + ticks;
                sim.Pause.Step(ticks);
                return SessionCommands.Wait(() => SimContext.Current == null ? "match ended" : (object)(SimContext.Current.Clock.Tick >= target ? SimContext.Current.Clock.Tick : (object)null), 10 + ticks / 20f);
            });
            r.Add("wait", DebugRole.Any, "wait (state S | ticks N | units owner count) [timeout]: answer when true", a =>
            {
                float timeout = DebugArgs.Float(a, "timeout", 60f);
                if (DebugArgs.Has(a, "state"))
                {
                    GameState want = DebugArgs.Enum<GameState>(a, "state");
                    return SessionCommands.Wait(() => GameCore.Instance != null && GameCore.Instance.CurrentState == want ? want.ToString() : null, timeout);
                }
                if (DebugArgs.Has(a, "ticks"))
                {
                    int target = (SimContext.Current?.Clock.Tick ?? 0) + DebugArgs.Int(a, "ticks");
                    return SessionCommands.Wait(() => SimContext.Current != null && SimContext.Current.Clock.Tick >= target ? (object)SimContext.Current.Clock.Tick : null, timeout);
                }
                int owner = DebugArgs.Int(a, "owner"), want2 = DebugArgs.Int(a, "count");
                return SessionCommands.Wait(() =>
                {
                    SettledSnapshot s = SettledSnapshot.Current;
                    if (s == null) return null;
                    int n = 0;
                    for (int i = 0; i < s.Count; i++) if (s.Owner[i] == owner && s.Health[i] > 0f) n++;
                    return n >= want2 ? (object)n : null;
                }, timeout);
            });
        }

        private static WorldStateManager World() => WorldStateManager.Instance ?? throw new DebugError("no match loaded");
        private static SimContext Sim() => SimContext.Current ?? throw new DebugError("no match running");

        /// <summary>The dev pause goes through the match's pause (v0.14 <c>MatchPause</c>), so it stops every match timer too.</summary>
        private static void DevPause(bool on)
        {
            SimContext sim = Sim();
            if (GameCore.Instance != null) GameCore.Instance.SetDevPause(on);
            else sim.Pause.Paused = on;
        }
    }
}

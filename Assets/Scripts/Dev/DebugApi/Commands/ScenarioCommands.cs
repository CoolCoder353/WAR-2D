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
            r.Add("pause", DebugRole.Server, "pause: stop the tick", _ => { Sim().Pause.Paused = true; return "paused"; });
            r.Add("resume", DebugRole.Server, "resume: run the tick again", _ => { Sim().Pause.Paused = false; return "running"; });
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
    }
}

using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Builder (spec §5.3, plan Task 7). Adds its own wants once the opening is done: the style's producers (one new one
    /// at a time), a Radar (when the style or its scouting wants one), and defences at the spots it defends (Hard:
    /// chokepoints on the lanes out of its start region towards known enemies, from the map's plan; others: its front,
    /// beside its base): Gun turrets, Jammers and bombs by style (Jammers and bombs scaled by <c>SpecialUse</c>), an
    /// extra turret against massed light units (Normal, Hard), and a wall line with a gap for its own army (styles that
    /// wall, at chokepoints). Then it serves every manager's building requests, highest priority first: claims the money
    /// (saving up for opening steps), runs the placement search (spread over thinks) and proposes the placement. It sends
    /// idle Builders to its blueprints and wall blueprints, and to repair damaged buildings out of danger.
    /// </summary>
    public sealed class BuilderManager : IAiManager
    {
        private readonly Dictionary<int, PlacementSearch> searches = new Dictionary<int, PlacementSearch>();
        private readonly Dictionary<int, double> failedUntil = new Dictionary<int, double>();
        private readonly Dictionary<int, double> assignedAt = new Dictionary<int, double>();
        private readonly HashSet<int> requestKeys = new HashSet<int>();
        private readonly List<int> stale = new List<int>();
        private readonly List<int> ids = new List<int>(8);
        private readonly List<int2> wallTiles = new List<int2>(64);
        private readonly HashSet<long> walledSpots = new HashSet<long>();
        private static readonly System.Comparison<BuildRequest> HighestFirst = (a, b) => b.Priority.CompareTo(a.Priority);
        private double nextSpots;

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            AiState state = ai.State;
            if (view.State != GameState.Playing || !state.HasHq) return;
            if (ai.Now >= nextSpots) { FindDefenceSpots(ai, view, state); nextSpots = ai.Now + ai.Config.Play.ReplanSeconds; }
            if (state.OpeningDone) WantBuildings(ai, view, state);
            ServeRequests(ai, state);
            AssignBuilders(ai, view, state);
        }

        // ---- what it wants ----

        private static readonly BuildingType[] ProducerOrder = { BuildingType.Barracks, BuildingType.HeavyFactory, BuildingType.SiegeWorkshop };

        private void WantBuildings(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            int2 hq = state.Hq.Anchor;
            // Producers: one at a time (no producer blueprint standing), the type furthest below the style's count.
            bool producerBuilding = false;
            foreach (BuildingType t in ProducerOrder) if (state.BuildingCount[(int)t] > state.CompleteCount[(int)t]) producerBuilding = true;
            if (!producerBuilding)
            {
                BuildingType next = BuildingType.None;
                float lowest = 1f;
                foreach (BuildingType t in ProducerOrder)
                {
                    if (!ai.Style.Producers.TryGetValue(t, out int wanted) || wanted <= 0) continue;
                    float share = state.BuildingCount[(int)t] / (float)wanted;
                    if (share < lowest) { lowest = share; next = t; }
                }
                if (next != BuildingType.None) state.RequestBuilding(next, hq, play.BaseRadius, AiPriority.Normal, AiKeys.Of(AiKeys.Place, (int)next), play.BuildingGap);
            }
            bool hasProducer = state.CompleteCount[(int)BuildingType.Barracks] + state.CompleteCount[(int)BuildingType.HeavyFactory] + state.CompleteCount[(int)BuildingType.SiegeWorkshop] > 0;
            if (!hasProducer) return;

            if ((ai.Style.Radar || ai.Difficulty.Scouting == AiScouting.RegularWithRadar) && state.BuildingCount[(int)BuildingType.Radar] == 0)
                state.RequestBuilding(BuildingType.Radar, Front(state, 0, play.DefenceSpread), play.BaseRadius / 2f, AiPriority.Low, AiKeys.Of(AiKeys.Place, (int)BuildingType.Radar), play.BuildingGap);

            // Defences: per spot, one missing piece at a time (cheap first), once its army has had its start.
            if (state.PlayingSeconds < play.DefenceAfterSeconds) return;
            int turrets = ai.Style.Turrets + (LightEnemies(ai) ? 1 : 0);
            int jammers = (int)math.round(ai.Style.Jammers * ai.Difficulty.SpecialUse);
            int bombs = (int)math.round(ai.Style.Bombs * ai.Difficulty.SpecialUse);
            for (int s = 0; s < state.DefenceSpots.Count; s++)
            {
                float2 spot = state.DefenceSpots[s], dir = state.DefenceDirs[s];
                float spread = play.DefenceSpread;
                float2 front = spot + dir * spread * 0.5f, back = spot - dir * spread * 0.5f;
                if (state.CountNear(BuildingType.GunTurret, spot, spread + 2f) < turrets)
                    state.RequestBuilding(BuildingType.GunTurret, (int2)math.floor(spot), spread, AiPriority.Low + 2f, AiKeys.Of(AiKeys.Place, (int)BuildingType.GunTurret, s));
                else if (state.CountNear(BuildingType.Bomb, front, spread) < bombs)
                    state.RequestBuilding(BuildingType.Bomb, (int2)math.floor(front), spread, AiPriority.Low + 1f, AiKeys.Of(AiKeys.Place, (int)BuildingType.Bomb, s));
                else if (state.CountNear(BuildingType.Jammer, back, spread + 2f) < jammers)
                    state.RequestBuilding(BuildingType.Jammer, (int2)math.floor(back), spread, AiPriority.Low, AiKeys.Of(AiKeys.Place, (int)BuildingType.Jammer, s), play.BuildingGap);
                else if (ai.Style.Walls && ai.Difficulty.DefendsChokepoints) Wall(ai, state, spot, dir, s);
            }
        }

        /// <summary>A point in front of its HQ (towards its first defended spot or gathering point), <paramref name="distance"/> tiles out.</summary>
        private static int2 Front(AiState state, int spot, float distance)
        {
            float2 to = state.DefenceSpots.Count > spot ? state.DefenceSpots[spot] : state.HasGatherPoint ? (float2)state.GatherPoint : state.Home;
            float2 d = to - state.Home;
            float len = math.length(d);
            return (int2)math.floor(len > 0.01f ? state.Home + d / len * math.min(distance, len) : state.Home);
        }

        /// <summary>True when most enemy units it remembers are light (massed Scouts, Tanks...): an extra turret per spot.</summary>
        private static bool LightEnemies(AiContext ai)
        {
            if (ai.Difficulty.Counters == AiCounters.None) return false;
            int light = 0, all = 0;
            foreach (ArmySighting army in ai.Board.Armies.Values)
                for (int t = 0; t < army.ByType.Length; t++)
                {
                    if (army.ByType[t] == 0 || (UnitType)t == UnitType.Builder) continue;
                    all += army.ByType[t];
                    UnitConfig c = ai.State.UnitOf((UnitType)t);
                    if (c != null && c.Armour == TargetClass.LightUnit) light += army.ByType[t];
                }
            return all > 0 && light >= all * ai.Config.Play.LightTurretShare;
        }

        /// <summary>A wall line across the lane at a chokepoint spot, leaving <c>WallGap</c> tiles open in the middle, once per spot.</summary>
        private void Wall(AiContext ai, AiState state, float2 spot, float2 dir, int index)
        {
            long key = ((long)index << 32) | (uint)state.Hq.Id;
            if (walledSpots.Contains(key)) return;
            // Walls are straight lines (horizontal, vertical or diagonal): snap the line across the lane to 45 degrees.
            float angle = math.atan2(dir.y, dir.x) + math.PI / 2f;
            float step = math.PI / 4f;
            angle = math.round(angle / step) * step;
            int2 across = new int2((int)math.round(math.cos(angle)), (int)math.round(math.sin(angle)));
            int half = 0;
            foreach (AiLane lane in ai.View.Lanes) half = math.max(half, lane.Width);
            half = math.max(3, half / 2 + 2);
            int gap = ai.Config.Play.WallGap;
            int2 centre = (int2)math.floor(spot);
            int2 a0 = centre + across * half, a1 = centre + across * ((gap + 1) / 2);
            int2 b0 = centre - across * (gap / 2 + 1), b1 = centre - across * half;
            float cost = ai.Game.Siege.WallCost * (2 * half - gap);
            if (!state.TrySpend(cost, 0f)) return;
            ai.Propose(AiPriority.Low, AiAction.PlaceWall(a0, a1), AiKeys.Of(AiKeys.Wall, index, 0));
            ai.Propose(AiPriority.Low, AiAction.PlaceWall(b0, b1), AiKeys.Of(AiKeys.Wall, index, 1));
            walledSpots.Add(key);
            WallRules.Line(a0, a1, wallTiles);
            WallRules.Line(b0, b1, wallTiles);
        }

        /// <summary>
        /// The spots it defends. Hard (<c>DefendsChokepoints</c>): where each lane out of its start region leaves it,
        /// nearest the known enemies first (else all lanes), at most <c>MaxDefendedSpots</c>. Otherwise one spot in front
        /// of its HQ towards the enemy (or the map's centre).
        /// </summary>
        private static void FindDefenceSpots(AiContext ai, TeamView view, AiState state)
        {
            state.DefenceSpots.Clear();
            state.DefenceDirs.Clear();
            AiPlayConfig play = ai.Config.Play;
            float2 enemy = EnemyDirectionPoint(ai, view, state);
            int start = state.RegionAt(state.Home);
            if (ai.Difficulty.DefendsChokepoints && start >= 0 && view.Lanes.Length > 0)
            {
                AiRegion home = view.Regions[start];
                for (int pass = 0; pass < play.MaxDefendedSpots; pass++)
                {
                    float best = float.MaxValue;
                    float2 bestSpot = default, bestDir = default;
                    foreach (AiLane lane in view.Lanes)
                    {
                        int other = lane.A == start ? lane.B : lane.B == start ? lane.A : -1;
                        if (other < 0 || other >= view.Regions.Length) continue;
                        float2 dir = math.normalizesafe((float2)(view.Regions[other].Centre - home.Centre));
                        float2 spot = (float2)home.Centre + dir * (home.Radius + play.DefenceDistance);
                        if (Contains(state.DefenceSpots, spot)) continue;
                        float score = math.distance(spot, enemy);
                        if (score < best) { best = score; bestSpot = spot; bestDir = dir; }
                    }
                    if (best == float.MaxValue) break;
                    state.DefenceSpots.Add(bestSpot);
                    state.DefenceDirs.Add(bestDir);
                }
            }
            if (state.DefenceSpots.Count > 0) return;
            float2 d = math.normalizesafe(enemy - state.Home, new float2(1f, 0f));
            state.DefenceSpots.Add(state.Home + d * (state.SizeOf(BuildingType.Base).x + play.DefenceSpread));
            state.DefenceDirs.Add(d);
        }

        private static bool Contains(List<float2> spots, float2 spot)
        {
            foreach (float2 s in spots) if (math.distancesq(s, spot) < 4f) return true;
            return false;
        }

        /// <summary>Where it expects the enemy: the nearest known enemy base, else the map's centre.</summary>
        internal static float2 EnemyDirectionPoint(AiContext ai, TeamView view, AiState state)
        {
            float best = float.MaxValue;
            float2 at = new float2(view.Width, view.Height) * 0.5f;
            foreach (EnemyBase b in ai.Board.EnemyBases.Values)
            {
                float d = math.distancesq((float2)b.Anchor, state.Home);
                if (d < best) { best = d; at = b.Anchor; }
            }
            return at;
        }

        // ---- serving requests ----

        private void ServeRequests(AiContext ai, AiState state)
        {
            List<BuildRequest> requests = state.BuildRequests;
            if (requests.Count > 1) requests.Sort(HighestFirst);
            int tries = ai.Config.Play.PlacementTriesPerThink;
            requestKeys.Clear();
            foreach (BuildRequest r in requests)
            {
                requestKeys.Add(r.Key);
                if (failedUntil.TryGetValue(r.Key, out double until) && ai.Now < until) continue;
                float cost = state.CostOf(r.Type), upkeep = state.UpkeepOf(r.Type);
                if (!state.TrySpend(cost, upkeep))
                {
                    if (r.Save && state.MayAddUpkeep(upkeep)) state.Reserve(cost); // saves up: nothing after it is bought
                    continue;
                }
                if (tries <= 0) continue; // paid for, searched next think
                int radius = (int)math.ceil(r.Radius);
                searches.TryGetValue(r.Key, out PlacementSearch search);
                if (!search.Same(r.Type, r.Near, radius, r.Gap)) search = PlacementSearch.Around(r.Type, r.Near, radius, r.Gap);
                SearchResult result = state.Placement.Search(ref search, ref tries, out int2 anchor, out float rotation);
                if (result == SearchResult.Searching) { searches[r.Key] = search; continue; }
                searches.Remove(r.Key);
                if (result == SearchResult.NotFound)
                {
                    state.Release(cost, upkeep);
                    failedUntil[r.Key] = ai.Now + ai.Config.Play.SearchRetrySeconds;
                    Explore(ai, state, r);
                    continue;
                }
                ai.Propose(r.Priority, AiAction.PlaceBuilding(r.Type, anchor, rotation), r.Key);
                state.AddPending(r.Type, anchor);
            }
            // Forget searches nobody asks for any more.
            stale.Clear();
            foreach (int key in searches.Keys) if (!requestKeys.Contains(key)) stale.Add(key);
            foreach (int key in stale) searches.Remove(key);
        }

        /// <summary>
        /// No spot on the ground it has seen: an idle Builder walks to the nearest ground in the request's area its side
        /// hasn't explored (for a Miner, beside the nearest gem it knows of from the map, as every client does).
        /// </summary>
        private void Explore(AiContext ai, AiState state, in BuildRequest r)
        {
            if (state.IdleBuilders.Count == 0) return;
            TeamView view = ai.View;
            int radius = (int)math.ceil(r.Radius);
            int2 goal = default;
            bool found = false;
            for (int ring = 0; ring <= radius && !found; ring++)
                for (int i = 0, n = ring == 0 ? 1 : 8 * ring; i < n && !found; i++)
                {
                    int2 t = r.Near + Placement.RingOffset(ring, i);
                    if (state.Placement.Explored(t)) continue;
                    TileType kind = view.TileAt(t);
                    if (r.Type == BuildingType.Miner ? kind == TileType.Gem : kind == TileType.Ground) { goal = t; found = true; }
                }
            if (!found) return;
            Pick(view, state, (float2)goal + 0.5f, 1);
            if (ids.Count == 0) return;
            goal = state.GroundNear((float2)goal + 0.5f, 3);
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.Move, goal), AiKeys.Of(AiKeys.Expand, r.Key));
        }

        // ---- Builders ----

        private void AssignBuilders(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            if (state.IdleBuilders.Count == 0) return;
            float reach = ai.Game.Siege.WorkReach + 1.5f;
            foreach (int index in state.Blueprints)
            {
                if (state.IdleBuilders.Count == 0) return;
                AiBuilding b = view.Buildings[index];
                if (assignedAt.TryGetValue(b.Id, out double at) && ai.Now - at < play.AssignSeconds) continue;
                float2 centre = state.CentreOf(b);
                int working = 0;
                foreach (int i in state.Builders)
                {
                    AiUnit u = view.OwnUnits[i];
                    if (u.HasOrder && math.distancesq(u.OrderGoal, centre) < reach * reach) working++;
                }
                int need = play.BuildersPerBlueprint - working;
                if (need <= 0) continue;
                Pick(view, state, centre, need);
                if (ids.Count == 0) continue;
                ai.Propose(AiPriority.High, AiAction.OrderUnits(ids, OrderKind.Build, b.Anchor), AiKeys.Of(AiKeys.Build, b.Id));
                assignedAt[b.Id] = ai.Now;
            }
            // Its wall blueprints, one Builder per segment.
            for (int i = wallTiles.Count - 1; i >= 0 && state.IdleBuilders.Count > 0; i--)
            {
                int2 t = wallTiles[i];
                TileType kind = view.TileAt(t);
                if (kind != TileType.WallBlueprint)
                {
                    if (kind == TileType.PlayerWall || kind == TileType.Wall || kind == TileType.Gem) wallTiles.RemoveAt(i);
                    continue;
                }
                int key = AiKeys.Of(AiKeys.Wall, t.x, t.y);
                if (assignedAt.TryGetValue(key, out double at) && ai.Now - at < play.AssignSeconds) continue;
                Pick(view, state, (float2)t + 0.5f, 1);
                if (ids.Count == 0) break;
                ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.Build, t), key);
                assignedAt[key] = ai.Now;
            }
            // Repairs: a damaged, finished building out of danger.
            for (int i = 0; i < view.Buildings.Count && state.IdleBuilders.Count > 0; i++)
            {
                AiBuilding b = view.Buildings[i];
                if (b.OwnerId != ai.Owner || b.Ghost || b.Progress < 100 || b.MaxHealth <= 0 || b.Health >= b.MaxHealth * play.RepairBelow) continue;
                if (ai.Board.Threats.TryGetValue(b.Id, out Threat threat) && ai.Now - threat.LastSeen < play.AssignSeconds) continue;
                int key = AiKeys.Of(AiKeys.Repair, b.Id);
                if (assignedAt.TryGetValue(key, out double at) && ai.Now - at < play.AssignSeconds) continue;
                Pick(view, state, state.CentreOf(b), 1);
                if (ids.Count == 0) break;
                ai.Propose(AiPriority.Normal - 5f, AiAction.OrderUnits(ids, OrderKind.Repair, b.Anchor), key);
                assignedAt[key] = ai.Now;
            }
            if (assignedAt.Count > 512) assignedAt.Clear();
        }

        /// <summary>Fills <see cref="ids"/> with up to <paramref name="count"/> idle Builders nearest a point, taking them off the idle list.</summary>
        private void Pick(TeamView view, AiState state, float2 at, int count)
        {
            ids.Clear();
            for (int n = 0; n < count && state.IdleBuilders.Count > 0; n++)
            {
                int best = -1;
                float bestD = float.MaxValue;
                for (int k = 0; k < state.IdleBuilders.Count; k++)
                {
                    float d = math.distancesq(view.OwnUnits[state.IdleBuilders[k]].Position, at);
                    if (d < bestD) { bestD = d; best = k; }
                }
                ids.Add(view.OwnUnits[state.IdleBuilders[best]].Id);
                state.IdleBuilders.RemoveAt(best);
            }
        }
    }
}

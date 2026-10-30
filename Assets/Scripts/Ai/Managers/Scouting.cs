using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Scouting (spec §5.3, §6, plan Task 7): its difficulty's scouts (Scouts it asks Production for, or after
    /// <c>ScoutAfterSeconds</c> its fastest fighting unit) share a route over the map plan's start sites and expansions
    /// it hasn't explored, nearest next. Easy scouts once; Normal and Hard re-scout every <c>RescoutSeconds</c>: the
    /// enemy bases it knows and every site and expansion out of its sight now. Its Radar is the Builder manager's.
    /// </summary>
    public sealed class ScoutingManager : IAiManager
    {
        private readonly List<int2> route = new List<int2>(32);
        private readonly List<int> ids = new List<int>(1);
        private int next;
        private bool firstRunDone, routeBuilt;
        private double routeDoneAt = double.NegativeInfinity;

        /// <summary>One scout: its unit, the route stop it runs to (-1: none yet), and when and where it was last ordered.</summary>
        private struct ScoutRun
        {
            public int Id, Stop;
            public double OrderedAt, StopSince;
            public int2 Ordered;
        }

        private readonly List<ScoutRun> scouts = new List<ScoutRun>(4);
        private readonly HashSet<int> alive = new HashSet<int>();

        /// <summary>The route it runs now (tests read it).</summary>
        public IReadOnlyList<int2> Route => route;

        /// <summary>Its route's progress (dev reads): the next stop of the stops, and how many scouts run it.</summary>
        public string Stops => $"{next}/{route.Count} x{scouts.Count}{(firstRunDone ? " done" : "")}";

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            AiState state = ai.State;
            state.Scouts.Clear();
            if (view.State != GameState.Playing || !state.HasHq) return;
            AiPlayConfig play = ai.Config.Play;
            bool regular = ai.Difficulty.Scouting != AiScouting.Once;
            if (firstRunDone && !regular) return;

            if (!routeBuilt || (next >= route.Count && !AnyRunning()))
            {
                if (routeBuilt) { routeDoneAt = ai.Now; firstRunDone = true; routeBuilt = false; route.Clear(); ClearStops(); }
                if (firstRunDone && (!regular || ai.Now - routeDoneAt < ai.Difficulty.RescoutSeconds)) return;
                BuildRoute(ai, view, state);
                if (route.Count == 0) { firstRunDone = true; routeDoneAt = ai.Now; return; }
            }

            FindScouts(ai, view, state);
            for (int k = 0; k < scouts.Count; k++)
            {
                ScoutRun run = scouts[k];
                int index = IndexOf(view, run.Id);
                if (index < 0) continue;
                AiUnit u = view.OwnUnits[index];
                state.Scouts.Add(u.Id);
                UnitConfig config = state.UnitOf(u.Type);
                float reach = config != null ? config.Sight * 0.5f : 1f;
                // Its stop seen (or reached): the next stop nobody has taken that isn't in sight now.
                // A stop it can't get to (walled off, or it keeps getting turned back) is given up after StopSeconds.
                if (run.Stop < 0 || Done(view, run.Stop, u.Position, reach) || ai.Now - run.StopSince > play.ScoutStopSeconds)
                {
                    while (next < route.Count && Done(view, next, u.Position, reach)) next++;
                    run.Stop = next < route.Count ? next++ : -1;
                    run.StopSince = ai.Now;
                }
                if (run.Stop >= 0)
                {
                    int2 target = route[run.Stop];
                    bool same = run.Ordered.Equals(target);
                    if (!(same && (u.HasOrder || ai.Now - run.OrderedAt < play.AssignSeconds)))
                    {
                        ids.Clear();
                        ids.Add(u.Id);
                        ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.Move, target), AiKeys.Of(AiKeys.Scout, u.Id));
                        run.Ordered = target;
                        run.OrderedAt = ai.Now;
                    }
                }
                scouts[k] = run;
            }
        }

        private bool Done(TeamView view, int stop, float2 at, float reach) =>
            view.FogAt((float2)route[stop] + 0.5f) == 2 || math.distancesq(at, (float2)route[stop] + 0.5f) < reach * reach;

        private bool AnyRunning()
        {
            foreach (ScoutRun r in scouts) if (r.Stop >= 0) return true;
            return false;
        }

        private void ClearStops()
        {
            for (int k = 0; k < scouts.Count; k++) { ScoutRun r = scouts[k]; r.Stop = -1; scouts[k] = r; }
        }

        private static int IndexOf(TeamView view, int id)
        {
            for (int i = 0; i < view.OwnUnits.Count; i++) if (view.OwnUnits[i].Id == id) return i;
            return -1;
        }

        /// <summary>The first run: start sites and expansions it hasn't explored. Later runs: known enemy bases, and every site out of sight now.</summary>
        private void BuildRoute(AiContext ai, TeamView view, AiState state)
        {
            route.Clear();
            next = 0;
            routeBuilt = true;
            float own = view.HqClearRadius * 2f;
            if (firstRunDone)
                foreach (EnemyBase b in ai.Board.EnemyBases.Values) Add(b.Anchor, view.HqClearRadius);
            foreach (int2 site in view.HqSites)
            {
                if (math.distance(site, state.Home) < own || TeammatePick(view, site)) continue;
                byte fog = view.FogAt((float2)site + 0.5f);
                if (firstRunDone ? fog != 2 : fog == 0) Add(site, view.HqClearRadius);
            }
            sitesInRoute = route.Count;
            foreach (AiRegion r in view.Regions)
            {
                if (r.Kind != AiRegionKind.Expansion) continue;
                byte fog = view.FogAt((float2)r.Centre + 0.5f);
                if (firstRunDone ? fog != 2 : fog == 0) Add(r.Centre, view.HqClearRadius);
            }
            // Nearest next, from home: the start sites first (where enemy bases are), then the expansions.
            float2 from = state.Home;
            int sites = sitesInRoute;
            SortNearestNext(0, sites, ref from);
            SortNearestNext(sites, route.Count, ref from);
        }

        private int sitesInRoute;

        /// <summary>Orders <see cref="route"/>[start, end) nearest next, starting from <paramref name="from"/>.</summary>
        private void SortNearestNext(int start, int end, ref float2 from)
        {
            for (int i = start; i < end; i++)
            {
                int best = i;
                float bestD = float.MaxValue;
                for (int k = i; k < end; k++)
                {
                    float d = math.distancesq((float2)route[k], from);
                    if (d < bestD) { bestD = d; best = k; }
                }
                (route[i], route[best]) = (route[best], route[i]);
                from = route[i];
            }
        }

        /// <summary>Adds a stop unless one is already within a start clearing of it.</summary>
        private void Add(int2 t, float apart)
        {
            foreach (int2 r in route) if (math.distancesq((float2)r, (float2)t) < apart * apart) return;
            route.Add(t);
        }

        private static bool TeammatePick(TeamView view, int2 site)
        {
            foreach (AiStartOption o in view.StartOptions)
                if (o.OwnerId != view.Owner && o.Picked && o.Centre.Equals(site)) return true;
            return false;
        }

        /// <summary>
        /// Keeps its difficulty's scouts (<c>Scouts</c>): the ones it had while they live, then its Scouts, else (after
        /// <c>ScoutAfterSeconds</c>, when it has none) its fastest fighter; asks for a Scout when it has too few.
        /// </summary>
        private void FindScouts(AiContext ai, TeamView view, AiState state)
        {
            // Its difficulty's scouts on the biggest map, fewer on smaller ones (a short route needs one).
            int wanted = math.clamp(ai.Difficulty.Scouts * view.Width / ai.Config.Play.ScoutsFullMapSize, 1, math.max(1, ai.Difficulty.Scouts));
            alive.Clear();
            foreach (AiUnit u in view.OwnUnits) alive.Add(u.Id);
            for (int k = scouts.Count - 1; k >= 0; k--) if (!alive.Contains(scouts[k].Id)) scouts.RemoveAt(k);
            int fastest = -1;
            float speed = 0f;
            for (int i = 0; i < view.OwnUnits.Count && scouts.Count < wanted; i++)
            {
                AiUnit u = view.OwnUnits[i];
                if (Has(u.Id)) continue;
                if (u.Type == UnitType.Scout) { scouts.Add(new ScoutRun { Id = u.Id, Stop = -1 }); continue; }
                if (u.Type == UnitType.Builder || u.Type == UnitType.SuicideBomber) continue;
                UnitConfig c = state.UnitOf(u.Type);
                if (c != null && c.MoveSpeed > speed) { speed = c.MoveSpeed; fastest = i; }
            }
            int scoutUnits = 0;
            foreach (ScoutRun r in scouts) if (IndexOf(view, r.Id) is int i && i >= 0 && view.OwnUnits[i].Type == UnitType.Scout) scoutUnits++;
            if (state.CompleteCount[(int)BuildingType.Barracks] > 0 && scoutUnits < wanted && state.QueuedCount[(int)UnitType.Scout] == 0)
                state.RequestUnit(UnitType.Scout, AiPriority.Normal + 1f);
            if (scouts.Count == 0 && fastest >= 0 && state.PlayingSeconds >= ai.Config.Play.ScoutAfterSeconds)
                scouts.Add(new ScoutRun { Id = view.OwnUnits[fastest].Id, Stop = -1 });
        }

        private bool Has(int id)
        {
            foreach (ScoutRun r in scouts) if (r.Id == id) return true;
            return false;
        }
    }
}

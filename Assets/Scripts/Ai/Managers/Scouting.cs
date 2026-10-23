using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Scouting (spec §5.3, §6, plan Task 7): one scout (a Scout it asks Production for, or after
    /// <c>ScoutAfterSeconds</c> its fastest fighting unit) runs a route over the map plan's start sites and expansions
    /// it hasn't explored, nearest next. Easy scouts once; Normal and Hard re-scout every <c>RescoutSeconds</c>: the
    /// enemy bases it knows and every site and expansion out of its sight now. Its Radar is the Builder manager's.
    /// </summary>
    public sealed class ScoutingManager : IAiManager
    {
        private int scout;
        private readonly List<int2> route = new List<int2>(32);
        private readonly List<int> ids = new List<int>(1);
        private int next;
        private bool firstRunDone, routeBuilt;
        private double routeDoneAt = double.NegativeInfinity, orderedAt = double.NegativeInfinity;
        private int2 ordered;

        /// <summary>The route it runs now (tests read it).</summary>
        public IReadOnlyList<int2> Route => route;

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            AiState state = ai.State;
            state.Scouts.Clear();
            if (view.State != GameState.Playing || !state.HasHq) return;
            AiPlayConfig play = ai.Config.Play;
            bool regular = ai.Difficulty.Scouting != AiScouting.Once;
            if (firstRunDone && !regular) return;

            if (!routeBuilt || next >= route.Count)
            {
                if (routeBuilt && next >= route.Count) { routeDoneAt = ai.Now; firstRunDone = true; routeBuilt = false; route.Clear(); }
                if (firstRunDone && (!regular || ai.Now - routeDoneAt < ai.Difficulty.RescoutSeconds)) return;
                BuildRoute(ai, view, state);
                if (route.Count == 0) { firstRunDone = true; routeDoneAt = ai.Now; return; }
            }

            int index = FindScout(ai, view, state);
            if (index < 0) return;
            AiUnit u = view.OwnUnits[index];
            state.Scouts.Add(u.Id);
            // Seen it (or there): the next one.
            UnitConfig config = state.UnitOf(u.Type);
            float reach = config != null ? config.Sight * 0.5f : 1f;
            while (next < route.Count && (view.FogAt((float2)route[next] + 0.5f) == 2 || math.distancesq(u.Position, (float2)route[next] + 0.5f) < reach * reach)) next++;
            if (next >= route.Count) return;
            int2 target = route[next];
            if (u.HasOrder && ordered.Equals(target)) return;
            if (ai.Now - orderedAt < play.AssignSeconds && ordered.Equals(target)) return;
            ids.Clear();
            ids.Add(u.Id);
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.Move, target), AiKeys.Of(AiKeys.Scout));
            ordered = target;
            orderedAt = ai.Now;
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
            foreach (AiRegion r in view.Regions)
            {
                if (r.Kind != AiRegionKind.Expansion) continue;
                byte fog = view.FogAt((float2)r.Centre + 0.5f);
                if (firstRunDone ? fog != 2 : fog == 0) Add(r.Centre, view.HqClearRadius);
            }
            // Nearest next, from home.
            float2 from = state.Home;
            for (int i = 0; i < route.Count; i++)
            {
                int best = i;
                float bestD = float.MaxValue;
                for (int k = i; k < route.Count; k++)
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

        /// <summary>Its scout's index in its units: the one it had, else a Scout, else (after a while) its fastest fighter; asks for a Scout when it has none.</summary>
        private int FindScout(AiContext ai, TeamView view, AiState state)
        {
            int fastest = -1;
            float speed = 0f;
            for (int i = 0; i < view.OwnUnits.Count; i++)
            {
                AiUnit u = view.OwnUnits[i];
                if (u.Id == scout && scout != 0) return i;
            }
            for (int i = 0; i < view.OwnUnits.Count; i++)
            {
                AiUnit u = view.OwnUnits[i];
                if (u.Type == UnitType.Scout) { scout = u.Id; return i; }
                if (u.Type == UnitType.Builder || u.Type == UnitType.SuicideBomber) continue;
                UnitConfig c = state.UnitOf(u.Type);
                if (c != null && c.MoveSpeed > speed) { speed = c.MoveSpeed; fastest = i; }
            }
            if (state.CompleteCount[(int)BuildingType.Barracks] > 0 && state.UnitCount[(int)UnitType.Scout] + state.QueuedCount[(int)UnitType.Scout] == 0)
                state.RequestUnit(UnitType.Scout, AiPriority.Normal + 1f);
            if (fastest >= 0 && state.PlayingSeconds >= ai.Config.Play.ScoutAfterSeconds)
            {
                scout = view.OwnUnits[fastest].Id;
                return fastest;
            }
            return -1;
        }
    }
}

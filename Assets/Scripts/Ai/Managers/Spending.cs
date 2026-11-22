using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Spending (v0.21 §7): while money piles up (<see cref="AiState.Banked"/>) it climbs a ladder of ways to spend it. The
    /// first rungs live where they always did (production queues and extra producers in Production and Builder, more
    /// expansions in Economy); this manager adds the last two. Forward bases: up to <see cref="AiContext.MaxForwardBases"/>
    /// of the expansions it holds (one it can walk to without a breach; the nearest to the enemy, or for a style that
    /// holds its chokes the farthest) grow its style's <c>ForwardProducers</c> producers (one more while banked) and a
    /// Radar; as bases, their lanes join its threat axes, so they get turrets, Mines and walls. Fortify: banked with no
    /// upkeep room left for another unit, or four times <c>BankHigh</c> in hand, it adds Mines and wall layers at its
    /// defended spots (<see cref="AiState.Fortify"/>), which cost little or no upkeep.
    /// </summary>
    public sealed class SpendingManager : IAiManager
    {
        private static readonly BuildingType[] ProducerOrder = { BuildingType.Barracks, BuildingType.HeavyFactory, BuildingType.SiegeWorkshop };

        /// <summary>Forward bases started so far (dev reads).</summary>
        public int ForwardBasesStarted { get; private set; }

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            AiState state = ai.State;
            state.Fortify = false;
            if (view.State != GameState.Playing || !state.HasHq) return;

            // A forward base it no longer holds (no building of its own there): dropped.
            for (int k = state.ForwardRegions.Count - 1; k >= 0; k--)
            {
                int r = state.ForwardRegions[k];
                if (r < 0 || r >= view.Regions.Length || !OwnsAnythingIn(ai, view, view.Regions[r])) state.ForwardRegions.RemoveAt(k);
            }

            bool maxed = !state.MayAddUpkeep(state.UpkeepOf(UnitType.Tank));
            state.Fortify = state.Banked && (maxed || state.Free >= 4f * ai.Policy.BankHigh);

            if (state.Banked && state.ForwardRegions.Count < ai.MaxForwardBases) ChooseForwardBase(ai, view, state);
            for (int k = 0; k < state.ForwardRegions.Count; k++) GrowForwardBase(ai, view, state, state.ForwardRegions[k]);
        }

        private void ChooseForwardBase(AiContext ai, TeamView view, AiState state)
        {
            float2 enemy = BuilderManager.EnemyDirectionPoint(ai, view, state);
            bool safest = ai.Style.HoldsChokes;
            int best = -1;
            float bestScore = float.MinValue;
            foreach (int r in state.HeldExpansions)
            {
                if (state.ForwardRegions.Contains(r)) continue;
                AiRegion region = view.Regions[r];
                // Held means a building of its own stands there (a Miner only placed, not yet shown, doesn't count).
                if (!OwnsAnythingIn(ai, view, region)) continue;
                if (ai.Reach != null && ai.Reach.Query((int2)math.floor(state.Home), region.Centre, view.Tiles, ai.WallCostFn, null, out _, snap: 8) != Reach.Open) continue;
                float d = math.distance((float2)region.Centre, enemy);
                float score = safest ? d : -d;
                if (score > bestScore) { bestScore = score; best = r; }
            }
            if (best < 0) return;
            state.ForwardRegions.Add(best);
            ForwardBasesStarted++;
        }

        private static void GrowForwardBase(AiContext ai, TeamView view, AiState state, int r)
        {
            AiRegion region = view.Regions[r];
            float radius = region.Radius + 4f;
            int wanted = ai.Style.ForwardProducers + (state.Banked ? 1 : 0);
            int have = 0, building = 0;
            foreach (BuildingType t in ProducerOrder) have += state.CountNear(t, region.Centre, radius + 4f);
            foreach (int i in state.Blueprints)
            {
                AiBuilding b = view.Buildings[i];
                if (System.Array.IndexOf(ProducerOrder, b.Type) >= 0 && math.distancesq((float2)b.Anchor, (float2)region.Centre) <= (radius + 4f) * (radius + 4f)) building++;
            }
            AiPlayConfig play = ai.Config.Play;
            if (have < wanted && building == 0)
            {
                // The style's producer types in turn, by its weights.
                BuildingType next = BuildingType.None;
                int least = int.MaxValue;
                foreach (BuildingType t in ProducerOrder)
                {
                    if (!ai.Style.Producers.TryGetValue(t, out int w) || w <= 0) continue;
                    int n = state.CountNear(t, region.Centre, radius + 4f);
                    if (n < least) { least = n; next = t; }
                }
                if (next != BuildingType.None)
                    state.RequestBuilding(next, region.Centre, radius, AiPriority.Low + 4f, AiKeys.Of(AiKeys.Forward, r, (int)next), play.BuildingGap);
            }
            if ((ai.Style.Radar || ai.Difficulty.Scouting == AiScouting.RegularWithRadar) && state.CountNear(BuildingType.Radar, region.Centre, radius + 4f) == 0)
                state.RequestBuilding(BuildingType.Radar, region.Centre, radius, AiPriority.Low, AiKeys.Of(AiKeys.Forward, r, (int)BuildingType.Radar), play.BuildingGap);
        }

        private static bool OwnsAnythingIn(AiContext ai, TeamView view, in AiRegion region)
        {
            float r2 = (region.Radius + 8f) * (region.Radius + 8f);
            foreach (AiBuilding b in view.Buildings)
                if (b.OwnerId == ai.Owner && !b.Ghost && math.distancesq((float2)b.Anchor, (float2)region.Centre) <= r2) return true;
            return false;
        }
    }
}

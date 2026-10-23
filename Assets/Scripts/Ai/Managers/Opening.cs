using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Opening (spec §5.3, plan Task 7): while HQs are placed, scores its dealt start options (gems and open ground
    /// around each, rich fields in reach, closeness to its teammates' picks) and places its HQ in the best one through
    /// <see cref="PlayerActions.PlaceHq"/>, at once (well before the pick deadline). In play it runs the style's build
    /// order: the first step it doesn't have yet is requested (a building from the Builder manager, a unit from the
    /// Production manager), saving up for it, until every step is had or <c>OpeningSeconds</c> pass.
    /// </summary>
    public sealed class OpeningManager : IAiManager
    {
        private int chosenSite = -2; // -2: not scored yet; -1: no options (dev seats, scene maps)
        private PlacementSearch hqSearch;
        private readonly int[] have = new int[64];

        /// <summary>The option it chose (a start site index), or -1.</summary>
        public int ChosenSite => chosenSite;

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            AiState state = ai.State;
            if (view.State == GameState.PlacingHQ) { PlaceHq(ai, view, state); return; }
            if (view.State != GameState.Playing || state.OpeningDone || !state.HasHq) return;
            if (state.PlayingSeconds > ai.Config.Play.OpeningSeconds) { state.OpeningDone = true; return; }
            RunBuildOrder(ai, state);
        }

        // ---- the HQ ----

        private void PlaceHq(AiContext ai, TeamView view, AiState state)
        {
            if (state.HasHq || state.HqPending) return;
            if (chosenSite == -2) chosenSite = BestOption(ai, view);
            int2 centre;
            int radius;
            if (chosenSite >= 0 && chosenSite < view.HqSites.Length)
            {
                centre = view.HqSites[chosenSite];
                radius = view.HqClearRadius;
            }
            else
            {
                // No options (a dev seat or a scene map): anywhere free, from the map's centre.
                centre = new int2(view.Width / 2, view.Height / 2);
                radius = math.max(view.Width, view.Height) / 2;
            }
            if (!hqSearch.Same(BuildingType.Base, centre, radius, 0)) hqSearch = PlacementSearch.Around(BuildingType.Base, centre, radius, 0);
            int tries = ai.Config.Play.PlacementTriesPerThink;
            SearchResult result = state.Placement.Search(ref hqSearch, ref tries, out int2 anchor, out _);
            if (result == SearchResult.NotFound)
            {
                // Nothing in the chosen option (blocked tries): the next best one, or start over.
                chosenSite = NextOption(view, chosenSite);
                hqSearch = default;
                return;
            }
            if (result != SearchResult.Found) return;
            ai.Propose(AiPriority.Critical, AiAction.PlaceHq(anchor), AiKeys.Of(AiKeys.Hq));
            state.AddPending(BuildingType.Base, anchor);
            hqSearch = default;
        }

        /// <summary>Its own option with the best score, or -1 when it has none.</summary>
        internal int BestOption(AiContext ai, TeamView view)
        {
            int best = -1;
            float bestScore = float.MinValue;
            foreach (AiStartOption o in view.StartOptions)
            {
                if (o.OwnerId != view.Owner) continue;
                float score = Score(ai, view, o.Centre);
                if (score > bestScore) { bestScore = score; best = o.Site; }
            }
            return best;
        }

        private static int NextOption(TeamView view, int after)
        {
            bool passed = after < 0;
            foreach (AiStartOption o in view.StartOptions)
            {
                if (o.OwnerId != view.Owner) continue;
                if (passed) return o.Site;
                if (o.Site == after) passed = true;
            }
            foreach (AiStartOption o in view.StartOptions) if (o.OwnerId == view.Owner) return o.Site;
            return -1;
        }

        /// <summary>
        /// An option's score from the generated map (static, as every client has it): gems and open ground within
        /// <c>OptionRadius</c>, expansions in reach (closer counts more), less distance to its teammates' picks.
        /// </summary>
        internal static float Score(AiContext ai, TeamView view, int2 centre)
        {
            AiPlayConfig play = ai.Config.Play;
            int r = (int)math.ceil(play.OptionRadius);
            float r2 = play.OptionRadius * play.OptionRadius;
            int gems = 0, open = 0;
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y > r2) continue;
                    TileType t = view.TileAt(centre + new int2(x, y));
                    if (t == TileType.Gem) gems++;
                    else if (t == TileType.Ground) open++;
                }
            float score = gems * play.OptionGemWeight + open * play.OptionOpenWeight;
            foreach (AiRegion region in view.Regions)
            {
                if (region.Kind != AiRegionKind.Expansion) continue;
                float d = math.distance(region.Centre, centre);
                if (d < play.OptionExpansionReach) score += play.OptionExpansionWeight * (1f - d / play.OptionExpansionReach);
            }
            float nearest = float.MaxValue;
            foreach (AiStartOption o in view.StartOptions)
                if (o.OwnerId != view.Owner && o.Picked) nearest = math.min(nearest, math.distance(o.Centre, centre));
            if (nearest < float.MaxValue) score -= nearest * play.OptionTeammateWeight;
            return score;
        }

        // ---- the build order ----

        private void RunBuildOrder(AiContext ai, AiState state)
        {
            AiBuildStep[] steps = ai.Style.BuildOrder;
            System.Array.Clear(have, 0, have.Length);
            bool pending = false;
            for (int i = 0; i < steps.Length; i++)
            {
                AiBuildStep step = steps[i];
                int slot = step.Building != BuildingType.None ? (int)step.Building : 32 + (int)step.Unit;
                if ((uint)slot >= (uint)have.Length) continue;
                int needed = ++have[slot];
                if (step.Building != BuildingType.None)
                {
                    if (state.BuildingCount[(int)step.Building] >= needed) continue;
                    // A step its upkeep can't carry yet (Normal, Hard) waits; the steps after it go on meanwhile.
                    if (!state.MayAddUpkeep(state.UpkeepOf(step.Building))) { pending = true; continue; }
                    RequestStep(ai, state, step.Building, i);
                    return;
                }
                int units = state.UnitCount[(int)step.Unit] + state.QueuedCount[(int)step.Unit];
                if (units >= needed) continue;
                if (!state.MayAddUpkeep(state.UpkeepOf(step.Unit))) { pending = true; continue; }
                state.RequestUnit(step.Unit, AiPriority.High);
                state.Reserve(state.CostOf(step.Unit)); // saves up for it
                return;
            }
            if (!pending) state.OpeningDone = true;
        }

        private static void RequestStep(AiContext ai, AiState state, BuildingType type, int step)
        {
            AiPlayConfig play = ai.Config.Play;
            int2 near = state.Hq.Anchor;
            float radius = play.BaseRadius;
            int gap = 0;
            if (type == BuildingType.GunTurret || type == BuildingType.Bomb || type == BuildingType.Jammer || type == BuildingType.Radar)
            {
                // A defence in the opening goes towards its front (the Builder manager's spots, or its gathering point).
                if (state.DefenceSpots.Count > 0) near = (int2)math.floor(state.DefenceSpots[0]);
                else if (state.HasGatherPoint) near = state.GatherPoint;
                radius = play.BaseRadius / 2f;
            }
            if (type != BuildingType.Miner && type != BuildingType.GunTurret && type != BuildingType.Bomb) gap = play.BuildingGap;
            state.RequestBuilding(type, near, radius, AiPriority.High, AiKeys.Of(AiKeys.Place, (int)type, 1000 + step), gap, save: true);
        }
    }
}

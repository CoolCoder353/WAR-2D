using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Economy (spec §5.3, plan Task 7): keeps its difficulty's Builders (plus more per expansion held), Miners facing
    /// gems at its base (the style's count), and takes expansions (the map plan's rich fields) by style and difficulty:
    /// after <c>ExpandAfterSeconds × ExpandTiming</c> of play, the nearest free one, scouted by a Builder first, then
    /// mined. Upkeep discipline lives in <see cref="AiState.MayAddUpkeep"/> (every purchase asks it); when upkeep runs
    /// ahead anyway (Normal, Hard), it cancels blueprints and then demolishes its costliest building to stay paid.
    /// </summary>
    public sealed class EconomyManager : IAiManager
    {
        private readonly List<int> claimed = new List<int>(4);
        private readonly List<int> ids = new List<int>(8);
        private readonly Dictionary<int, double> sentAt = new Dictionary<int, double>();
        private double nextEmergency;

        /// <summary>The expansions (region indices) it has claimed.</summary>
        public IReadOnlyList<int> Claimed => claimed;

        /// <summary>How many of them it holds (a Miner of its own there), as of its last think.</summary>
        public int Held { get; private set; }

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            AiState state = ai.State;
            if (view.State != GameState.Playing || !state.HasHq) return;
            AiPlayConfig play = ai.Config.Play;

            KeepUpkeepPaid(ai, view, state);
            // Upkeep caught up with income (Normal, Hard; Easy just goes unpaid): more Miners, as many at once as its Builders staff (a Miner being built already counts for its income).
            int minersBuilding = state.BuildingCount[(int)BuildingType.Miner] - state.CompleteCount[(int)BuildingType.Miner];
            float incomeSoon = state.Income + minersBuilding * ai.Game.Resources.MiningRate;
            // As many Miner blueprints at once as its Builders staff (one at a time would let a far one hold up the rest).
            int minersAtOnce = math.max(1, state.Builders.Count / math.max(1, play.BuildersPerBlueprint));
            bool capped = ai.Difficulty.KeepsUpkeepPaid && incomeSoon * play.UpkeepIncomeShare - state.Upkeep < play.UpkeepHeadroom && minersBuilding < minersAtOnce;
            // Normal, Hard: Miners on every gem it can reach, as many at once as its Builders staff (they cost no upkeep
            // and pay for themselves in seconds); an area whose search found no spot is full for a while.
            bool more = ai.Difficulty.SaturatesMiners && minersBuilding < minersAtOnce;
            int baseKey = AiKeys.Of(AiKeys.Place, (int)BuildingType.Miner, -1);
            bool baseFull = state.IsSaturated(baseKey);
            int held = UpdateExpansions(ai, view, state, capped, baseFull);
            Held = held;

            int builders = ai.Difficulty.Builders + ai.Difficulty.BuildersPerExpansion * held;
            if (state.UnitCount[(int)UnitType.Builder] + state.QueuedCount[(int)UnitType.Builder] < builders)
                state.RequestUnit(UnitType.Builder, AiPriority.High - 1f);

            // The style's Miners, and more while its income barely covers its upkeep (it builds Miners before anything else that costs upkeep).
            float baseRadius = play.BaseRadius;
            if (state.CountNear(BuildingType.Miner, state.Home, baseRadius + 4f) < ai.Style.Miners || ((capped || more) && !baseFull))
                state.RequestBuilding(BuildingType.Miner, state.Hq.Anchor, baseRadius, AiPriority.High - 2f, baseKey);

            foreach (int index in claimed)
            {
                AiRegion region = view.Regions[index];
                if (!state.Placement.Explored(region.Centre)) { SendBuilder(ai, state, region.Centre, index); continue; }
                int key = AiKeys.Of(AiKeys.Place, (int)BuildingType.Miner, index);
                if (state.CountNear(BuildingType.Miner, region.Centre, region.Radius + 4f) < ai.Style.MinersPerExpansion || ((capped || more) && !state.IsSaturated(key)))
                    state.RequestBuilding(BuildingType.Miner, region.Centre, region.Radius + 4f, ai.Difficulty.SaturatesMiners ? AiPriority.High - 2f : AiPriority.Normal + 5f, key);
            }
        }

        /// <summary>Claims and drops expansions; returns how many it holds (a Miner of its own there).</summary>
        private int UpdateExpansions(AiContext ai, TeamView view, AiState state, bool capped, bool baseFull)
        {
            AiRegion[] regions = view.Regions;
            for (int i = claimed.Count - 1; i >= 0; i--)
            {
                AiRegion r = regions[claimed[i]];
                if (EnemyBuildingNear(ai, r.Centre, r.Radius) && state.CountNear(BuildingType.Miner, r.Centre, r.Radius + 4f) == 0) claimed.RemoveAt(i);
            }
            int held = 0;
            foreach (int index in claimed)
                if (state.CountNear(BuildingType.Miner, regions[index].Centre, regions[index].Radius + 4f) > 0) held++;

            int wanted = math.min(ai.Style.Expansions, ai.Difficulty.MaxExpansions);
            // On time (by difficulty and style), or sooner when its upkeep has caught up with its income and it has money to spare.
            bool onTime = state.PlayingSeconds >= ai.Difficulty.ExpandAfterSeconds * ai.Style.ExpandTiming;
            bool squeezed = capped && state.Free >= ai.Config.Play.CappedExpandResources;
            // Upkeep-capped with money to spare, it takes more than its style's (up to its difficulty's most): money banked is wasted.
            if (squeezed && onTime) wanted = ai.Difficulty.MaxExpansions;
            // Normal, Hard: its base and every expansion it holds full of Miners, it takes the next one (up to its difficulty's most).
            bool full = ai.Difficulty.SaturatesMiners && baseFull && AllHeldFull(state);
            if (full) wanted = ai.Difficulty.MaxExpansions;
            if (claimed.Count >= wanted || !(onTime || squeezed || full)) return held;
            if (state.BuildingCount[(int)BuildingType.Miner] < ai.Style.Miners || state.Free < 2f * state.CostOf(BuildingType.Miner)) return held;
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < regions.Length; i++)
            {
                if (regions[i].Kind != AiRegionKind.Expansion || claimed.Contains(i)) continue;
                if (EnemyBuildingNear(ai, regions[i].Centre, regions[i].Radius)) continue;
                float d = math.distance(regions[i].Centre, state.Home);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0) claimed.Add(best);
            return held;
        }

        private bool AllHeldFull(AiState state)
        {
            foreach (int index in claimed)
                if (!state.IsSaturated(AiKeys.Of(AiKeys.Place, (int)BuildingType.Miner, index))) return false;
            return true;
        }

        private static bool EnemyBuildingNear(AiContext ai, int2 at, float radius)
        {
            float r2 = (radius + 4f) * (radius + 4f);
            foreach (AiBuilding b in ai.Board.EnemyBuildings.Values)
                if (math.distancesq((float2)b.Anchor, at) <= r2) return true;
            return false;
        }

        /// <summary>Sends its nearest idle Builder to see an expansion (it may only build on ground its side has seen).</summary>
        private void SendBuilder(AiContext ai, AiState state, int2 to, int index)
        {
            if (sentAt.TryGetValue(index, out double at) && ai.Now - at < ai.Config.Play.AssignSeconds) return;
            int best = -1;
            float bestD = float.MaxValue;
            foreach (int i in state.IdleBuilders)
            {
                float d = math.distancesq(ai.View.OwnUnits[i].Position, to);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) return;
            ids.Clear();
            ids.Add(ai.View.OwnUnits[best].Id);
            state.IdleBuilders.Remove(best);
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.Move, to), AiKeys.Of(AiKeys.Expand, index));
            sentAt[index] = ai.Now;
        }

        /// <summary>Normal and Hard: upkeep ahead of income with a short balance cancels blueprints, then demolishes the costliest building.</summary>
        private void KeepUpkeepPaid(AiContext ai, TeamView view, AiState state)
        {
            if (!ai.Difficulty.KeepsUpkeepPaid || !state.UpkeepTight || ai.Now < nextEmergency) return;
            nextEmergency = ai.Now + ai.Config.Play.AssignSeconds;
            ids.Clear();
            foreach (int i in state.Blueprints)
            {
                AiBuilding b = view.Buildings[i];
                if (b.Type != BuildingType.Miner && state.UpkeepOf(b.Type) > 0f && ids.Count < SpawnerRules.MaxSpawnersPerCommand) ids.Add(b.Id);
            }
            if (ids.Count == 0)
            {
                int worst = -1;
                float most = 0f;
                for (int i = 0; i < view.Buildings.Count; i++)
                {
                    AiBuilding b = view.Buildings[i];
                    if (b.OwnerId != ai.Owner || b.Ghost || b.Type == BuildingType.Base || b.Type == BuildingType.Miner) continue;
                    float upkeep = state.UpkeepOf(b.Type);
                    if (upkeep > most) { most = upkeep; worst = i; }
                }
                if (worst < 0) return;
                ids.Add(view.Buildings[worst].Id);
            }
            ai.Propose(AiPriority.Critical, AiAction.CancelBuildings(ids), AiKeys.Of(AiKeys.Cancel));
        }
    }
}

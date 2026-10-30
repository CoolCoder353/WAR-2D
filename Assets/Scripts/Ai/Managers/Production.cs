using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Production (spec §5.3, §5.5, plan Task 7): keeps <c>QueueAhead</c> units queued on each finished producer (a whole
    /// <c>QueueBatch</c> while money piles up, AI overhaul §3.3). The HQ makes the Builders the Economy asks for; the
    /// others first make what the managers request (opening steps, a Scout) and, once the opening is done, the army: the
    /// type furthest below its share of the army mix. The mix is the style's (Diggers and Suicide bombers scaled by
    /// <c>SpecialUse</c>), moved by <c>CounterShift</c> towards counters of the enemy units its enemy models remember
    /// (by its <c>CounterWeight</c>) and of the enemy buildings it has seen (by its <c>BuildingCounterWeight</c>), then
    /// scaled by the strategy's <c>MixBias</c>. A producer switches type only with an empty queue (switching clears the
    /// queue). Every unit asks the economy first (cost and upkeep). New units rally at the army's gathering point. A
    /// production slip (AI overhaul §2.3) leaves one producer unrefilled for a think.
    /// </summary>
    public sealed class ProductionManager : IAiManager
    {
        private static readonly int Types = System.Enum.GetValues(typeof(UnitType)).Length + 1;
        private readonly float[] mix = new float[Types];
        private readonly float[] counter = new float[Types];
        private readonly System.Collections.Generic.List<int> ids = new System.Collections.Generic.List<int>(16);

        /// <summary>The army mix it last produced towards (shares by <see cref="UnitType"/> value; tests read it).</summary>
        public float[] Mix => mix;

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            AiState state = ai.State;
            if (view.State != GameState.Playing || !state.HasHq) return;
            if (state.OpeningDone) BuildMix(ai, state);
            int army = 0;
            for (int t = 0; t < Types && t < state.UnitCount.Length; t++)
                if ((UnitType)t != UnitType.Builder) army += state.UnitCount[t] + state.QueuedCount[t];

            bool slipped = false;
            foreach (int index in state.Producers)
            {
                AiBuilding p = view.Buildings[index];
                // Money piling up: army producers keep a whole batch queued (production capacity it already has).
                int ahead = state.Banked && p.Type != BuildingType.Base ? ai.Config.Play.QueueBatch : ai.Config.Play.QueueAhead;
                if (p.Queued >= ahead) continue;
                if (!slipped && ai.Slip(AiSlip.Production)) { slipped = true; continue; }
                UnitType want = Requested(state, p.Type);
                bool forArmy = false;
                if (want == UnitType.None && state.OpeningDone && p.Type != BuildingType.Base) { want = ArmyChoice(state, p.Type, army); forArmy = true; }
                if (want == UnitType.None) continue;
                if (p.Producing != want && p.Queued > 0) continue; // let the queue run out before switching (a switch clears it)
                // An empty army producer takes a batch when it can pay for all of it (fewer clicks); upkeep-planning difficulties (all, since the overhaul) claim the whole
                // batch's cost, so its units spawn from money in hand beyond their upkeep floor. Else one at a time.
                int batch = ai.Config.Play.QueueBatch;
                int step = 1;
                float cost = state.CostOf(want), upkeep = state.UpkeepOf(want);
                if (p.Queued == 0 && forArmy && batch > 1 && state.Free >= cost * batch && state.TrySpend(ai.Difficulty.KeepsUpkeepPaid ? cost * batch : cost, upkeep * batch)) step = batch;
                else if (!state.TrySpend(cost, upkeep)) continue;
                ids.Clear();
                ids.Add(p.Id);
                float priority = want == UnitType.Builder ? AiPriority.High : AiPriority.Normal;
                if (p.Producing != want) ai.Propose(priority, AiAction.SetProduction(ids, want), AiKeys.Of(AiKeys.Produce, p.Id));
                ai.Propose(priority, AiAction.ChangeQueues(ids, step), AiKeys.Of(AiKeys.Queue, p.Id));
                state.QueuedCount[(int)want] += step;
                if (want != UnitType.Builder) army += step;
            }
            Rally(ai, view, state);
        }

        /// <summary>A unit some manager asked for that this producer makes (taken off the requests), or None.</summary>
        private static UnitType Requested(AiState state, BuildingType producer)
        {
            for (int i = 0; i < state.UnitRequests.Count; i++)
            {
                UnitType t = state.UnitRequests[i].Type;
                if (!SpawnerRules.MayProduce(producer, t)) continue;
                state.UnitRequests.RemoveAt(i);
                return t;
            }
            return UnitType.None;
        }

        /// <summary>The type this producer makes that is furthest below its share of the mix, or None (nothing it makes is wanted).</summary>
        private UnitType ArmyChoice(AiState state, BuildingType producer, int army)
        {
            UnitType best = UnitType.None;
            float bestGap = float.MinValue;
            foreach (UnitType t in SpawnerRules.Produces(producer))
            {
                float share = mix[(int)t];
                if (share <= 0f) continue;
                float have = army > 0 ? (state.UnitCount[(int)t] + state.QueuedCount[(int)t]) / (float)army : 0f;
                float gap = share - have;
                if (gap > bestGap) { bestGap = gap; best = t; }
            }
            return best;
        }

        /// <summary>The army mix (shares summing to 1): the style's, moved towards counters of what it knows (by its insight), scaled by the strategy's bias.</summary>
        internal void BuildMix(AiContext ai, AiState state)
        {
            System.Array.Clear(mix, 0, mix.Length);
            float sum = 0f;
            foreach (System.Collections.Generic.KeyValuePair<UnitType, float> w in ai.Style.ArmyMix)
            {
                float weight = w.Value * (IsSpecial(w.Key) ? ai.Difficulty.SpecialUse : 1f);
                mix[(int)w.Key] += weight;
                sum += weight;
            }
            if (sum > 0f) for (int t = 0; t < mix.Length; t++) mix[t] /= sum;

            float unitWeight = ai.Difficulty.CounterWeight, buildingWeight = ai.Difficulty.BuildingCounterWeight;
            if (unitWeight > 0f || buildingWeight > 0f)
            {
                // Counters of the enemy units its models remember (AI overhaul §4.2) ...
                System.Array.Clear(counter, 0, counter.Length);
                float unitTotal = 0f;
                foreach (EnemyModel model in ai.Board.Models.Values)
                {
                    if (!ai.View.IsEnemy(model.Owner)) continue;
                    for (int t = 0; t < model.Peak.Length; t++)
                    {
                        float n = model.Peak[t];
                        if (n <= 0f || !ai.Config.SeenCounters.TryGetValue((UnitType)t, out var against)) continue;
                        foreach (System.Collections.Generic.KeyValuePair<UnitType, float> c in against) { counter[(int)c.Key] += n * c.Value; unitTotal += n * c.Value; }
                    }
                }
                // ... and of what the enemy buildings it has seen will make (a Heavy Factory ghost: anti-Heavy), weighed
                // against the unit counters by BuildingCounterWeight.
                float buildingTotal = 0f;
                System.Array.Clear(scratch, 0, scratch.Length);
                foreach (AiBuilding b in ai.Board.EnemyBuildings.Values)
                {
                    if (!ai.Config.BuildingCounters.TryGetValue(b.Type, out var against)) continue;
                    float boost = b.Type == BuildingType.GunTurret ? state.TurretCounterBoost : 1f;
                    foreach (System.Collections.Generic.KeyValuePair<UnitType, float> c in against) { scratch[(int)c.Key] += c.Value * boost; buildingTotal += c.Value * boost; }
                }
                float buildingScale = buildingTotal > 0f ? (unitTotal > 0f ? unitTotal : 1f) * ai.Config.Play.BuildingCounterWeight / buildingTotal : 0f;
                float unitPart = unitTotal, buildingPart = buildingTotal * buildingScale;
                // Each part counts by its weight (its insight): the shift is CounterShift times the parts' weighted share.
                float weighted = unitPart * unitWeight + buildingPart * buildingWeight;
                if (weighted > 0f)
                {
                    float shift = ai.Config.Play.CounterShift * weighted / (unitPart + buildingPart);
                    for (int t = 0; t < mix.Length; t++)
                    {
                        float c = (counter[t] * unitWeight + scratch[t] * buildingScale * buildingWeight) / weighted;
                        mix[t] = (1f - shift) * mix[t] + shift * c;
                    }
                }
            }

            // The strategy's bias (AI overhaul §4.3), only on types the mix already has (a style's roster is kept).
            float biased = 0f;
            for (int t = 0; t < mix.Length && t < state.MixBias.Length; t++) { mix[t] *= state.MixBias[t]; biased += mix[t]; }
            if (biased > 0f) for (int t = 0; t < mix.Length; t++) mix[t] /= biased;
        }

        private readonly float[] scratch = new float[Types];

        private static bool IsSpecial(UnitType t) => t == UnitType.Digger || t == UnitType.SuicideBomber;

        /// <summary>Sends every producer's new units to the army's gathering point (one action for all that rally elsewhere).</summary>
        private void Rally(AiContext ai, TeamView view, AiState state)
        {
            if (!state.HasGatherPoint) return;
            ids.Clear();
            foreach (int index in state.Producers)
            {
                AiBuilding p = view.Buildings[index];
                if (p.Type == BuildingType.Base) continue;
                if (p.HasRally && math.distancesq((float2)p.Rally, state.GatherPoint) < ai.Config.Play.ArmyRadius) continue;
                if (ids.Count < SpawnerRules.MaxSpawnersPerCommand) ids.Add(p.Id);
            }
            if (ids.Count > 0) ai.Propose(AiPriority.Low, AiAction.SetRally(ids, state.GatherPoint), AiKeys.Of(AiKeys.Rally));
        }
    }
}

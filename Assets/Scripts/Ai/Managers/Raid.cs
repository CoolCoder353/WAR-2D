using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Raids (AI overhaul §3.5, v0.21 §6.3): a style that raids sends up to <c>RaidSize</c> of its fastest units (move speed
    /// at least <c>RaidMinSpeed</c>) gathered at home at the known enemy Miner or expansion building with the best worth over
    /// distance whose known defence they beat by <c>RaidAdvantage</c>, away from its other raids' targets and the army's.
    /// A raid comes home when it is losing, its target is gone or <c>RaidSeconds</c> pass. Up to
    /// <see cref="AiContext.MaxRaids"/> raids at once, one started per <c>RaidSeconds</c> / that many, formed only on
    /// attentive thinks; while the army attacks only at full Insight (Hard times its raids with its attacks, so defenders
    /// are split). Raiders are left out of the army (<see cref="AiState.Raiders"/>).
    /// </summary>
    public sealed class RaidManager : IAiManager
    {
        private sealed class Raid
        {
            public readonly HashSet<int> Units = new HashSet<int>();
            public int TargetId;
            public int2 Target;
            public double Started, OrderedAt = double.NegativeInfinity;
        }

        private readonly List<Raid> raids = new List<Raid>(4);
        private readonly List<int> ids = new List<int>(8);
        private readonly List<(int Id, float Distance)> candidates = new List<(int, float)>(16);
        private double nextRaid;

        /// <summary>True while a raid is out; the first raid's target; raids sent so far.</summary>
        public bool Active => raids.Count > 0;
        public int TargetId => raids.Count > 0 ? raids[0].TargetId : 0;
        public int2 Target => raids.Count > 0 ? raids[0].Target : default;
        public int Raids { get; private set; }

        /// <summary>Raids out now (dev reads).</summary>
        public int Out => raids.Count;

        public void Think(AiContext ai)
        {
            AiState state = ai.State;
            state.Raiders.Clear();
            TeamView view = ai.View;
            if (view.State != GameState.Playing || !state.HasHq) return;
            AiPolicyConfig policy = ai.Policy;
            if (!ai.Style.Raids || policy.RaidSize <= 0) { for (int k = raids.Count - 1; k >= 0; k--) ComeHome(ai, state, k); return; }

            for (int k = raids.Count - 1; k >= 0; k--)
            {
                Raid r = raids[k];
                r.Units.RemoveWhere(id => IndexOf(view, id) < 0);
                if (r.Units.Count == 0) { raids.RemoveAt(k); continue; }
                foreach (int id in r.Units) state.Raiders.Add(id);
            }
            for (int k = raids.Count - 1; k >= 0; k--) Run(ai, view, state, k);

            bool duringAttack = state.Attacking && ai.Difficulty.Insight >= 1f;
            if (raids.Count >= ai.MaxRaids || !ai.Attentive || (state.Attacking && !duringAttack) || !state.OpeningDone || ai.Now < nextRaid) return;
            TryStart(ai, view, state);
        }

        private void Run(AiContext ai, TeamView view, AiState state, int k)
        {
            Raid r = raids[k];
            AiPolicyConfig policy = ai.Policy;
            float2 centre = 0;
            foreach (int id in r.Units) centre += view.OwnUnits[IndexOf(view, id)].Position;
            centre /= r.Units.Count;
            Force ours = AiForces.Own(ai, r.Units);
            Force theirs = AiForces.EnemyNear(ai, centre, ai.Config.Play.ArmyRadius);
            bool losing = !theirs.Empty && AiForces.Advantage(ai, ours, theirs) < 1f;
            bool gone = !ai.Board.EnemyBuildings.ContainsKey(r.TargetId);
            if (losing || gone || ai.Now - r.Started > policy.RaidSeconds) { ComeHome(ai, state, k); return; }
            if (!ai.Attentive || ai.Now - r.OrderedAt < ai.Config.Play.OrderSeconds) return;
            ids.Clear();
            foreach (int id in r.Units) if (!view.OwnUnits[IndexOf(view, id)].HasOrder) ids.Add(id);
            if (ids.Count == 0) return;
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.AttackMove, r.Target), AiKeys.Of(AiKeys.Raid, r.TargetId));
            r.OrderedAt = ai.Now;
        }

        /// <summary>True when <paramref name="at"/> is near another raid's target or the army's.</summary>
        private bool Taken(AiContext ai, AiState state, int2 at)
        {
            float r = ai.Config.Play.ArmyRadius * 2f;
            foreach (Raid raid in raids) if (math.distancesq((float2)raid.Target, (float2)at) < r * r) return true;
            return state.Attacking && math.distancesq((float2)state.AttackTarget, (float2)at) < r * r;
        }

        private void TryStart(AiContext ai, TeamView view, AiState state)
        {
            AiPolicyConfig policy = ai.Policy;
            AiPlayConfig play = ai.Config.Play;
            if (!state.HasGatherPoint) return;
            float2 home = (float2)state.GatherPoint + 0.5f;
            float r2 = play.ArmyRadius * play.ArmyRadius;
            candidates.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (state.Scouts.Contains(u.Id) || state.Raiders.Contains(u.Id) || state.Posted.Contains(u.Id) || state.InAttack.Contains(u.Id) || u.HasOrder) continue;
                UnitConfig c = state.UnitOf(u.Type);
                if (c == null || c.MoveSpeed < policy.RaidMinSpeed) continue;
                float d = math.distancesq(u.Position, home);
                if (d <= r2) candidates.Add((u.Id, d));
            }
            if (candidates.Count < 2) return;
            candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var raid = new Raid();
            for (int k = 0; k < candidates.Count && raid.Units.Count < policy.RaidSize; k++) raid.Units.Add(candidates[k].Id);
            Force ours = AiForces.Own(ai, raid.Units);

            float best = 0f;
            int bestId = 0;
            int2 bestAt = default;
            float noise = ai.Noise();
            foreach (AiBuilding b in ai.Board.EnemyBuildings.Values)
            {
                if (b.Type != BuildingType.Miner && !AtExpansion(view, b.Anchor)) continue;
                if (b.Type == BuildingType.Mine || ai.Board.IsAvoided(b.Anchor, play.ArmyRadius) || Taken(ai, state, b.Anchor)) continue;
                if (ai.Reach != null && ai.Reach.Query((int2)math.floor(home), b.Anchor, view.Tiles, ai.WallCostFn, null, out _, snap: 8) != Reach.Open) continue;
                float2 at = (float2)b.Anchor + 0.5f;
                Force defence = AiForces.KnownTurrets(ai, at, play.ArmyRadius);
                if (ai.Board.Armies.TryGetValue(b.OwnerId, out ArmySighting seen) && math.distancesq(seen.Centre, at) <= 4f * r2)
                    defence.Add(AiForces.EnemyArmy(ai, b.OwnerId));
                if (FightModel.Advantage(ours, defence, policy.RangeWeight) * noise < policy.RaidAdvantage) continue;
                float score = ArmyManager.WorthOf(ai, view, b) / (1f + math.distance(home, at) / policy.TargetDistanceScale);
                if (score > best) { best = score; bestId = b.Id; bestAt = b.Anchor; }
            }
            if (bestId == 0) return;
            raid.TargetId = bestId;
            raid.Target = bestAt;
            raid.Started = ai.Now;
            raids.Add(raid);
            Raids++;
            float cooldown = policy.RaidSeconds / math.max(1, ai.MaxRaids);
            if (ai.Board.EnemyBuildings.TryGetValue(bestId, out AiBuilding target) && ai.Board.Models.TryGetValue(target.OwnerId, out EnemyModel model) && model.Guess == EnemyStyleGuess.Turtle)
                cooldown *= state.RaidCooldownFactor;
            nextRaid = ai.Now + cooldown;
            foreach (int id in raid.Units) state.Raiders.Add(id);
            ids.Clear();
            foreach (int id in raid.Units) ids.Add(id);
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.AttackMove, raid.Target), AiKeys.Of(AiKeys.Raid, raid.TargetId));
            raid.OrderedAt = ai.Now;
        }

        /// <summary>A raid ends: its units go back to the gathering point (and to the army).</summary>
        private void ComeHome(AiContext ai, AiState state, int k)
        {
            Raid r = raids[k];
            raids.RemoveAt(k);
            ids.Clear();
            foreach (int id in r.Units) { state.Raiders.Remove(id); if (IndexOf(ai.View, id) >= 0) ids.Add(id); }
            if (ids.Count > 0 && state.HasGatherPoint) ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.Move, state.GatherPoint), AiKeys.Of(AiKeys.Raid, r.TargetId));
        }

        private static bool AtExpansion(TeamView view, int2 at)
        {
            foreach (AiRegion r in view.Regions)
                if (r.Expandable && math.distancesq((float2)r.Centre, (float2)at) <= (r.Radius + 4f) * (r.Radius + 4f)) return true;
            return false;
        }

        private static int IndexOf(TeamView view, int id)
        {
            for (int i = 0; i < view.OwnUnits.Count; i++) if (view.OwnUnits[i].Id == id) return i;
            return -1;
        }
    }
}

using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Raids (AI overhaul §3.5): a style that raids sends up to <c>RaidSize</c> of its fastest units (move speed at least
    /// <c>RaidMinSpeed</c>) gathered at home at the known enemy Miner or expansion building with the best worth over
    /// distance whose known defence they beat by <c>RaidAdvantage</c>. The raid comes home when it is losing, its target
    /// is gone or <c>RaidSeconds</c> pass. One raid at a time, formed only on attentive thinks while the army isn't
    /// attacking; raiders are left out of the army (<see cref="AiState.Raiders"/>).
    /// </summary>
    public sealed class RaidManager : IAiManager
    {
        private readonly HashSet<int> raiders = new HashSet<int>();
        private readonly List<int> ids = new List<int>(8);
        private readonly List<(int Id, float Distance)> candidates = new List<(int, float)>(16);
        private double started, nextRaid, orderedAt = double.NegativeInfinity;

        /// <summary>True while a raid is out, its target, and raids sent so far.</summary>
        public bool Active { get; private set; }
        public int TargetId { get; private set; }
        public int2 Target { get; private set; }
        public int Raids { get; private set; }

        public void Think(AiContext ai)
        {
            AiState state = ai.State;
            state.Raiders.Clear();
            TeamView view = ai.View;
            if (view.State != GameState.Playing || !state.HasHq) return;
            AiPolicyConfig policy = ai.Policy;
            if (!ai.Style.Raids || policy.RaidSize <= 0) { if (Active) ComeHome(ai, state); return; }

            if (Active)
            {
                raiders.RemoveWhere(id => IndexOf(view, id) < 0);
                if (raiders.Count == 0) { Active = false; return; }
                foreach (int id in raiders) state.Raiders.Add(id);
                Run(ai, view, state);
                return;
            }
            if (!ai.Attentive || state.Attacking || !state.OpeningDone || ai.Now < nextRaid) return;
            TryStart(ai, view, state);
        }

        private void Run(AiContext ai, TeamView view, AiState state)
        {
            AiPolicyConfig policy = ai.Policy;
            float2 centre = 0;
            foreach (int id in raiders) centre += view.OwnUnits[IndexOf(view, id)].Position;
            centre /= raiders.Count;
            Force ours = AiForces.Own(ai, raiders);
            Force theirs = AiForces.EnemyNear(ai, centre, ai.Config.Play.ArmyRadius);
            bool losing = !theirs.Empty && AiForces.Advantage(ai, ours, theirs) < 1f;
            bool gone = !ai.Board.EnemyBuildings.ContainsKey(TargetId);
            if (losing || gone || ai.Now - started > policy.RaidSeconds) { ComeHome(ai, state); return; }
            if (!ai.Attentive || ai.Now - orderedAt < ai.Config.Play.OrderSeconds) return;
            ids.Clear();
            foreach (int id in raiders) if (!view.OwnUnits[IndexOf(view, id)].HasOrder) ids.Add(id);
            if (ids.Count == 0) return;
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.AttackMove, Target), AiKeys.Of(AiKeys.Raid));
            orderedAt = ai.Now;
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
                if (state.Scouts.Contains(u.Id) || u.HasOrder) continue;
                UnitConfig c = state.UnitOf(u.Type);
                if (c == null || c.MoveSpeed < policy.RaidMinSpeed) continue;
                float d = math.distancesq(u.Position, home);
                if (d <= r2) candidates.Add((u.Id, d));
            }
            if (candidates.Count < 2) return;
            candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            raiders.Clear();
            for (int k = 0; k < candidates.Count && raiders.Count < policy.RaidSize; k++) raiders.Add(candidates[k].Id);
            Force ours = AiForces.Own(ai, raiders);

            float best = 0f;
            int bestId = 0;
            int2 bestAt = default;
            float noise = ai.Noise();
            foreach (AiBuilding b in ai.Board.EnemyBuildings.Values)
            {
                if (b.Type != BuildingType.Miner && !AtExpansion(view, b.Anchor)) continue;
                if (b.Type == BuildingType.Bomb || ai.Board.IsAvoided(b.Anchor, play.ArmyRadius)) continue;
                float2 at = (float2)b.Anchor + 0.5f;
                Force defence = AiForces.KnownTurrets(ai, at, play.ArmyRadius);
                if (ai.Board.Armies.TryGetValue(b.OwnerId, out ArmySighting seen) && math.distancesq(seen.Centre, at) <= 4f * r2)
                    defence.Add(AiForces.EnemyArmy(ai, b.OwnerId));
                if (FightModel.Advantage(ours, defence, policy.RangeWeight) * noise < policy.RaidAdvantage) continue;
                float score = ArmyManager.WorthOf(ai, b.Type) / (1f + math.distance(home, at) / policy.TargetDistanceScale);
                if (score > best) { best = score; bestId = b.Id; bestAt = b.Anchor; }
            }
            if (bestId == 0) { raiders.Clear(); return; }
            Active = true;
            TargetId = bestId;
            Target = bestAt;
            started = ai.Now;
            Raids++;
            float cooldown = policy.RaidSeconds;
            if (ai.Board.EnemyBuildings.TryGetValue(bestId, out AiBuilding target) && ai.Board.Models.TryGetValue(target.OwnerId, out EnemyModel model) && model.Guess == EnemyStyleGuess.Turtle)
                cooldown *= state.RaidCooldownFactor;
            nextRaid = ai.Now + cooldown;
            foreach (int id in raiders) state.Raiders.Add(id);
            ids.Clear();
            foreach (int id in raiders) ids.Add(id);
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.AttackMove, Target), AiKeys.Of(AiKeys.Raid));
            orderedAt = ai.Now;
        }

        /// <summary>The raid ends: its units go back to the gathering point (and to the army).</summary>
        private void ComeHome(AiContext ai, AiState state)
        {
            ids.Clear();
            foreach (int id in raiders) if (IndexOf(ai.View, id) >= 0) ids.Add(id);
            if (ids.Count > 0 && state.HasGatherPoint) ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.Move, state.GatherPoint), AiKeys.Of(AiKeys.Raid));
            raiders.Clear();
            Active = false;
            TargetId = 0;
        }

        private static bool AtExpansion(TeamView view, int2 at)
        {
            foreach (AiRegion r in view.Regions)
                if (r.Kind == AiRegionKind.Expansion && math.distancesq((float2)r.Centre, (float2)at) <= (r.Radius + 4f) * (r.Radius + 4f)) return true;
            return false;
        }

        private static int IndexOf(TeamView view, int id)
        {
            for (int i = 0; i < view.OwnUnits.Count; i++) if (view.OwnUnits[i].Id == id) return i;
            return -1;
        }
    }
}

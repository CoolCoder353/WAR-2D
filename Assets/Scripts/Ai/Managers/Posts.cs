using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Posts (v0.21 §6.2): once its army is big enough to attack, groups of <c>PostSize</c> of its fighting units hold
    /// ground away from its base, at most <see cref="AiContext.MaxPosts"/> posts and its style's <c>PostShare</c> of its
    /// fighting units. Sites, chosen every <c>PostSeconds</c>: central areas and expansions it doesn't hold, scored by their
    /// gems plus the enemy Miners and blueprints it knows there over distance, reachable without a breach, and only where
    /// it beats what it knows defends them. A post attack-moves onto the enemy Builders, blueprints and Miners it sees
    /// within <c>PostRadius</c>; it falls back (and avoids the site for a while) from a fight it is losing, and comes home
    /// while its army defends its base. Posted units are left out of the army (<see cref="AiState.Posted"/>).
    /// </summary>
    public sealed class PostManager : IAiManager
    {
        private sealed class Post
        {
            public int Region;
            public int2 Site;
            public readonly HashSet<int> Units = new HashSet<int>();
            public double OrderedAt = double.NegativeInfinity;
        }

        private readonly List<Post> posts = new List<Post>(4);
        private readonly List<int> ids = new List<int>(16);
        private readonly List<(int Id, float Distance)> free = new List<(int, float)>(64);
        private double nextChoice;

        /// <summary>Posts held now, and posts taken so far (dev reads).</summary>
        public int Held => posts.Count;
        public int Taken { get; private set; }

        /// <summary>Every post site it has sent units to, oldest first (dev and test reads; public map data: region centres).</summary>
        public readonly List<int2> History = new List<int2>(8);

        /// <summary>Where its posts stand (dev reads).</summary>
        public string Sites
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                foreach (Post p in posts) { if (sb.Length > 0) sb.Append(' '); sb.Append(p.Site.x).Append(',').Append(p.Site.y).Append('(').Append(p.Units.Count).Append(')'); }
                return sb.ToString();
            }
        }

        public void Think(AiContext ai)
        {
            AiState state = ai.State;
            state.Posted.Clear();
            TeamView view = ai.View;
            if (view.State != GameState.Playing || !state.HasHq) return;

            // Dead units leave; a post with none left is gone.
            for (int k = posts.Count - 1; k >= 0; k--)
            {
                posts[k].Units.RemoveWhere(id => IndexOf(view, id) < 0 || state.Raiders.Contains(id) || state.Scouts.Contains(id));
                if (posts[k].Units.Count == 0) posts.RemoveAt(k);
            }
            // Its base under attack: every post comes home to help.
            if (state.Defending) { while (posts.Count > 0) Release(ai, state, posts.Count - 1, false); return; }
            foreach (Post p in posts) foreach (int id in p.Units) state.Posted.Add(id);

            for (int k = posts.Count - 1; k >= 0; k--) Hold(ai, view, state, k);
            if (ai.Now >= nextChoice)
            {
                nextChoice = ai.Now + ai.Config.Warpath.PostSeconds;
                TryTake(ai, view, state);
            }
        }

        private void Hold(AiContext ai, TeamView view, AiState state, int k)
        {
            Post p = posts[k];
            float2 site = (float2)p.Site + 0.5f;
            float radius = ai.Config.Warpath.PostRadius;
            Force ours = AiForces.Own(ai, p.Units);
            Force theirs = AiForces.EnemyNear(ai, site, radius + ai.Config.Play.ArmyRadius * 0.5f);
            if (!theirs.Empty && AiForces.Advantage(ai, ours, theirs) < 1f)
            {
                ai.Board.Avoid(p.Site, ai.Now + ai.Policy.AvoidSeconds);
                Release(ai, state, k, true);
                return;
            }
            if (ai.Now - p.OrderedAt < ai.Config.Play.OrderSeconds) return;
            // What it denies first: an enemy Builder, then a blueprint, then a Miner it sees by the post.
            int2 goal = p.Site;
            float best = float.MaxValue;
            float r2 = radius * radius;
            foreach (AiUnit u in view.OtherUnits)
            {
                if (!view.IsEnemy(u.OwnerId) || u.Type != UnitType.Builder) continue;
                float d = math.distancesq(u.Position, site);
                if (d <= r2 && d < best) { best = d; goal = (int2)math.floor(u.Position); }
            }
            if (best == float.MaxValue)
                foreach (AiBuilding b in view.Buildings)
                {
                    if (b.Ghost || !view.IsEnemy(b.OwnerId) || (b.Progress >= 100 && b.Type != BuildingType.Miner)) continue;
                    float d = math.distancesq((float2)b.Anchor, site);
                    if (d <= r2 && d < best) { best = d; goal = b.Anchor; }
                }
            ids.Clear();
            bool attack = best < float.MaxValue;
            foreach (int id in p.Units)
            {
                AiUnit u = view.OwnUnits[IndexOf(view, id)];
                if (attack ? true : !u.HasOrder && math.distancesq(u.Position, site) > r2 * 0.25f) ids.Add(id);
            }
            if (ids.Count == 0) return;
            ai.Propose(AiPriority.Normal - 2f, AiAction.OrderUnits(ids, OrderKind.AttackMove, goal), AiKeys.Of(AiKeys.Post, p.Region));
            p.OrderedAt = ai.Now;
        }

        private void TryTake(AiContext ai, TeamView view, AiState state)
        {
            int most = ai.MaxPosts;
            if (most <= 0 || posts.Count >= most || !state.OpeningDone || !state.HasGatherPoint) return;
            // Its fighting units not scouting, raiding or attacking (the army's attack group is busy) or already posted.
            int combat = 0, postedNow = 0;
            foreach (int i in state.Combat) { AiUnit u = view.OwnUnits[i]; if (!state.Scouts.Contains(u.Id) && !state.Raiders.Contains(u.Id)) combat++; }
            foreach (Post p in posts) postedNow += p.Units.Count;
            if (combat < ai.Style.AttackArmySize) return;
            int size = ai.Config.Warpath.PostSize;
            if (postedNow + size > combat * ai.Style.PostShare) return;

            float2 home = (float2)state.GatherPoint + 0.5f;
            free.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (state.Scouts.Contains(u.Id) || state.Raiders.Contains(u.Id) || state.Posted.Contains(u.Id) || state.InAttack.Contains(u.Id) || u.HasOrder) continue;
                free.Add((u.Id, math.distancesq(u.Position, home)));
            }
            if (free.Count < size) return;
            free.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var group = new HashSet<int>();
            for (int k = 0; k < size; k++) group.Add(free[k].Id);
            Force ours = AiForces.Own(ai, group);

            int bestRegion = -1;
            float bestScore = 0f;
            int2 bestSite = default;
            float noise = ai.Noise();
            float rich = ai.Game.Resources.RichGemMultiplier;
            for (int r = 0; r < view.Regions.Length; r++)
            {
                AiRegion region = view.Regions[r];
                if (region.Kind == AiRegionKind.Start || state.BaseRegions.Contains(r) || Taken_(r)) continue;
                // Its own Miners there: an expansion of its own, defended by the Army already.
                if (state.CountNear(BuildingType.Miner, region.Centre, region.Radius + 4f) > 0) continue;
                if (ai.Board.IsAvoided(region.Centre, region.Radius)) continue;
                if (ai.Reach != null && ai.Reach.Query((int2)math.floor(home), region.Centre, view.Tiles, ai.WallCostFn, null, out _, snap: 8) != Reach.Open) continue;
                float worth = region.Worth(rich);
                foreach (AiBuilding b in ai.Board.EnemyBuildings.Values)
                    if (b.Type == BuildingType.Miner && math.distancesq((float2)b.Anchor, (float2)region.Centre) <= (region.Radius + 4f) * (region.Radius + 4f))
                        worth += ai.Policy.MinerIncomeValue / 10f;
                if (worth <= 0f) continue;
                float2 at = (float2)region.Centre + 0.5f;
                Force defence = AiForces.KnownTurrets(ai, at, region.Radius + 4f);
                foreach (ArmySighting seen in ai.Board.Armies.Values)
                    if (view.IsEnemy(seen.OwnerId) && math.distancesq(seen.Centre, at) <= (region.Radius + 16f) * (region.Radius + 16f)) defence.Add(AiForces.EnemyArmy(ai, seen.OwnerId));
                if (!defence.Empty && FightModel.Advantage(ours, defence, ai.Policy.RangeWeight) * noise < ai.Policy.RaidAdvantage) continue;
                float score = worth / (1f + math.distance(home, at) / ai.Policy.TargetDistanceScale);
                if (score > bestScore) { bestScore = score; bestRegion = r; bestSite = state.GroundNear(at, 6); }
            }
            if (bestRegion < 0) return;
            var post = new Post { Region = bestRegion, Site = bestSite };
            foreach (int id in group) { post.Units.Add(id); state.Posted.Add(id); }
            posts.Add(post);
            Taken++;
            if (History.Count < 64) History.Add(bestSite);
            ids.Clear();
            ids.AddRange(group);
            ai.Propose(AiPriority.Normal - 2f, AiAction.OrderUnits(ids, OrderKind.AttackMove, bestSite), AiKeys.Of(AiKeys.Post, bestRegion));
            post.OrderedAt = ai.Now;
        }

        private bool Taken_(int region)
        {
            foreach (Post p in posts) if (p.Region == region) return true;
            return false;
        }

        /// <summary>A post ends: its units go back to the gathering point (and to the army).</summary>
        private void Release(AiContext ai, AiState state, int k, bool sendHome)
        {
            Post p = posts[k];
            posts.RemoveAt(k);
            foreach (int id in p.Units) state.Posted.Remove(id);
            if (!sendHome || !state.HasGatherPoint) return;
            ids.Clear();
            foreach (int id in p.Units) if (IndexOf(ai.View, id) >= 0) ids.Add(id);
            if (ids.Count > 0) ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.Move, state.GatherPoint), AiKeys.Of(AiKeys.Post, p.Region));
        }

        private static int IndexOf(TeamView view, int id)
        {
            for (int i = 0; i < view.OwnUnits.Count; i++) if (view.OwnUnits[i].Id == id) return i;
            return -1;
        }
    }
}

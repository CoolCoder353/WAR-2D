using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.Net.Replication;

namespace WAR2D.Client
{
    /// <summary>
    /// Roam (playtest 2026-10-30: "auto roam randomly scout mode"): the client's autopilot for units told to scout on their own.
    /// Each roaming unit is sent, with an ordinary Move order, to a random floor tile the player has not seen lately (unexplored
    /// first, then explored but out of sight), and to a new one when it arrives or stops making progress. It sends only what the
    /// player could send by hand, so the server needs nothing new. Any other order to a unit ends its roaming.
    /// A unit that can attack roams by Attack-move (2026-11-14), so it stops to fight what comes in range instead of driving
    /// through enemy fire; the rest (Diggers aside, Bombers and anything unarmed) keep the plain Move (<see cref="OrderOf"/>).
    /// A roamer in a fight is left to it: standing to shoot is neither arriving nor being stuck.
    /// </summary>
    public sealed class Roam
    {
        /// <summary>Seconds between looks at the roaming units.</summary>
        public const float CheckSeconds = 0.5f;

        /// <summary>Tiles from its goal at which a unit counts as arrived.</summary>
        public const float ArriveTiles = 2f;

        /// <summary>Seconds a unit may go without getting <see cref="ProgressTiles"/> closer before it is given another goal.</summary>
        public const float StuckSeconds = 6f;

        /// <summary>Tiles closer that count as progress.</summary>
        public const float ProgressTiles = 1f;

        /// <summary>
        /// Seconds a unit that was on its way may stand still before it is given another goal. The server ends a Move well
        /// short of <see cref="ArriveTiles"/>: a group stops 0.5 + 0.4 × √followers tiles from the goal (3.3 tiles for 48
        /// units, over 7 for 300 sharing a goal), and a unit walled off from the goal or moved off rock stops where it can.
        /// Those units used to count as still travelling and stood for the whole <see cref="StuckSeconds"/> at every goal.
        /// </summary>
        public const float IdleSeconds = 1.5f;

        /// <summary>Tiles a unit moves between two checks, at most, to count as standing still (the slowest unit covers a tile).</summary>
        public const float IdleTiles = 0.25f;

        /// <summary>Nearest and farthest a new goal is from the unit, in tiles.</summary>
        public const float MinReach = 12f, MaxReach = 48f;

        /// <summary>Random tiles tried per new goal; the least recently seen wins.</summary>
        public const int Samples = 24;

        /// <summary>
        /// Most Move orders one check sends (playtest 2026-11-02: a large roaming group sent one order per unit at once,
        /// past <c>CmdOrderChunk</c>'s rate limit, so the orders were dropped and the host was disconnected). 4 per check is
        /// 8 a second, well under the limit's 20; the rest wait for the next check, round robin.
        /// </summary>
        public const int MaxOrdersPerCheck = 4;

        /// <summary>
        /// Most distinct goals the roamers follow at once. The server lets a player hold only 32 live orders (one per goal and
        /// size class; 24 for the game's own, such as idle Builders' work), and refuses the rest without a word. Every roaming
        /// group used to draw its own random goal, so 180 roamers in 60 clusters held 28 to 31 of the slots and up to two thirds
        /// of them got no order and stood still; what was left for Builders' orders and auto-work was little (2026-11-11). With this many
        /// goals held, a group that needs one joins the nearest goal already held, which costs the server no slot. Two size
        /// classes at most make this 12 of the 32.
        /// </summary>
        public const int MaxGoals = 6;

        /// <summary>
        /// The order a roaming unit of this kind is sent with: Attack-move when it can attack (its <c>Damage</c> is above 0),
        /// else Move. A Bomber's <c>Damage</c> is 0 (its blast is not a weapon it fires), so it keeps the Move and doesn't
        /// spend itself on the first enemy it passes.
        /// </summary>
        public static OrderKind OrderOf(Config.UnitConfig unit) => unit != null && unit.Damage > 0 ? OrderKind.AttackMove : OrderKind.Move;

        /// <summary>
        /// Seconds after a unit's last shot during which it still counts as fighting: its time between shots
        /// (<paramref name="aimSeconds"/> more for one that aims first) and a second's grace, so a slow gun between two shots
        /// is not taken for a unit standing idle.
        /// </summary>
        public static float FightSeconds(Config.UnitConfig unit, float aimSeconds) => (unit != null ? unit.AttackInterval : 0f) + aimSeconds + 1f;

        /// <summary>A unit's drawn position, or false when the client no longer knows it.</summary>
        public delegate bool PositionOf(int id, out float2 position);

        private sealed class Roamer
        {
            public int2 Goal;
            public bool HasGoal;
            /// <summary>This check: the unit is still on its way to <see cref="Goal"/>, so the server holds that order for it.</summary>
            public bool Holding;
            public float Best, BestAt;
            /// <summary>Where it was at the last check, and whether it was seen then.</summary>
            public float2 LastAt;
            public bool Seen;
            /// <summary>It has moved since it got <see cref="Goal"/> (a unit still waiting for its route has not, and is left to <see cref="StuckSeconds"/>).</summary>
            public bool Moved;
            /// <summary>When it last moved.</summary>
            public float MovedAt;
        }

        private readonly Dictionary<int, Roamer> roamers = new Dictionary<int, Roamer>();

        /// <summary>This check: each goal still being followed, and by how many units (see <see cref="MaxGoals"/>).</summary>
        private readonly Dictionary<int2, int> held = new Dictionary<int2, int>();
        private readonly List<Roamer> letGo = new List<Roamer>();

        /// <summary>
        /// Goals still followed that are finished with: a roamer got there (or was stopped on its way by the server), or a
        /// group gave it up as stuck. No group joins one, so the army never goes back and forth between the goals it holds
        /// (player report 2026-11-13: 500 roamers circled between two points, each goal's arrivals joining the other).
        /// </summary>
        private readonly HashSet<int2> reached = new HashSet<int2>();
        private readonly List<int2> closed = new List<int2>();
        private readonly List<int> gone = new List<int>();
        private int cursor;
        private Random random = new Random(0x5C07u);
        private float nextCheck;

        /// <summary>How many units are roaming.</summary>
        public int Count => roamers.Count;

        /// <summary>The roaming units' ids (drawn with the roaming ring).</summary>
        public IEnumerable<int> Ids => roamers.Keys;

        /// <summary>True when unit <paramref name="id"/> is roaming.</summary>
        public bool IsRoaming(int id) => roamers.ContainsKey(id);

        /// <summary>Starts the given units roaming (each picks its first goal at the next check).</summary>
        public void Start(IEnumerable<int> ids)
        {
            foreach (int id in ids) roamers[id] = new Roamer();
            nextCheck = 0f;
        }

        /// <summary>Ends roaming for the given units (they were given another order).</summary>
        public void Stop(IEnumerable<int> ids)
        {
            foreach (int id in ids) roamers.Remove(id);
        }

        /// <summary>Ends all roaming (a new match).</summary>
        public void Clear()
        {
            roamers.Clear();
            reached.Clear();
        }

        /// <summary>
        /// Gives each roaming unit that arrived or stalled a new goal; forgets units the client no longer knows.
        /// <paramref name="send"/> sends a Move order for the listed ids to a tile.
        /// </summary>
        public void Update(float now, int2 mapMin, int2 mapMax, System.Action<List<int>, int2> send, System.Predicate<int> fighting = null)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return;
            Update(now, mapMin, mapMax, send, (int id, out float2 position) =>
            {
                bool known = world.TryGet(id, out ClientUnitView view);
                position = view.Position;
                return known;
            }, fighting);
        }

        /// <summary>
        /// As <see cref="Update(float, int2, int2, System.Action{List{int}, int2})"/>, with the units' positions from
        /// <paramref name="positionOf"/>. The units that need a goal are grouped by <see cref="GroupCell"/>-tile cell, and
        /// each group (at most <see cref="MaxGroup"/> units) shares one goal and one order (playtest 2026-11-05: one order per
        /// unit, 8 a second, left most of 3,000 roamers waiting minutes for a goal, so they looked stuck). At most
        /// <see cref="MaxOrdersPerCheck"/> orders go out per check, starting at the group after the last one served.
        /// </summary>
        /// <remarks>
        /// <paramref name="fighting"/> says a unit is in a fight (it fired lately): it is not counted as stopped or stuck
        /// while it stands to shoot, so it keeps its goal and walks on when the fight is over.
        /// </remarks>
        public void Update(float now, int2 mapMin, int2 mapMax, System.Action<List<int>, int2> send, PositionOf positionOf, System.Predicate<int> fighting = null)
        {
            if (roamers.Count == 0 || now < nextCheck) return;
            nextCheck = now + CheckSeconds;
            gone.Clear();
            groups.Clear();
            groupKeys.Clear();
            held.Clear();
            // Two passes: units that never had a goal first, so a crowded cell's MaxGroup never starves them.
            for (int pass = 0; pass < 2; pass++)
            foreach (KeyValuePair<int, Roamer> pair in roamers)
            {
                int id = pair.Key;
                Roamer r = pair.Value;
                if (r.HasGoal != (pass == 1)) continue;
                if (!positionOf(id, out float2 position)) { gone.Add(id); continue; } // each unit is seen in one pass only
                float distance = r.HasGoal ? math.distance(position, (float2)r.Goal + 0.5f) : 0f;
                bool inFight = fighting != null && fighting(id);
                bool moved = r.Seen && math.distance(position, r.LastAt) > IdleTiles;
                if (moved) r.Moved = true;
                if (!r.Seen || moved || inFight) r.MovedAt = now;
                if (inFight) r.BestAt = now; // standing to shoot is not being stuck either
                r.LastAt = position;
                r.Seen = true;
                // It set off and now stands: the server ended its order short of the goal (a group's stop radius, a wall, a moved goal).
                bool stopped = r.HasGoal && r.Moved && now - r.MovedAt >= IdleSeconds;
                r.Holding = r.HasGoal && distance > ArriveTiles && !stopped; // a stuck unit still follows its order until it gets another
                if (r.HasGoal && !r.Holding) reached.Add(r.Goal);
                if (r.Holding)
                {
                    held[r.Goal] = held.TryGetValue(r.Goal, out int followers) ? followers + 1 : 1;
                    if (distance < r.Best - ProgressTiles) { r.Best = distance; r.BestAt = now; }
                    if (now - r.BestAt < StuckSeconds) continue;
                }
                int2 cell = (int2)math.floor(position / GroupCell);
                long key = ((long)cell.y << 32) ^ (uint)cell.x;
                if (!groups.TryGetValue(key, out Group g))
                {
                    g = new Group();
                    groups[key] = g;
                    groupKeys.Add(key);
                }
                if (g.Ids.Count >= MaxGroup) continue; // the rest of a crowded cell waits a check
                g.Ids.Add(id);
                g.Sum += position;
            }
            foreach (int id in gone) roamers.Remove(id);
            // A reached goal nobody follows any more is gone from the server too: forget it.
            closed.Clear();
            foreach (int2 goal in reached) if (!held.ContainsKey(goal)) closed.Add(goal);
            foreach (int2 goal in closed) reached.Remove(goal);
            if (groupKeys.Count == 0) return;
            groupKeys.Sort(); // a steady order, so the round robin reaches every cell
            int sent = 0, start = cursor % groupKeys.Count;
            for (int k = 0; k < groupKeys.Count && sent < MaxOrdersPerCheck; k++)
            {
                Group g = groups[groupKeys[(start + k) % groupKeys.Count]];
                float2 centre = g.Sum / g.Ids.Count;
                // The group lets go of its old goals first: they are the orders it is about to leave.
                letGo.Clear();
                foreach (int id in g.Ids)
                {
                    Roamer r = roamers[id];
                    if (!r.Holding) continue;
                    Let(r.Goal, -1);
                    r.Holding = false;
                    reached.Add(r.Goal); // given up as stuck: nobody else is sent there either
                    letGo.Add(r);
                }
                if (!ChooseGoal(centre, mapMin, mapMax, out int2 goal, out bool replaces, out int2 replaced))
                {
                    foreach (Roamer r in letGo) { Let(r.Goal, 1); r.Holding = true; }
                    continue;
                }
                if (replaces)
                {
                    // The new goal takes the place of a reached one: its last followers come along in the same order, so
                    // the goals followed stay within MaxGoals.
                    foreach (KeyValuePair<int, Roamer> pair in roamers)
                        if (pair.Value.Holding && pair.Value.Goal.Equals(replaced)) g.Ids.Add(pair.Key);
                    held.Remove(replaced);
                    reached.Remove(replaced);
                }
                reached.Remove(goal);
                float2 goalCentre = (float2)goal + 0.5f;
                foreach (int id in g.Ids)
                {
                    Roamer r = roamers[id];
                    r.Goal = goal;
                    r.HasGoal = true;
                    r.Holding = true;
                    r.Best = math.distance(r.LastAt, goalCentre) + ProgressTiles; // progress is counted from where it stands
                    r.BestAt = now;
                    r.Moved = false; // standing still counts only once it has set off for this goal
                    r.MovedAt = now;
                }
                Let(goal, g.Ids.Count);
                g.Ids.Sort(); // the id codec wants ascending ids
                send(g.Ids, goal);
                sent++;
            }
            cursor = start + math.max(1, sent);
        }

        /// <summary>Tiles on a side of the cell whose roamers share a goal.</summary>
        public const float GroupCell = 16f;

        /// <summary>Most roamers one order sends to one goal (more would only queue at it).</summary>
        public const int MaxGroup = 48;

        private sealed class Group
        {
            public readonly List<int> Ids = new List<int>();
            public float2 Sum;
        }

        private readonly Dictionary<long, Group> groups = new Dictionary<long, Group>();
        private readonly List<long> groupKeys = new List<long>();

        /// <summary>Adds <paramref name="followers"/> (negative to take away) to the count of units following <paramref name="goal"/>.</summary>
        private void Let(int2 goal, int followers)
        {
            int now = (held.TryGetValue(goal, out int was) ? was : 0) + followers;
            if (now > 0) held[goal] = now;
            else held.Remove(goal);
        }

        /// <summary>
        /// A new goal while fewer than <see cref="MaxGoals"/> are held. With the budget spent, the group joins the nearest goal
        /// nobody has reached yet; when every goal held has been reached, a new one replaces the one with the fewest followers
        /// left (<paramref name="replaces"/>, <paramref name="replaced"/>), who are sent on with the group. So the roamers
        /// never take more of the server's order slots than <see cref="MaxGoals"/>, and never go back to a goal they left.
        /// A reached goal is joined only when no new one can be picked at all.
        /// </summary>
        private bool ChooseGoal(float2 from, int2 mapMin, int2 mapMax, out int2 goal, out bool replaces, out int2 replaced)
        {
            replaces = false;
            replaced = default;
            if (held.Count < MaxGoals && PickGoal(from, mapMin, mapMax, out goal)) return true;
            if (JoinNearestGoal(from, true, out goal)) return true;
            if (held.Count >= MaxGoals && FewestFollowers(out replaced) && PickGoal(from, mapMin, mapMax, out goal))
            {
                replaces = true;
                return true;
            }
            return JoinNearestGoal(from, false, out goal);
        }

        /// <summary>The reached goal with the fewest followers left (not one the group being served has just given up).</summary>
        private bool FewestFollowers(out int2 goal)
        {
            goal = default;
            int fewest = int.MaxValue;
            foreach (KeyValuePair<int2, int> pair in held)
            {
                if (!reached.Contains(pair.Key) || WasLetGo(pair.Key) || pair.Value >= fewest) continue;
                fewest = pair.Value;
                goal = pair.Key;
            }
            return fewest != int.MaxValue;
        }

        /// <summary>
        /// The nearest held goal at least <see cref="MinReach"/> tiles away (a unit sent to one beside it would be there at once),
        /// else the farthest; with <paramref name="openOnly"/>, only goals nobody has reached. False when there is none.
        /// </summary>
        private bool JoinNearestGoal(float2 from, bool openOnly, out int2 goal)
        {
            goal = default;
            bool far = false, near = false;
            float nearest = float.MaxValue, farthest = -1f;
            int2 close = default;
            foreach (int2 g in held.Keys)
            {
                if (WasLetGo(g)) continue; // the goal the group is stuck on is no way out of it
                if (openOnly && reached.Contains(g)) continue;
                float d = math.distance(from, (float2)g + 0.5f);
                if (d >= MinReach) { if (d < nearest) { nearest = d; goal = g; far = true; } }
                else if (d > farthest) { farthest = d; close = g; near = true; }
            }
            if (far) return true;
            goal = close;
            return near;
        }

        /// <summary>True when a unit of the group being served has just given up <paramref name="goal"/>.</summary>
        private bool WasLetGo(int2 goal)
        {
            foreach (Roamer r in letGo) if (r.Goal.Equals(goal)) return true;
            return false;
        }

        /// <summary>A random floor tile <see cref="MinReach"/> to <see cref="MaxReach"/> tiles away, preferring ground not seen now.</summary>
        private bool PickGoal(float2 from, int2 mapMin, int2 mapMax, out int2 goal)
        {
            goal = default;
            int bestScore = -1;
            ClientFog fog = ClientFog.Current;
            ClientTerrain terrain = ClientTerrain.Current;
            for (int i = 0; i < Samples; i++)
            {
                float angle = random.NextFloat(0f, 2f * math.PI), reach = random.NextFloat(MinReach, MaxReach);
                int2 tile = (int2)math.floor(from + new float2(math.cos(angle), math.sin(angle)) * reach);
                if (math.any(tile <= mapMin) || math.any(tile >= mapMax - 1)) continue;
                if (terrain.Width > 0)
                {
                    int index = tile.y * terrain.Width + tile.x;
                    if ((uint)index < (uint)terrain.Tiles.Length && !TileRules.IsFloor(terrain.Tiles[index])) continue;
                }
                int score = 1;
                if (fog.State != null && fog.CellSize > 0)
                {
                    int cx = tile.x / fog.CellSize, cy = tile.y / fog.CellSize;
                    if ((uint)cx < (uint)fog.Width && (uint)cy < (uint)fog.Height)
                    {
                        byte f = fog.State[cy * fog.Width + cx];
                        score = f == (byte)FogState.Unexplored ? 3 : f == (byte)FogState.Explored ? 2 : 1;
                    }
                }
                if (score <= bestScore) continue;
                bestScore = score;
                goal = tile;
            }
            return bestScore > 0;
        }
    }
}

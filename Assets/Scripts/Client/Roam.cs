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

        /// <summary>Nearest and farthest a new goal is from the unit, in tiles.</summary>
        public const float MinReach = 12f, MaxReach = 48f;

        /// <summary>Random tiles tried per new goal; the least recently seen wins.</summary>
        public const int Samples = 24;

        private sealed class Roamer
        {
            public int2 Goal;
            public bool HasGoal;
            public float Best, BestAt;
        }

        private readonly Dictionary<int, Roamer> roamers = new Dictionary<int, Roamer>();
        private readonly List<int> gone = new List<int>();
        private readonly List<int> one = new List<int>(1);
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
        public void Clear() => roamers.Clear();

        /// <summary>
        /// Gives each roaming unit that arrived or stalled a new goal; forgets units the client no longer knows.
        /// <paramref name="send"/> sends a Move order for the listed ids to a tile.
        /// </summary>
        public void Update(float now, int2 mapMin, int2 mapMax, System.Action<List<int>, int2> send)
        {
            if (roamers.Count == 0 || now < nextCheck) return;
            nextCheck = now + CheckSeconds;
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return;
            gone.Clear();
            foreach (KeyValuePair<int, Roamer> pair in roamers)
            {
                if (!world.TryGet(pair.Key, out ClientUnitView view)) { gone.Add(pair.Key); continue; }
                Roamer r = pair.Value;
                float distance = r.HasGoal ? math.distance(view.Position, (float2)r.Goal + 0.5f) : 0f;
                if (r.HasGoal && distance > ArriveTiles)
                {
                    if (distance < r.Best - ProgressTiles) { r.Best = distance; r.BestAt = now; }
                    if (now - r.BestAt < StuckSeconds) continue;
                }
                if (!PickGoal(view.Position, mapMin, mapMax, out int2 goal)) continue;
                r.Goal = goal;
                r.HasGoal = true;
                r.Best = math.distance(view.Position, (float2)goal + 0.5f);
                r.BestAt = now;
                one.Clear();
                one.Add(pair.Key);
                send(one, goal);
            }
            foreach (int id in gone) roamers.Remove(id);
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

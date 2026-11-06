using System;
using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.Net.Replay;
using WAR2D.Net.Replication;

namespace WAR2D.Client.Replay
{
    /// <summary>One thing on the replay viewer's seek bar (v0.18, Figma R1).</summary>
    public struct ReplayMarker
    {
        public float Time, Length;
        public bool Fight;
        public string Text;
        /// <summary>Where the camera goes when it is clicked.</summary>
        public float2 At;
    }

    /// <summary>
    /// The per-player view of a replay (v0.18): each player's recorded fog grid and the last they saw of every building. The
    /// memory is kept for every player as the replay plays forward, so choosing a player mid-way shows their ghosts; seeking back
    /// replays from the start, so it stays exact. Pure, apart from reading the replay's buildings.
    /// </summary>
    public sealed class ReplayView
    {
        private sealed class Player
        {
            public byte[] State;
            public readonly Dictionary<int, ClientBuildings.Entry> Memory = new Dictionary<int, ClientBuildings.Entry>();
        }

        private readonly Dictionary<int, Player> players = new Dictionary<int, Player>();
        private readonly List<int> gone = new List<int>();
        private readonly int fogW, fogH, cellSize;

        /// <summary>Which player's eyes are shown, or 0 for everything.</summary>
        public int Viewer { get; set; }

        public ReplayView(int fogW, int fogH, int cellSize)
        {
            this.fogW = fogW;
            this.fogH = fogH;
            this.cellSize = Math.Max(1, cellSize);
        }

        /// <summary>The player's grid, made on first use.</summary>
        public byte[] GridOf(int owner)
        {
            if (!players.TryGetValue(owner, out Player player)) players[owner] = player = new Player { State = new byte[fogW * fogH] };
            return player.State;
        }

        /// <summary>Applies a recorded fog payload, then refreshes every player's memory of the buildings.</summary>
        public void Fog(ArraySegment<byte> payload, ClientBuildings all)
        {
            // A file can name any owner: only as many grids as a match can have players, so it can't grow memory without bound.
            TeamFogCodec.Apply(payload, fogW * fogH, owner => players.ContainsKey(owner) || players.Count < LobbyRules.MaxPlayers ? GridOf(owner) : null);
            Remember(all);
        }

        private int CellOf(float2 p)
        {
            int2 c = math.clamp((int2)math.floor(p / cellSize), 0, new int2(fogW - 1, fogH - 1));
            return c.y * fogW + c.x;
        }

        /// <summary>True when the player's team sees the point now.</summary>
        public bool Sees(int owner, float2 position) =>
            players.TryGetValue(owner, out Player player) && player.State[CellOf(position)] == (byte)FogState.Visible;

        private static float2 PositionOf(in ClientBuildings.Entry e) => new float2(e.Data.position.x, e.Data.position.y);

        /// <summary>Each player remembers what they see now, and forgets a building whose spot they see empty.</summary>
        private void Remember(ClientBuildings all)
        {
            foreach (KeyValuePair<int, Player> pair in players)
            {
                Player player = pair.Value;
                foreach (ClientBuildings.Entry e in all.Entries.Values)
                {
                    if (e.Data.buildingType == BuildingType.Mine && e.Data.ownerId != pair.Key) continue;
                    if (e.Data.ownerId == pair.Key || player.State[CellOf(PositionOf(e))] == (byte)FogState.Visible) player.Memory[e.Data.id] = e;
                }
                gone.Clear();
                foreach (KeyValuePair<int, ClientBuildings.Entry> seen in player.Memory)
                    if (!all.Entries.ContainsKey(seen.Key) && player.State[CellOf(PositionOf(seen.Value))] == (byte)FogState.Visible) gone.Add(seen.Key);
                foreach (int id in gone) player.Memory.Remove(id);
            }
        }

        /// <summary>
        /// Fills <paramref name="shown"/> with what the viewer knows: their own buildings as they are, others in sight as they
        /// are, others as last seen (ghosts), and never another player's bombs.
        /// </summary>
        public void BuildingsFor(int owner, ClientBuildings all, ClientBuildings shown)
        {
            shown.Entries.Clear();
            shown.Burning.Clear();
            if (!players.TryGetValue(owner, out Player player)) return;
            foreach (KeyValuePair<int, ClientBuildings.Entry> seen in player.Memory)
            {
                ClientBuildings.Entry e = seen.Value;
                bool own = e.Data.ownerId == owner;
                bool inSight = player.State[CellOf(PositionOf(e))] == (byte)FogState.Visible;
                if ((own || inSight) && all.Entries.TryGetValue(seen.Key, out ClientBuildings.Entry live))
                {
                    live.Ghost = false;
                    shown.Entries[seen.Key] = live;
                    if (live.Burning) shown.Burning.Add(seen.Key); // v0.20: fires show where the player sees them
                    continue;
                }
                if (own) continue; // gone
                e.Ghost = true;
                shown.Entries[seen.Key] = e;
            }
        }

        /// <summary>True when a unit is drawn in the player's view: their own, or standing where their team sees now.</summary>
        public bool ShowsUnit(int viewer, int unitOwner, float2 position) => viewer == 0 || unitOwner == viewer || Sees(viewer, position);

        /// <summary>The seek-bar marks from a replay's summary.</summary>
        public static List<ReplayMarker> Markers(in SummaryData data, Func<int, string> nameOf)
        {
            var markers = new List<ReplayMarker>();
            foreach (SummaryFight f in data.Fights)
                markers.Add(new ReplayMarker { Time = f.Start, Length = f.End - f.Start, Fight = true, At = new float2(f.CentreX, f.CentreY) });
            foreach (SummaryElimination e in data.Eliminations)
            {
                float2 at = data.MapSize / 2f;
                for (int i = 0; i < data.HqOwners.Length; i++) if (data.HqOwners[i] == e.Owner) at = new float2(data.HqX[i], data.HqY[i]);
                markers.Add(new ReplayMarker { Time = e.Time, At = at });
            }
            List<WAR2D.UI.TimelineEntry> words = WAR2D.UI.TimelineText.Entries(data, nameOf, 0);
            markers.Sort((a, b) => a.Time.CompareTo(b.Time));
            for (int i = 0; i < markers.Count && i < words.Count; i++)
            {
                ReplayMarker m = markers[i];
                m.Text = $"{WAR2D.UI.SummaryModel.Time(m.Time)} · {words[i].Text}";
                markers[i] = m;
            }
            return markers;
        }

        /// <summary>Where a marker's click seeks to: a little before it, never before the start.</summary>
        public static float SeekTime(in ReplayMarker marker, float leadSeconds) => math.max(0f, marker.Time - leadSeconds);
    }
}

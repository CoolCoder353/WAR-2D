using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.AiView
{
    /// <summary>
    /// A growable list of plain values the team view reuses between thinks (no per-think garbage once it has grown).
    /// The AI reads it; only <see cref="TeamViewBuilder"/> fills it.
    /// </summary>
    public sealed class ViewList<T> : IReadOnlyList<T> where T : struct
    {
        private T[] items;

        public ViewList(int capacity = 16) => items = new T[System.Math.Max(1, capacity)];

        /// <summary>How many values it holds.</summary>
        public int Count { get; private set; }

        public T this[int index] => (uint)index < (uint)Count ? items[index] : throw new System.ArgumentOutOfRangeException(nameof(index));

        internal T[] Items => items;

        internal void Clear() => Count = 0;

        internal void Add(in T value)
        {
            if (Count == items.Length) System.Array.Resize(ref items, items.Length * 2);
            items[Count++] = value;
        }

        /// <summary>Makes room for <paramref name="count"/> values and sets the count (the caller fills <see cref="Items"/>).</summary>
        internal void SetCount(int count)
        {
            if (items.Length < count) System.Array.Resize(ref items, math.ceilpow2(math.max(count, 1)));
            Count = count;
        }

        /// <summary>True when it holds <paramref name="value"/>.</summary>
        public bool Contains(T value)
        {
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < Count; i++) if (comparer.Equals(items[i], value)) return true;
            return false;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>A non-allocating enumerator (foreach over a <see cref="ViewList{T}"/> uses it).</summary>
        public struct Enumerator : IEnumerator<T>
        {
            private readonly ViewList<T> list;
            private int index;

            internal Enumerator(ViewList<T> list)
            {
                this.list = list;
                index = -1;
            }

            public T Current => list.items[index];
            object IEnumerator.Current => Current;
            public bool MoveNext() => ++index < list.Count;
            public void Reset() => index = -1;
            public void Dispose() { }
        }
    }

    /// <summary>A unit as the AI's side may know it. The order fields are filled for its own units only (what its owner's client knows).</summary>
    public struct AiUnit
    {
        public int Id, OwnerId;
        public UnitType Type;
        public float2 Position;
        public float Health, MaxHealth;
        /// <summary>Own units only: the stance (<c>Stances</c>: idle, move, attack-move, hold).</summary>
        public byte Stance;
        /// <summary>Own units only: the work it is doing (<c>WorkKinds</c>: none, build, repair, dig).</summary>
        public byte Work;
        /// <summary>Own units only: true while it follows a live order, towards <see cref="OrderGoal"/>.</summary>
        public bool HasOrder;
        public float2 OrderGoal;
        /// <summary>Own units only: its upkeep went unpaid at the last charge.</summary>
        public bool Unpaid;
    }

    /// <summary>A building as the AI's side knows it: its own, one seen now, or a last-seen ghost.</summary>
    public struct AiBuilding
    {
        public int Id, OwnerId;
        public BuildingType Type;
        public int2 Anchor;
        /// <summary>Quarter turns (0..3).</summary>
        public byte Rotation;
        public int Health, MaxHealth;
        /// <summary>Construction progress in percent, 100 when complete.</summary>
        public byte Progress;
        /// <summary>Out of sight: what it holds is how the building was last seen (it may be gone).</summary>
        public bool Ghost;
        /// <summary>Own buildings only: its upkeep went unpaid at the last charge.</summary>
        public bool Unpaid;
    }

    /// <summary>An alert the AI's owner was sent (as <c>ClientPlayer.TargetAlerts</c> gives a client), with when it arrived.</summary>
    public struct AiAlert
    {
        public AlertKind Kind;
        public int2 Tile;
        public int OtherOwnerId;
        public float Amount;
        /// <summary>The AI clock (seconds) it arrived at.</summary>
        public double Time;
    }

    /// <summary>An attack its owner's client would be told about: the attacker is known, the target named only when it may be known (else 0).</summary>
    public struct AiAttack
    {
        /// <summary>True for a building's attack (a Gun turret, v0.13).</summary>
        public bool ByBuilding;
        public int AttackerId;
        /// <summary>The target's id, or 0 when unknown to the AI's side (or a wall shot).</summary>
        public int TargetId;
        public double Time;
    }

    /// <summary>What went off.</summary>
    public enum AiBlastKind : byte
    {
        /// <summary>A bomb.</summary>
        Bomb,
        /// <summary>A Suicide bomber's blast.</summary>
        Bomber,
        /// <summary>A unit's or building's death explosion.</summary>
        Death,
    }

    /// <summary>A blast or death explosion on a fog cell the AI's side saw at the time (as a client is shown them).</summary>
    public struct AiBlast
    {
        public AiBlastKind Kind;
        public float2 Position;
        public double Time;
    }

    /// <summary>One player as the lobby and HUD show everyone (public information).</summary>
    public struct AiPlayer
    {
        public int OwnerId;
        public string Name;
        public int Team;
        public int Colour;
        public bool IsAi;
        /// <summary>Out of the match (the HUD shows it).</summary>
        public bool Eliminated;
    }

    /// <summary>A dealt start option: the AI's own, or a teammate's (as a human teammate is told), with whether it is that player's pick.</summary>
    public struct AiStartOption
    {
        public int OwnerId;
        /// <summary>The start site's index (into <see cref="TeamView.HqSites"/>).</summary>
        public int Site;
        public int2 Centre;
        public bool Picked;
    }

    /// <summary>A player wall tile's owner and health percent, as the AI's side last saw it.</summary>
    public struct AiWall
    {
        public int OwnerId;
        public byte Health;
    }

    /// <summary>
    /// What one AI player may know this think (v0.14, spec §4): built by <see cref="TeamViewBuilder"/> from the settled
    /// world with the same rules replication uses for a client of its owner, into buffers reused between thinks. The AI
    /// never reads the simulation, the map store or another player's records: only this.
    /// </summary>
    public sealed class TeamView
    {
        /// <summary>The AI's owner id and its starting team.</summary>
        public int Owner, Team;

        // ---- the match (public) ----
        public GameState State;
        public MatchMode Mode;
        /// <summary>True when the match lets players change attack and vision (lobby setting).</summary>
        public bool DiplomacyOn;
        /// <summary>The AI clock (seconds of unpaused PlacingHQ and Playing) at this view.</summary>
        public double Time;
        /// <summary>The settled simulation tick the view was built on.</summary>
        public int Tick;
        /// <summary>Every player, with teams (public lobby information).</summary>
        public readonly ViewList<AiPlayer> Players = new ViewList<AiPlayer>(8);
        /// <summary>The map's start sites (the client regenerates them from the seed too).</summary>
        public int2[] HqSites = System.Array.Empty<int2>();

        // ---- its own economy (what ClientPlayer.TargetUpdateResources sends) ----
        public float Resources, Income, Upkeep;

        // ---- start options (what ClientPlayer.TargetStartOptions and TargetTeammatePick send) ----
        public readonly ViewList<AiStartOption> StartOptions = new ViewList<AiStartOption>(8);

        // ---- units and buildings ----
        /// <summary>Its own living units.</summary>
        public readonly ViewList<AiUnit> OwnUnits = new ViewList<AiUnit>(256);
        /// <summary>Every other player's units on fog cells its grid sees now (shared vision included).</summary>
        public readonly ViewList<AiUnit> OtherUnits = new ViewList<AiUnit>(256);
        /// <summary>Its own buildings, others' seen now, and last-seen ghosts.</summary>
        public readonly ViewList<AiBuilding> Buildings = new ViewList<AiBuilding>(64);

        // ---- fog and terrain ----
        public int FogW, FogH, FogCellSize;
        /// <summary>Its grid's fog per cell (<c>FogState</c>: 0 unexplored, 1 explored, 2 visible), row-major.</summary>
        public byte[] Fog = System.Array.Empty<byte>();
        public int Width, Height;
        /// <summary>The map as it knows it: the generated map plus every change on a cell its grid saw (tile kinds, row-major).</summary>
        public byte[] Tiles = System.Array.Empty<byte>();
        /// <summary>Player walls it knows of, by tile index.</summary>
        public readonly Dictionary<int, AiWall> Walls = new Dictionary<int, AiWall>();
        /// <summary>Tile indices whose known kind changed since the last think.</summary>
        public readonly ViewList<int> TerrainChanged = new ViewList<int>(32);

        // ---- since the last think ----
        public readonly ViewList<AiAlert> Alerts = new ViewList<AiAlert>(16);
        public readonly ViewList<AiAttack> Attacks = new ViewList<AiAttack>(64);
        public readonly ViewList<AiBlast> Blasts = new ViewList<AiBlast>(16);
        /// <summary>Events dropped since the last think because a buffer was full (a huge fight between thinks).</summary>
        public int DroppedEvents;

        // ---- diplomacy: its own row only (what ClientPlayer.TargetDiplomacy sends) ----
        /// <summary>Owners it attacks.</summary>
        public readonly ViewList<int> Attacking = new ViewList<int>(8);
        /// <summary>Owners it shares its vision with.</summary>
        public readonly ViewList<int> SharingWith = new ViewList<int>(8);
        /// <summary>Owners who share their vision with it.</summary>
        public readonly ViewList<int> SharedWithMe = new ViewList<int>(8);

        /// <summary>True when it attacks <paramref name="owner"/> ("enemy" is an owner on its attack mask, never just another team).</summary>
        public bool IsEnemy(int owner) => Attacking.Contains(owner);

        /// <summary>A player's starting team, or int.MinValue when unknown.</summary>
        public int TeamOf(int owner)
        {
            foreach (AiPlayer p in Players) if (p.OwnerId == owner) return p.Team;
            return int.MinValue;
        }

        /// <summary>The fog state (0 unexplored, 1 explored, 2 visible) at a world position.</summary>
        public byte FogAt(float2 p)
        {
            if (FogW <= 0 || FogH <= 0 || FogCellSize <= 0) return 0;
            int2 c = math.clamp((int2)math.floor(p / FogCellSize), 0, new int2(FogW - 1, FogH - 1));
            return Fog[c.y * FogW + c.x];
        }

        /// <summary>The known tile kind at a tile, or the border kind off the map.</summary>
        public TileType TileAt(int2 t) =>
            t.x >= 0 && t.y >= 0 && t.x < Width && t.y < Height ? (TileType)Tiles[t.y * Width + t.x] : TileType.Border;
    }
}

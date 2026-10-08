using System;
using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>Set operations on a <see cref="ulong"/> bitset stored in a <see cref="NativeArray{T}"/>.</summary>
    public static class BitSet
    {
        /// <summary>Words a bitset of <paramref name="bits"/> bits needs.</summary>
        public static int Words(int bits) => (bits + 63) / 64;

        /// <summary>Sets a bit.</summary>
        public static void Set(NativeArray<ulong> words, int index) => words[index >> 6] |= 1UL << (index & 63);

        /// <summary>Clears a bit.</summary>
        public static void Clear(NativeArray<ulong> words, int index) => words[index >> 6] &= ~(1UL << (index & 63));

        /// <summary>Reads a bit; an out-of-range index reads false.</summary>
        public static bool Get(NativeArray<ulong> words, int index) =>
            (uint)index < (uint)(words.Length * 64) && (words[index >> 6] & (1UL << (index & 63))) != 0;

        /// <summary>Zeroes the whole set.</summary>
        public static void ClearAll(NativeArray<ulong> words)
        {
            for (int i = 0; i < words.Length; i++) words[i] = 0;
        }

        /// <summary>Counts the set bits, so a bench can report the size of a set without unpacking it.</summary>
        public static int Count(NativeArray<ulong> words)
        {
            int count = 0;
            for (int i = 0; i < words.Length; i++) count += math.countbits(words[i]);
            return count;
        }
    }

    /// <summary>
    /// What each client is allowed to know, and what it already knows. This is the encoder's only
    /// window on the world: the v0.5 plan's interest management, prototyped so the "the encoder
    /// cannot see anything else" guarantee is structural rather than a convention.
    ///
    /// <para><b>Allowed.</b> All units of the client's team (the plan's mapping: FFA is one team per
    /// player, 4v4 puts players in teams of four), plus enemy units standing on a tile the client's
    /// team's <see cref="FogSystem.Visible"/> grid marks. Nothing else is ever put in the set, so a
    /// unit outside vision has no path into the encoder at all.</para>
    ///
    /// <para><b>Known.</b> Per client, the allowed set as it was after the last
    /// <see cref="Build"/>. The difference between the two is exactly the tick's
    /// <see cref="Entered"/> and <see cref="Left"/> lists - a unit that spawned, died, walked into
    /// vision or walked out of it. Ids are monotonic, so a bit never needs clearing for a reused
    /// unit.</para>
    /// </summary>
    public sealed class InterestSets : IDisposable
    {
        private readonly int idCapacity, words;
        private readonly NativeArray<ulong>[] allowed;
        private readonly NativeArray<ulong>[] known;
        private readonly NativeArray<ulong>[] teamAllowed;
        private readonly NativeList<int>[] entered;
        private readonly NativeList<int>[] left;
        private readonly int[] teamOf;

        private NativeArray<float2> positions;
        private NativeArray<byte> player;
        private NativeArray<float> health;
        private NativeArray<int> ids;
        private int capacity;

        private NativeArray<byte>[] visible;
        private FogSystem fog;
        private int width, height, teams;
        private bool disposed;

        /// <summary>Allocates one allowed and one known bitset per client, plus per-team scratch.</summary>
        public InterestSets(int clients, int teams, int idCapacity, Allocator allocator)
        {
            if (clients < 1) throw new ArgumentOutOfRangeException(nameof(clients));
            if (idCapacity < 1) throw new ArgumentOutOfRangeException(nameof(idCapacity));

            Clients = clients;
            this.teams = math.max(1, teams);
            this.idCapacity = idCapacity;
            words = BitSet.Words(idCapacity);

            allowed = new NativeArray<ulong>[clients];
            known = new NativeArray<ulong>[clients];
            teamAllowed = new NativeArray<ulong>[this.teams];
            entered = new NativeList<int>[clients];
            left = new NativeList<int>[clients];
            teamOf = new int[clients];
            for (int team = 0; team < this.teams; team++) teamAllowed[team] = new NativeArray<ulong>(words, allocator);
            for (int client = 0; client < clients; client++)
            {
                allowed[client] = new NativeArray<ulong>(words, allocator);
                known[client] = new NativeArray<ulong>(words, allocator);
                entered[client] = new NativeList<int>(256, allocator);
                left[client] = new NativeList<int>(64, allocator);
                teamOf[client] = FogTeams.Of((byte)client, this.teams);
            }
        }

        /// <summary>Clients this set is built for; one per player.</summary>
        public int Clients { get; }

        /// <summary>Teams in the run: 8 (FFA) or 2 (4v4).</summary>
        public int Teams => teams;

        /// <summary>The fog team a client reads.</summary>
        public int TeamOfClient(int client) => teamOf[client];

        /// <summary>Binds the unit SoA the sets are built from; the arrays stay the caller's.</summary>
        public void SetUnits(NativeArray<float2> positions, NativeArray<byte> player, NativeArray<float> health,
            NativeArray<int> ids, int capacity)
        {
            this.positions = positions;
            this.player = player;
            this.health = health;
            this.ids = ids;
            this.capacity = capacity;
        }

        /// <summary>Binds the fog whose per-team visibility decides enemy access.</summary>
        public void SetFog(FogSystem fog)
        {
            this.fog = fog;
            teams = fog.Teams;
            if (visible == null || visible.Length < teams) visible = new NativeArray<byte>[teams];
        }

        /// <summary>The tile grid the visibility masks are indexed by.</summary>
        public void SetTileGrid(int width, int height)
        {
            this.width = width;
            this.height = height;
        }

        /// <summary>
        /// Rebuilds every set from the SoA and the fog. One pass over the live units fills each team's
        /// allowed set (own units always, enemies only through the team's visibility), then each
        /// client's set is the copy of its team's and the difference against what it knew becomes the
        /// tick's enter and leave lists.
        /// </summary>
        public void Build()
        {
            ThrowIfDisposed();
            if (fog == null) throw new InvalidOperationException("InterestSets has no fog: call SetFog first");
            // The fog trades its grids for a team on every update, so the references are re-read here
            // rather than held from SetFog.
            for (int team = 0; team < teams; team++) visible[team] = fog.Visible(team);
            for (int team = 0; team < teams; team++) BitSet.ClearAll(teamAllowed[team]);

            for (int i = 0; i < capacity; i++)
            {
                if (health[i] <= 0f) continue;
                int id = ids[i];
                if ((uint)id >= (uint)idCapacity) continue;
                byte owner = player[i];
                if (owner >= Clients) continue;

                int ownTeam = FogTeams.Of(owner, teams);
                BitSet.Set(teamAllowed[ownTeam], id);

                // An enemy is allowed on a tile the team sees. Units do not block sight, so this is
                // the whole rule.
                int2 tile = (int2)math.floor(positions[i]);
                if ((uint)tile.x >= (uint)width || (uint)tile.y >= (uint)height) continue;
                int cell = tile.y * width + tile.x;
                for (int team = 0; team < teams; team++)
                {
                    if (team == ownTeam) continue;
                    if (visible[team][cell] != 0) BitSet.Set(teamAllowed[team], id);
                }
            }

            for (int client = 0; client < Clients; client++)
            {
                NativeArray<ulong> clientAllowed = allowed[client], clientKnown = known[client];
                NativeArray<ulong> team = teamAllowed[teamOf[client]];
                NativeList<int> enters = entered[client], leaves = left[client];
                enters.Clear();
                leaves.Clear();

                for (int w = 0; w < words; w++)
                {
                    ulong value = team[w];
                    clientAllowed[w] = value;
                    ulong difference = value ^ clientKnown[w];
                    clientKnown[w] = value;
                    while (difference != 0)
                    {
                        int bit = math.tzcnt(difference);
                        int id = w * 64 + bit;
                        if (id < idCapacity)
                        {
                            if ((value & (1UL << bit)) != 0) enters.Add(id);
                            else leaves.Add(id);
                        }
                        difference &= difference - 1;
                    }
                }
            }
        }

        /// <summary>The ids a client may be told about, as a bitset.</summary>
        public NativeArray<ulong> Allowed(int client) => allowed[client];

        /// <summary>True when the client is allowed to know the unit.</summary>
        public bool IsAllowed(int client, int id) => BitSet.Get(allowed[client], id);

        /// <summary>Units that entered the client's allowed set in the last <see cref="Build"/>, ascending.</summary>
        public NativeList<int> Entered(int client) => entered[client];

        /// <summary>Units that left it (died or walked out of vision), ascending.</summary>
        public NativeList<int> Left(int client) => left[client];

        /// <summary>Frees every bitset and list this set allocated. Safe to call twice.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int team = 0; team < teamAllowed.Length; team++) teamAllowed[team].Dispose();
            for (int client = 0; client < Clients; client++)
            {
                allowed[client].Dispose();
                known[client].Dispose();
                entered[client].Dispose();
                left[client].Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(InterestSets));
        }
    }
}

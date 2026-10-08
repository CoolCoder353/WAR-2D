using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Net.Replication
{
    /// <summary>Fixed-size bitsets over <c>ulong</c> words.</summary>
    public static class BitSet
    {
        public static int Words(int bits) => (bits + 63) / 64;
        public static void Set(NativeArray<ulong> words, int index) => words[index >> 6] |= 1UL << (index & 63);
        public static bool Get(NativeArray<ulong> words, int index) =>
            (uint)index < (uint)(words.Length * 64) && (words[index >> 6] & (1UL << (index & 63))) != 0;
    }

    /// <summary>The unit SoA the replication layer reads (the settled <c>SimData</c>, or a test rig).</summary>
    public struct ReplicationInput
    {
        public int Count;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> MaxHealth;
        [ReadOnly] public NativeArray<int> OwnerId;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> IndexOfId;
        [ReadOnly] public NativeArray<float> SpeedByType;
        /// <summary>Team per unit slot.</summary>
        [ReadOnly] public NativeArray<int> Team;
        /// <summary>Every team's fog grid (see <c>SimData.Visible</c>): grid g covers [g * FogCells, (g + 1) * FogCells).</summary>
        [ReadOnly] public NativeArray<byte> Visible;
        public int FogCellSize, FogW, FogH, FogCells;

        /// <summary>True when fog grid <paramref name="grid"/> sees <paramref name="p"/>.</summary>
        public bool Sees(int grid, float2 p)
        {
            if (grid < 0) return false;
            int2 c = math.clamp((int2)math.floor(p / FogCellSize), 0, new int2(FogW - 1, FogH - 1));
            return Visible[grid * FogCells + c.y * FogW + c.x] != 0;
        }

        /// <summary>The slot of the unit at an id index, or -1.</summary>
        public int SlotOfIndex(int index)
        {
            if ((uint)index >= (uint)IndexOfId.Length) return -1;
            int slot = IndexOfId[index];
            return (uint)slot < (uint)Count && NetIdAllocator.IndexOf(IdOf[slot]) == index && Health[slot] > 0f ? slot : -1;
        }
    }

    /// <summary>
    /// What each client may know about units: its own team's units always, and any other unit standing
    /// on a fog cell its team sees now. Nothing else, whatever the client asks for. <see cref="Build"/> also diffs against what the client
    /// knew, by id index: an index whose id changed (the unit died and the index was reused) is a Leave
    /// of the old id followed by an Enter of the new one.
    /// </summary>
    public sealed class InterestSets : IDisposable
    {
        private readonly int words, idCapacity;
        private readonly NativeArray<ulong>[] allowed;
        private readonly NativeArray<ulong>[] knownBits;
        private readonly NativeArray<int>[] knownId;
        private readonly NativeList<int>[] entered;
        private readonly NativeList<int>[] left;
        private readonly int[] ownerOf, teamOf, gridOf;
        /// <summary>Most units a client may newly learn about per build (snapshot pacing); 0 is unlimited.</summary>
        public int MaxEntersPerBuild;
        private bool disposed;

        public InterestSets(int clients, int idCapacity, Allocator allocator)
        {
            Clients = clients;
            this.idCapacity = idCapacity;
            words = BitSet.Words(idCapacity);
            allowed = new NativeArray<ulong>[clients];
            knownBits = new NativeArray<ulong>[clients];
            knownId = new NativeArray<int>[clients];
            entered = new NativeList<int>[clients];
            left = new NativeList<int>[clients];
            ownerOf = new int[clients];
            teamOf = new int[clients];
            gridOf = new int[clients];
            for (int c = 0; c < clients; c++)
            {
                allowed[c] = new NativeArray<ulong>(words, allocator);
                knownBits[c] = new NativeArray<ulong>(words, allocator);
                knownId[c] = new NativeArray<int>(idCapacity, allocator);
                entered[c] = new NativeList<int>(256, allocator);
                left[c] = new NativeList<int>(64, allocator);
                ownerOf[c] = int.MinValue;
            }
        }

        public int Clients { get; }

        /// <summary>
        /// Sets a client's owner id, team and fog grid (-1: sees only its team's units). An owner of
        /// int.MinValue disables the client.
        /// </summary>
        public void SetClient(int client, int ownerId, int team, int grid)
        {
            ownerOf[client] = ownerId;
            teamOf[client] = team;
            gridOf[client] = grid;
        }

        /// <summary>Forgets everything a client knew (it left, or a new client takes its place).</summary>
        public void ResetClient(int client)
        {
            ownerOf[client] = int.MinValue;
            Clear(allowed[client]);
            Clear(knownBits[client]);
            NativeArray<int> ids = knownId[client];
            for (int i = 0; i < ids.Length; i++) ids[i] = 0;
            entered[client].Clear();
            left[client].Clear();
        }

        private static void Clear(NativeArray<ulong> bits) { for (int i = 0; i < bits.Length; i++) bits[i] = 0; }

        /// <summary>Schedules one Burst job per client; returns their combined handle.</summary>
        public JobHandle Schedule(in ReplicationInput input, JobHandle dependency)
        {
            var handles = new NativeArray<JobHandle>(Clients, Allocator.Temp);
            for (int c = 0; c < Clients; c++)
            {
                handles[c] = new BuildInterestJob
                {
                    Input = input,
                    Active = ownerOf[c] != int.MinValue,
                    Team = teamOf[c],
                    Grid = gridOf[c],
                    Allowed = allowed[c],
                    KnownBits = knownBits[c],
                    KnownId = knownId[c],
                    Entered = entered[c],
                    Left = left[c],
                    MaxEnters = MaxEntersPerBuild > 0 ? MaxEntersPerBuild : int.MaxValue,
                }.Schedule(dependency);
            }
            JobHandle all = JobHandle.CombineDependencies(handles);
            handles.Dispose();
            return all;
        }

        /// <summary>Builds every client's sets now (tests).</summary>
        public void Build(in ReplicationInput input) => Schedule(input, default).Complete();

        public NativeArray<ulong> Allowed(int client) => allowed[client];
        public bool IsAllowed(int client, int index) => BitSet.Get(allowed[client], index);
        public NativeList<int> Entered(int client) => entered[client];
        public NativeList<int> Left(int client) => left[client];

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int c = 0; c < Clients; c++)
            {
                allowed[c].Dispose(); knownBits[c].Dispose(); knownId[c].Dispose(); entered[c].Dispose(); left[c].Dispose();
            }
        }
    }

    /// <summary>One client's allowed set and its Enter/Leave diff against what it knew.</summary>
    [BurstCompile]
    public struct BuildInterestJob : IJob
    {
        public ReplicationInput Input;
        public bool Active;
        public int Team, Grid;
        public NativeArray<ulong> Allowed, KnownBits;
        public NativeArray<int> KnownId;
        public NativeList<int> Entered, Left;
        /// <summary>
        /// Snapshot pacing: units past this many new arrivals stay unknown this build and arrive on a
        /// later one; until then the client is sent nothing about them.
        /// </summary>
        public int MaxEnters;

        public void Execute()
        {
            Entered.Clear();
            Left.Clear();
            for (int w = 0; w < Allowed.Length; w++) Allowed[w] = 0;
            if (Active)
            {
                for (int i = 0; i < Input.Count; i++)
                {
                    if (Input.Health[i] <= 0f) continue;
                    if (Input.Team[i] != Team && !Input.Sees(Grid, Input.Positions[i])) continue;
                    int index = NetIdAllocator.IndexOf(Input.IdOf[i]);
                    if (index < KnownId.Length) BitSet.Set(Allowed, index);
                }
            }

            for (int w = 0; w < Allowed.Length; w++)
            {
                ulong union = Allowed[w] | KnownBits[w];
                ulong now = 0;
                while (union != 0)
                {
                    int bit = math.tzcnt(union);
                    union &= union - 1;
                    int index = w * 64 + bit;
                    int current = 0;
                    if ((Allowed[w] & (1UL << bit)) != 0)
                    {
                        int slot = Input.SlotOfIndex(index);
                        if (slot >= 0) current = Input.IdOf[slot];
                    }
                    int previous = KnownId[index];
                    if (previous != current)
                    {
                        if (previous != 0) Left.Add(previous);
                        if (current != 0 && Entered.Length >= MaxEnters) current = 0; // paced: next build
                        if (current != 0) Entered.Add(current);
                        KnownId[index] = current;
                    }
                    if (current != 0) now |= 1UL << bit;
                }
                KnownBits[w] = now;
                Allowed[w] = now;
            }
        }
    }
}

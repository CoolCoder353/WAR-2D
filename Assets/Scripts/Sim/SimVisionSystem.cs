using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Fog of war. Every <see cref="SimData.VisionInterval"/> ticks it recomputes each team's vision grid
    /// on the fog grid (<see cref="SimData.FogCellSize"/> tiles per cell): every living unit and building
    /// is a sight source at its fog cell; sources sharing a cell and team are merged (largest radius wins);
    /// each source marks the cells within its radius that it has line of sight to (opaque cells stop the
    /// line but are themselves seen). Open ground skips the line tests (summed-area table of opaque cells).
    /// Cells whose visibility changed are queued on <see cref="SimData.FogChanges"/> for replication, and
    /// seen cells are added to <see cref="SimData.Explored"/>.
    /// </summary>
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimMovementSystem))]
    [UpdateBefore(typeof(SimEconomySystem))]
    public partial struct SimVisionSystem : ISystem
    {
        private EntityQuery units;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimData>();
            units = state.GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            if ((clock.Tick - 1) % data.VisionInterval != 0) return;
            int total = SimData.MaxOwners * data.FogCells;

            state.Dependency = new RollVisionJob { Visible = data.Visible, VisiblePrev = data.VisiblePrev }
                .Schedule(total, 8192, state.Dependency);
            state.Dependency = new CollectSourcesJob
            {
                Positions = data.Positions,
                Health = data.Health,
                Type = data.Type,
                OwnerSlot = data.OwnerSlot,
                SightByType = data.SightByType,
                ShareVisionMask = data.ShareVisionMask,
                BuildingPositions = data.BuildingPositions,
                BuildingHealth = data.BuildingHealth,
                BuildingSight = data.BuildingSight,
                BuildingOwnerSlot = data.BuildingOwnerSlot,
                SourceRadius = data.SourceRadius,
                Sources = data.Sources,
                FogCellSize = data.FogCellSize,
                FogW = data.FogW,
                FogH = data.FogH,
                FogCells = data.FogCells,
                UnitCount = clock.UnitCount,
                BuildingCount = math.min(data.BuildingCount[0], data.BuildingCapacity),
            }.Schedule(state.Dependency);
            state.Dependency = new StampVisionJob
            {
                Sources = data.Sources.AsDeferredJobArray(),
                SourceRadius = data.SourceRadius,
                Visible = data.Visible,
                Opaque = data.Opaque,
                OpaqueSum = data.OpaqueSum,
                FogW = data.FogW,
                FogH = data.FogH,
                FogCells = data.FogCells,
            }.Schedule(data.Sources, 8, state.Dependency);
            state.Dependency = new DiffVisionJob
            {
                Visible = data.Visible,
                VisiblePrev = data.VisiblePrev,
                Explored = data.Explored,
                Changes = data.FogChanges.AsParallelWriter(),
            }.Schedule(total, 8192, state.Dependency);
        }
    }

    /// <summary>Keeps the last pass's visibility and clears the grid for this one.</summary>
    [BurstCompile]
    public struct RollVisionJob : IJobParallelFor
    {
        public NativeArray<byte> Visible, VisiblePrev;

        public void Execute(int i)
        {
            VisiblePrev[i] = Visible[i];
            Visible[i] = 0;
        }
    }

    /// <summary>
    /// Merges units and buildings into one sight source per (grid, fog cell). Each source goes on its
    /// owner's grid and on the grid of every slot its owner shares vision with.
    /// </summary>
    [BurstCompile]
    public struct CollectSourcesJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Positions, BuildingPositions;
        [ReadOnly] public NativeArray<float> Health, SightByType, BuildingHealth, BuildingSight;
        [ReadOnly] public NativeArray<byte> Type, OwnerSlot, BuildingOwnerSlot;
        [ReadOnly] public NativeArray<ushort> ShareVisionMask;
        public NativeArray<byte> SourceRadius;
        public NativeList<int> Sources;
        public int FogCellSize, FogW, FogH, FogCells;
        public int UnitCount, BuildingCount;

        public void Execute()
        {
            Sources.Clear();
            float inv = 1f / FogCellSize;
            for (int i = 0; i < UnitCount; i++)
            {
                if (Health[i] <= 0f) continue;
                int type = Type[i];
                float sight = type < SightByType.Length ? SightByType[type] : 0f;
                AddShared(OwnerSlot[i], Positions[i], sight * inv);
            }
            for (int b = 0; b < BuildingCount; b++)
            {
                if (BuildingHealth[b] <= 0f || BuildingSight[b] <= 0f) continue;
                AddShared(BuildingOwnerSlot[b], BuildingPositions[b], BuildingSight[b] * inv);
            }
        }

        /// <summary>Adds the source to its owner slot's grid and to every grid the owner shares with.</summary>
        private void AddShared(int slot, float2 position, float radiusCells)
        {
            if ((uint)slot >= SimData.MaxOwners) return;
            Add(slot, position, radiusCells);
            int shared = ShareVisionMask[slot];
            while (shared != 0)
            {
                int r = math.tzcnt(shared);
                shared &= shared - 1;
                Add(r, position, radiusCells);
            }
        }

        private void Add(int grid, float2 position, float radiusCells)
        {
            int2 c = math.clamp((int2)math.floor(position / FogCellSize), 0, new int2(FogW - 1, FogH - 1));
            int index = grid * FogCells + c.y * FogW + c.x;
            byte radius = (byte)math.clamp((int)math.ceil(radiusCells), 1, 255);
            byte old = SourceRadius[index];
            if (old == 0) Sources.Add(index);
            if (radius > old) SourceRadius[index] = radius;
        }
    }

    /// <summary>Marks what one source sees. Sources write the same value (1), so overlapping writes are harmless.</summary>
    [BurstCompile]
    public struct StampVisionJob : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<int> Sources;
        [NativeDisableParallelForRestriction] public NativeArray<byte> SourceRadius;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Visible;
        [ReadOnly] public NativeArray<byte> Opaque;
        [ReadOnly] public NativeArray<int> OpaqueSum;
        public int FogW, FogH, FogCells;

        public void Execute(int s)
        {
            int index = Sources[s];
            int radius = SourceRadius[index];
            SourceRadius[index] = 0; // ready for the next pass; each source owns its index
            int grid = index / FogCells;
            int cell = index - grid * FogCells;
            int2 c = new int2(cell % FogW, cell / FogW);
            int2 lo = math.max(c - radius, 0);
            int2 hi = math.min(c + radius, new int2(FogW - 1, FogH - 1));
            int stride = FogW + 1;
            int opaque = OpaqueSum[(hi.y + 1) * stride + hi.x + 1] - OpaqueSum[lo.y * stride + hi.x + 1]
                         - OpaqueSum[(hi.y + 1) * stride + lo.x] + OpaqueSum[lo.y * stride + lo.x];
            int limit = radius * radius + radius; // a slightly rounder circle than r^2
            int baseIndex = grid * FogCells;

            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int2 d = new int2(x, y) - c;
                if (d.x * d.x + d.y * d.y > limit) continue;
                if (opaque > 0 && !LineOfSight(c, new int2(x, y))) continue;
                Visible[baseIndex + y * FogW + x] = 1;
            }
        }

        /// <summary>True when no opaque cell lies strictly between a and b (Bresenham).</summary>
        private bool LineOfSight(int2 a, int2 b)
        {
            int2 d = math.abs(b - a);
            int2 step = new int2(a.x < b.x ? 1 : -1, a.y < b.y ? 1 : -1);
            int err = d.x - d.y;
            int2 p = a;
            while (true)
            {
                int e2 = 2 * err;
                if (e2 > -d.y) { err -= d.y; p.x += step.x; }
                if (e2 < d.x) { err += d.x; p.y += step.y; }
                if (p.x == b.x && p.y == b.y) return true;
                if (Opaque[p.y * FogW + p.x] != 0) return false;
            }
        }
    }

    /// <summary>Queues the cells whose visibility changed and adds seen cells to the explored grid.</summary>
    [BurstCompile]
    public struct DiffVisionJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Visible, VisiblePrev;
        public NativeArray<byte> Explored;
        public NativeQueue<int>.ParallelWriter Changes;

        public void Execute(int i)
        {
            byte now = Visible[i];
            if (now != VisiblePrev[i]) Changes.Enqueue(i);
            if (now != 0) Explored[i] = 1;
        }
    }
}

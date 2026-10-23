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
    /// <para>The jam (v0.13): <see cref="RebuildJamJob"/> keeps <see cref="SimData.JamMask"/> per grid, rebuilt only when
    /// the set of jamming Jammers or the share-vision masks changed. A grid's sources see a jammed cell only from within
    /// <see cref="SimData.JamSight"/>, and its Radars don't see it at all. Everything clients learn follows the grids,
    /// so the jam needs no special case anywhere else.</para>
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

        /// <summary>Whether the stage runs on tick <paramref name="tick"/> (every <paramref name="interval"/> ticks; the perf stamps follow it).</summary>
        public static bool RunsOn(int tick, int interval) => (tick - 1) % interval == 0;

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            if (!RunsOn(clock.Tick, data.VisionInterval)) return;
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
                BuildingFlag = data.BuildingFlag,
                RadarSources = data.RadarSources,
            }.Schedule(state.Dependency);
            state.Dependency = new RebuildJamJob
            {
                BuildingCount = math.min(data.BuildingCount[0], data.BuildingCapacity),
                BuildingFlag = data.BuildingFlag,
                BuildingHealth = data.BuildingHealth,
                BuildingOwnerSlot = data.BuildingOwnerSlot,
                BuildingAnchor = data.BuildingAnchor,
                BuildingSize = data.BuildingSize,
                ShareVisionMask = data.ShareVisionMask,
                JamMask = data.JamMask,
                JamSources = data.JamSources,
                JamShare = data.JamShare,
                JamDirty = data.JamDirty,
                JamRadius = data.JamRadius,
                FogCellSize = data.FogCellSize,
                FogW = data.FogW,
                FogH = data.FogH,
                FogCells = data.FogCells,
            }.Schedule(state.Dependency);
            state.Dependency = new StampVisionJob
            {
                Sources = data.Sources.AsDeferredJobArray(),
                SourceRadius = data.SourceRadius,
                Visible = data.Visible,
                JamMask = data.JamMask,
                JamSightSq = data.JamSight * data.JamSight,
                OpaqueTile = data.OpaqueTile,
                OpaqueSum = data.OpaqueSum,
                Width = data.Width,
                Height = data.Height,
                FogCellSize = data.FogCellSize,
                FogW = data.FogW,
                FogH = data.FogH,
                FogCells = data.FogCells,
            }.Schedule(data.Sources, 8, state.Dependency);
            state.Dependency = new StampRadarJob
            {
                Sources = data.RadarSources.AsDeferredJobArray(),
                Visible = data.Visible,
                JamMask = data.JamMask,
                FogW = data.FogW,
                FogH = data.FogH,
                FogCells = data.FogCells,
            }.Schedule(data.RadarSources, 4, state.Dependency);
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
        [ReadOnly] public NativeArray<byte> Type, OwnerSlot, BuildingOwnerSlot, BuildingFlag;
        [ReadOnly] public NativeArray<ushort> ShareVisionMask;
        public NativeList<int3> RadarSources;
        public NativeArray<byte> SourceRadius;
        public NativeList<int> Sources;
        public int FogCellSize, FogW, FogH, FogCells;
        public int UnitCount, BuildingCount;

        public void Execute()
        {
            Sources.Clear();
            RadarSources.Clear();
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
                if ((BuildingFlag[b] & BuildingFlags.SeesThroughWalls) != 0) AddRadar(BuildingOwnerSlot[b], BuildingPositions[b], BuildingSight[b] * inv);
                else AddShared(BuildingOwnerSlot[b], BuildingPositions[b], BuildingSight[b] * inv);
            }
        }

        /// <summary>Adds a Radar's source (no line-of-sight test) to its owner slot's grid and every grid the owner shares with.</summary>
        private void AddRadar(int slot, float2 position, float radiusCells)
        {
            if ((uint)slot >= SimData.MaxOwners) return;
            int2 c = math.clamp((int2)math.floor(position / FogCellSize), 0, new int2(FogW - 1, FogH - 1));
            byte radius = (byte)math.clamp((int)math.ceil(radiusCells), 1, 255);
            RadarSources.Add(new int3(slot, c.y * FogW + c.x, radius));
            int shared = ShareVisionMask[slot];
            while (shared != 0)
            {
                int r = math.tzcnt(shared);
                shared &= shared - 1;
                RadarSources.Add(new int3(r, c.y * FogW + c.x, radius));
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

    /// <summary>
    /// Rebuilds the jam mask (v0.13) when it is stale: the jamming Jammers (complete, paid and alive, see
    /// <see cref="BuildingFlags.Jams"/>) or the share-vision masks differ from the ones it was built from, or
    /// <see cref="SimData.JamDirty"/> is set. A Jammer finishing, dying, going unpaid or paid again, being built
    /// complete, and a share-vision change at the boundary all show up as such a difference, so no event needs a hook.
    /// A cell of grid g is jammed when its centre lies within the radius of a Jammer whose owner slot is not g and
    /// doesn't share vision with g. Most passes only compare the lists.
    /// </summary>
    [BurstCompile]
    public struct RebuildJamJob : IJob
    {
        public int BuildingCount;
        [ReadOnly] public NativeArray<byte> BuildingFlag, BuildingOwnerSlot;
        [ReadOnly] public NativeArray<float> BuildingHealth;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;
        [ReadOnly] public NativeArray<ushort> ShareVisionMask;
        public NativeArray<byte> JamMask;
        public NativeList<int3> JamSources;
        public NativeArray<ushort> JamShare;
        public NativeArray<byte> JamDirty;
        public float JamRadius;
        public int FogCellSize, FogW, FogH, FogCells;

        /// <summary>True for a slot that jams now.</summary>
        private bool Jams(int b) =>
            (BuildingFlag[b] & (BuildingFlags.Jams | BuildingFlags.UnderConstruction | BuildingFlags.Unpaid)) == BuildingFlags.Jams &&
            BuildingHealth[b] > 0f && BuildingOwnerSlot[b] < SimData.MaxOwners;

        public void Execute()
        {
            bool dirty = JamDirty[0] != 0;
            for (int s = 0; s < SimData.MaxOwners && !dirty; s++) dirty = ShareVisionMask[s] != JamShare[s];
            int n = 0;
            for (int b = 0; b < BuildingCount; b++)
            {
                if (!Jams(b)) continue;
                var source = new int3(BuildingOwnerSlot[b], BuildingAnchor[b]);
                if (!dirty && (n >= JamSources.Length || !math.all(JamSources[n] == source))) dirty = true;
                n++;
            }
            if (!dirty && n == JamSources.Length) return;

            JamDirty[0] = 0;
            for (int s = 0; s < SimData.MaxOwners; s++) JamShare[s] = ShareVisionMask[s];
            JamSources.Clear();
            for (int i = 0; i < JamMask.Length; i++) JamMask[i] = 0;
            float radiusSq = JamRadius * JamRadius;
            int reach = (int)math.ceil(JamRadius / FogCellSize) + 1;
            for (int b = 0; b < BuildingCount; b++)
            {
                if (!Jams(b)) continue;
                int slot = BuildingOwnerSlot[b];
                JamSources.Add(new int3(slot, BuildingAnchor[b]));
                float2 centre = Footprint.VisualCenter(BuildingAnchor[b], BuildingSize[b]);
                int2 c = math.clamp((int2)math.floor(centre / FogCellSize), 0, new int2(FogW - 1, FogH - 1));
                int2 lo = math.max(c - reach, 0), hi = math.min(c + reach, new int2(FogW - 1, FogH - 1));
                for (int g = 0; g < SimData.MaxOwners; g++)
                {
                    // Never the Jammer's own grid, nor a grid it shares vision with (its allies see what it sees).
                    if (g == slot || (ShareVisionMask[slot] >> g & 1) != 0) continue;
                    int baseIndex = g * FogCells;
                    for (int y = lo.y; y <= hi.y; y++)
                    for (int x = lo.x; x <= hi.x; x++)
                    {
                        float2 cell = (new float2(x, y) + 0.5f) * FogCellSize;
                        if (math.distancesq(cell, centre) <= radiusSq) JamMask[baseIndex + y * FogW + x] = 1;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Marks what one source sees. Sources write the same value (1), so overlapping writes are harmless. A jammed cell
    /// of the source's grid is marked only when the two cells' closest tiles are within <see cref="JamSightSq"/> of each other.
    /// </summary>
    [BurstCompile]
    public struct StampVisionJob : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<int> Sources;
        [NativeDisableParallelForRestriction] public NativeArray<byte> SourceRadius;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Visible;
        /// <summary>The jam mask (same layout as <see cref="Visible"/>) and the squared sight, in tiles, that still reaches into it.</summary>
        [ReadOnly] public NativeArray<byte> JamMask;
        public float JamSightSq;
        [ReadOnly] public NativeArray<byte> OpaqueTile;
        [ReadOnly] public NativeArray<int> OpaqueSum;
        public int FogW, FogH, FogCells, Width, Height, FogCellSize;

        /// <summary>The tile of fog cell <paramref name="cell"/> closest to the centre of cell <paramref name="towards"/>.</summary>
        private int2 NearestTile(int2 cell, int2 towards) =>
            math.clamp(Aim(towards), cell * FogCellSize, math.min(cell * FogCellSize + FogCellSize - 1, new int2(Width - 1, Height - 1)));

        private int2 Aim(int2 towards) => towards * FogCellSize + (FogCellSize - 1) / 2;

        /// <summary>
        /// Per axis, the tiles between the closest tiles of two fog cells <paramref name="d"/> cells apart (0 for the same
        /// row or column): the jam's <see cref="JamSightSq"/> is measured on this, so a target within JamSight tiles of a
        /// source is never refused because the two cells' centres are further apart.
        /// </summary>
        private int2 NearestTileGap(int2 d) => math.max(math.abs(d) * FogCellSize - (FogCellSize - 1), 0);

        /// <summary>
        /// The open tile of fog cell <paramref name="cell"/> closest to the centre of cell <paramref name="towards"/>, or
        /// <see cref="NearestTile"/> when that is open or the whole cell is opaque. The border rows and columns are half rock
        /// (fog cells are 2 tiles), and a trace from or to a cell's rock half runs along the rock and sees nothing.
        /// </summary>
        private int2 OpenTile(int2 cell, int2 towards)
        {
            int2 nearest = NearestTile(cell, towards);
            if (OpaqueTile[nearest.y * Width + nearest.x] == 0) return nearest;
            int2 lo = cell * FogCellSize, hi = math.min(lo + FogCellSize - 1, new int2(Width - 1, Height - 1)), aim = Aim(towards);
            int best = int.MaxValue;
            int2 open = nearest;
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                if (OpaqueTile[y * Width + x] != 0) continue;
                int2 d = new int2(x, y) - aim;
                int distance = d.x * d.x + d.y * d.y;
                if (distance < best) { best = distance; open = new int2(x, y); }
            }
            return open;
        }

        /// <summary>
        /// True when the source cell sees cell <paramref name="target"/>: a clear trace between the two cells' tiles nearest
        /// each other (the end tiles are never tested, so a wall face is seen). When that is blocked and an end tile is
        /// rock, the trace is tried again from or to that cell's nearest open tile instead, so the half-rock border rows and
        /// columns see along themselves. Never sees less than the first trace alone.
        /// </summary>
        private bool SeesCell(int2 c, int2 target)
        {
            int2 from = NearestTile(c, target), to = NearestTile(target, c);
            if (LineOfSight.Tiles(OpaqueTile, Width, from, to)) return true;
            int2 openFrom = OpaqueTile[from.y * Width + from.x] != 0 ? OpenTile(c, target) : from;
            int2 openTo = OpaqueTile[to.y * Width + to.x] != 0 ? OpenTile(target, c) : to;
            bool fromMoved = math.any(openFrom != from), toMoved = math.any(openTo != to);
            if (fromMoved && LineOfSight.Tiles(OpaqueTile, Width, openFrom, to)) return true;
            if (toMoved && LineOfSight.Tiles(OpaqueTile, Width, from, openTo)) return true;
            return fromMoved && toMoved && LineOfSight.Tiles(OpaqueTile, Width, openFrom, openTo);
        }

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
                if (JamMask[baseIndex + y * FogW + x] != 0 && math.lengthsq((float2)NearestTileGap(d)) > JamSightSq) continue;
                if (opaque > 0)
                {
                    if (!SeesCell(c, new int2(x, y))) continue;
                }
                Visible[baseIndex + y * FogW + x] = 1;
            }
        }
    }

    /// <summary>Marks what one Radar sees: its whole circle, walls or not, except the cells jammed for its grid.</summary>
    [BurstCompile]
    public struct StampRadarJob : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<int3> Sources;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Visible;
        [ReadOnly] public NativeArray<byte> JamMask;
        public int FogW, FogH, FogCells;

        public void Execute(int s)
        {
            int3 source = Sources[s];
            int radius = source.z;
            int2 c = new int2(source.y % FogW, source.y / FogW);
            int2 lo = math.max(c - radius, 0);
            int2 hi = math.min(c + radius, new int2(FogW - 1, FogH - 1));
            int limit = radius * radius + radius;
            int baseIndex = source.x * FogCells;
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int2 d = new int2(x, y) - c;
                if (d.x * d.x + d.y * d.y <= limit && JamMask[baseIndex + y * FogW + x] == 0) Visible[baseIndex + y * FogW + x] = 1;
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

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>What a combat target slot refers to.</summary>
    public static class TargetKinds
    {
        public const byte Unit = 0;
        public const byte Building = 1;
        /// <summary>A player wall tile; the target is the tile index.</summary>
        public const byte Wall = 2;

        /// <summary>
        /// True for a unit on a <see cref="Stances.Move"/> order that is still live: it neither searches
        /// for nor keeps a target until it arrives.
        /// </summary>
        public static bool IgnoresEnemies(byte stance, int orderSlot, NativeArray<byte> orderLive) =>
            stance == Stances.Move && (uint)orderSlot < (uint)orderLive.Length && orderLive[orderSlot] != 0;
    }

    /// <summary>
    /// The world a target search and a target check read: the units over the counting-sort hash, the buildings
    /// over theirs, the wall tiles, the fog grids and the line-of-sight tiles. One copy of the rules for every
    /// attacker, so a Gun turret targets exactly as a unit does (v0.13). All read-only.
    /// </summary>
    public struct SearchInput
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<byte> OwnerSlot;
        [ReadOnly] public NativeArray<ushort> AttackMask;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<int> IdOf, IndexOfId;
        public int UnitCount;
        [ReadOnly] public NativeArray<int> CellStart, Sorted;
        [ReadOnly] public NativeArray<float2> BuildingPositions;
        [ReadOnly] public NativeArray<byte> BuildingOwnerSlot;
        [ReadOnly] public NativeArray<float> BuildingHealth;
        [ReadOnly] public NativeArray<byte> BuildingFlag;
        [ReadOnly] public NativeArray<int> BuildingIds, BuildingSlotOfIndex;
        public int BuildingCount;
        [ReadOnly] public NativeArray<int> BuildingCellStart, BuildingSorted;
        public float InvCellSize;
        public int CellsX, CellsY, SearchCells; // SearchCells = ceil(max range / cell size): the cap on any type's reach
        /// <summary>Fog grids (see <see cref="SimData.Visible"/>): an attacker fires only at what its owner's grid sees now.</summary>
        [ReadOnly] public NativeArray<byte> Visible;
        public int FogCellSize, FogW, FogH, FogCells;
        [ReadOnly] public NativeArray<byte> OpaqueTile;
        [ReadOnly] public NativeArray<byte> WallState, WallOwnerSlot;
        public int Width, Height;

        /// <summary>Everything a search reads, from the tick's data.</summary>
        public static SearchInput From(in SimData data, int unitCount, int buildingCount) => new SearchInput
        {
            Positions = data.Positions,
            OwnerSlot = data.OwnerSlot,
            AttackMask = data.AttackMask,
            Health = data.Health,
            IdOf = data.IdOf,
            IndexOfId = data.IndexOfId,
            UnitCount = unitCount,
            CellStart = data.CellStart,
            Sorted = data.Sorted,
            BuildingPositions = data.BuildingPositions,
            BuildingOwnerSlot = data.BuildingOwnerSlot,
            BuildingHealth = data.BuildingHealth,
            BuildingFlag = data.BuildingFlag,
            BuildingIds = data.BuildingIds,
            BuildingSlotOfIndex = data.BuildingSlotOfIndex,
            BuildingCount = buildingCount,
            BuildingCellStart = data.BuildingCellStart,
            BuildingSorted = data.BuildingSorted,
            InvCellSize = 1f / data.CellSize,
            CellsX = data.CellsX,
            CellsY = data.CellsY,
            SearchCells = data.SearchCells,
            Visible = data.Visible,
            FogCellSize = data.FogCellSize, FogW = data.FogW, FogH = data.FogH, FogCells = data.FogCells,
            OpaqueTile = data.OpaqueTile,
            WallState = data.WallState,
            WallOwnerSlot = data.WallOwnerSlot,
            Width = data.Width,
            Height = data.Height,
        };
    }

    /// <summary>
    /// The shared target rules (v0.13): the nearest-enemy search and the check that keeps a stored target. Units
    /// (<see cref="NearestEnemyJob"/>, <see cref="ResolveTargetsJob"/>) and attacker buildings
    /// (<see cref="BuildingTargetJob"/>, <see cref="ResolveBuildingTargetsJob"/>) both call them.
    /// "Enemy" means an owner on the attacker's owner's attack mask.
    /// </summary>
    public static class TargetSearch
    {
        /// <summary>True when the fog grid starting at <paramref name="grid"/> (an owner slot times <c>FogCells</c>) sees the point now.</summary>
        public static bool Sees(in SearchInput s, int grid, float2 position) =>
            NearestEnemyJob.Sees(s.Visible, grid, position, s.FogCellSize, s.FogW, s.FogH);

        /// <summary>
        /// The nearest living enemy unit within range (and outside the minimum range) that the owner's grid sees with a
        /// clear line; with none, the nearest such enemy building that isn't hidden; with none and
        /// <paramref name="walls"/>, the nearest enemy wall tile with a clear line. <paramref name="target"/> is a unit
        /// slot, a building slot or a tile index, or -1 (then <paramref name="kind"/> is <see cref="TargetKinds.Unit"/>).
        /// </summary>
        public static bool Nearest(in SearchInput s, float2 p, int slot, float rangeSq, float minSq, bool walls, out int target, out byte kind)
        {
            int grid = slot * s.FogCells;
            int2 c = (int2)math.floor(p * s.InvCellSize);
            // Each attacker scans only the cells its own range reaches: the longest range (a Sniper's) would
            // otherwise make every Tank scan nine times the cells.
            int reach = math.clamp((int)math.ceil(math.sqrt(rangeSq) * s.InvCellSize), 1, s.SearchCells);
            int2 lo = math.max(c - reach, 0);
            int2 hi = math.min(c + reach, new int2(s.CellsX - 1, s.CellsY - 1));

            float best = rangeSq;
            int bestIndex = -1;
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * s.CellsX + cx;
                for (int k = s.CellStart[cell]; k < s.CellStart[cell + 1]; k++)
                {
                    int j = s.Sorted[k];
                    if (s.Health[j] <= 0f || !SimData.Attacks(s.AttackMask, slot, s.OwnerSlot[j])) continue;
                    float d = math.distancesq(p, s.Positions[j]);
                    if (d <= best && d >= minSq && Sees(s, grid, s.Positions[j]) && LineOfSight.ClearTiles(s.OpaqueTile, s.Width, s.Height, p, s.Positions[j])) { best = d; bestIndex = j; }
                }
            }
            if (bestIndex >= 0)
            {
                target = bestIndex;
                kind = TargetKinds.Unit;
                return true;
            }

            best = rangeSq;
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * s.CellsX + cx;
                for (int k = s.BuildingCellStart[cell]; k < s.BuildingCellStart[cell + 1]; k++)
                {
                    int j = s.BuildingSorted[k];
                    if (s.BuildingHealth[j] <= 0f || (s.BuildingFlag[j] & BuildingFlags.Hidden) != 0 || !SimData.Attacks(s.AttackMask, slot, s.BuildingOwnerSlot[j])) continue;
                    float d = math.distancesq(p, s.BuildingPositions[j]);
                    if (d <= best && d >= minSq && Sees(s, grid, s.BuildingPositions[j]) && LineOfSight.ClearTiles(s.OpaqueTile, s.Width, s.Height, p, s.BuildingPositions[j])) { best = d; bestIndex = j; }
                }
            }
            if (bestIndex >= 0)
            {
                target = bestIndex;
                kind = TargetKinds.Building;
                return true;
            }
            kind = TargetKinds.Unit;
            target = walls ? FindWall(s, p, slot, rangeSq) : -1;
            if (target >= 0) kind = TargetKinds.Wall;
            return target >= 0;
        }

        /// <summary>
        /// The nearest enemy wall tile within range with a clear line, or -1. The wall tile ends the line, so it
        /// doesn't block its own line.
        /// </summary>
        private static int FindWall(in SearchInput s, float2 p, int slot, float rangeSq)
        {
            int r = (int)math.ceil(math.sqrt(rangeSq));
            int2 c = (int2)math.floor(p);
            int2 lo = math.max(c - r, 0), hi = math.min(c + r, new int2(s.Width - 1, s.Height - 1));
            float best = rangeSq;
            int bestTile = -1;
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int t = y * s.Width + x;
                if (s.WallState[t] == WallStates.None || !SimData.Attacks(s.AttackMask, slot, s.WallOwnerSlot[t])) continue;
                float2 centre = new float2(x + 0.5f, y + 0.5f);
                float d = math.distancesq(p, centre);
                if (d <= best && LineOfSight.ClearTiles(s.OpaqueTile, s.Width, s.Height, p, centre)) { best = d; bestTile = t; }
            }
            return bestTile;
        }

        /// <summary>
        /// This tick's slot (or tile) for a stored target id of the given kind, or -1 when it must be dropped: it died,
        /// left range (or came inside the minimum range), lost the line of fire, went into the fog of the owner's grid
        /// (units and buildings), changed hands, is no longer attacked (diplomacy), is a hidden building, or no longer exists.
        /// </summary>
        public static int Keep(in SearchInput s, float2 p, int slot, float rangeSq, float minSq, int id, byte kind)
        {
            if (id <= 0) return -1;
            int index = kind == TargetKinds.Wall ? 0 : NetIdAllocator.IndexOf(id);
            if (index >= s.IndexOfId.Length) return -1;

            if (kind == TargetKinds.Wall)
            {
                if ((uint)id >= (uint)s.WallState.Length || s.WallState[id] == WallStates.None || !SimData.Attacks(s.AttackMask, slot, s.WallOwnerSlot[id])) return -1;
                float2 centre = new float2(id % s.Width + 0.5f, id / s.Width + 0.5f);
                if (math.distancesq(p, centre) > rangeSq || !LineOfSight.ClearTiles(s.OpaqueTile, s.Width, s.Height, p, centre)) return -1;
                return id;
            }
            int grid = slot * s.FogCells;
            if (kind == TargetKinds.Unit)
            {
                int j = s.IndexOfId[index];
                if ((uint)j >= (uint)s.UnitCount || s.IdOf[j] != id || s.Health[j] <= 0f || !SimData.Attacks(s.AttackMask, slot, s.OwnerSlot[j]) ||
                    math.distancesq(p, s.Positions[j]) > rangeSq || math.distancesq(p, s.Positions[j]) < minSq ||
                    !Sees(s, grid, s.Positions[j]) ||
                    !LineOfSight.ClearTiles(s.OpaqueTile, s.Width, s.Height, p, s.Positions[j])) return -1;
                return j;
            }
            {
                int j = s.BuildingSlotOfIndex[index];
                if ((uint)j >= (uint)s.BuildingCount || s.BuildingIds[j] != id || s.BuildingHealth[j] <= 0f || (s.BuildingFlag[j] & BuildingFlags.Hidden) != 0 ||
                    !SimData.Attacks(s.AttackMask, slot, s.BuildingOwnerSlot[j]) || math.distancesq(p, s.BuildingPositions[j]) > rangeSq ||
                    math.distancesq(p, s.BuildingPositions[j]) < minSq ||
                    !Sees(s, grid, s.BuildingPositions[j]) ||
                    !LineOfSight.ClearTiles(s.OpaqueTile, s.Width, s.Height, p, s.BuildingPositions[j])) return -1;
                return j;
            }
        }
    }

    /// <summary>
    /// Nearest-enemy search over the counting-sort hash. Each unit searches on one tick in
    /// <see cref="Slice"/> (staggered by slot) and keeps its resolved target in between. It looks for
    /// the nearest living enemy unit within its type's range; with none in range it falls back to the
    /// nearest enemy building, then (on an Attack-move order) the nearest enemy wall tile
    /// (<see cref="TargetSearch.Nearest"/>).
    /// </summary>
    [BurstCompile]
    public struct NearestEnemyJob : IJobParallelFor
    {
        [ReadOnly] public SearchInput Search;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<byte> Stance;
        [ReadOnly] public NativeArray<int> OrderSlot;
        [ReadOnly] public NativeArray<byte> OrderLive;
        [ReadOnly] public NativeArray<float> RangeSqByType, MinRangeSqByType;
        [ReadOnly] public NativeArray<float> DamageByType;
        public int Tick, Slice;
        public NativeArray<int> Target;
        public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            if (Slice > 1 && (i + Tick) % Slice != 0) return;
            if (Search.Health[i] <= 0f || TargetKinds.IgnoresEnemies(Stance[i], OrderSlot[i], OrderLive) || DamageByType[Type[i]] <= 0f) { Target[i] = -1; return; }

            int type = Type[i];
            float range = type < RangeSqByType.Length ? RangeSqByType[type] : 0f;
            float minSq = type < MinRangeSqByType.Length ? MinRangeSqByType[type] : 0f;
            TargetSearch.Nearest(Search, Search.Positions[i], Search.OwnerSlot[i], range, minSq, Stance[i] == Stances.AttackMove, out int target, out byte kind);
            Target[i] = target;
            TargetKind[i] = kind;
        }

        /// <summary>True when the fog grid starting at <paramref name="grid"/> (an owner slot times <c>FogCells</c>) sees the point now.</summary>
        public static bool Sees(NativeArray<byte> visible, int grid, float2 position, int fogCellSize, int fogW, int fogH)
        {
            int2 f = math.clamp((int2)math.floor(position / fogCellSize), 0, new int2(fogW - 1, fogH - 1));
            return visible[grid + f.y * fogW + f.x] != 0;
        }
    }
}

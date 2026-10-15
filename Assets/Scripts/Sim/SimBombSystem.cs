using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Hidden bombs. A finished bomb goes off when a living unit of an owner it attacks has its centre on
    /// the bomb's tile (or when it is in <see cref="SimData.ForcedBombs"/>). The blast hurts every unit,
    /// building and wall tile within <see cref="SimData.BlastRadius"/>, friends included, and destroys the
    /// bomb. Unit damage lands in the SoA (combat's write-back stores it); building and wall damage wait for
    /// the next boundary.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimHashSystem))]
    [UpdateBefore(typeof(SimCombatSystem))]
    public partial struct SimBombSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimData>();
            // Declared, not used: being a writer of Unit chains this stage between the hash and combat.
            state.GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            state.Dependency = new BombJob
            {
                UnitCount = clock.UnitCount,
                BuildingCount = math.min(data.BuildingCount[0], data.BuildingCapacity),
                Width = data.Width, Height = data.Height,
                CellSize = data.CellSize, CellsX = data.CellsX, CellsY = data.CellsY,
                Radius = data.BlastRadius, Damage = data.BlastDamage,
                Positions = data.Positions, UnitHealth = data.Health, OwnerSlot = data.OwnerSlot, OwnerId = data.OwnerId, LastHitBy = data.LastHitBy,
                CellStart = data.CellStart, Sorted = data.Sorted, AttackMask = data.AttackMask,
                BuildingPositions = data.BuildingPositions, BuildingAnchor = data.BuildingAnchor, BuildingSize = data.BuildingSize,
                BuildingFlag = data.BuildingFlag, BuildingHealth = data.BuildingHealth, BuildingDamage = data.BuildingDamage,
                BuildingOwnerSlot = data.BuildingOwnerSlot, BuildingOwnerId = data.BuildingOwnerId, BuildingIds = data.BuildingIds,
                WallState = data.WallState, WallDamage = data.WallDamage,
                Blasts = data.BombBlasts, Forced = data.ForcedBombs,
            }.Schedule(state.Dependency);
        }
    }

    /// <summary>One pass over the buildings; bombs are few.</summary>
    [BurstCompile]
    public struct BombJob : IJob
    {
        public int UnitCount, BuildingCount, Width, Height, CellSize, CellsX, CellsY;
        public float Radius, Damage;
        [ReadOnly] public NativeArray<float2> Positions, BuildingPositions;
        [ReadOnly] public NativeArray<byte> OwnerSlot, BuildingFlag, BuildingOwnerSlot, WallState;
        [ReadOnly] public NativeArray<int> OwnerId, CellStart, Sorted, BuildingOwnerId, BuildingIds;
        [ReadOnly] public NativeArray<ushort> AttackMask;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;
        public NativeArray<float> UnitHealth, BuildingHealth, BuildingDamage;
        public NativeArray<int> LastHitBy;
        public NativeQueue<TileWork> WallDamage;
        public NativeQueue<BombBlast> Blasts;
        [ReadOnly] public NativeParallelHashSet<int> Forced;

        public void Execute()
        {
            for (int b = 0; b < BuildingCount; b++)
            {
                byte flag = BuildingFlag[b];
                if ((flag & BuildingFlags.Hidden) == 0 || (flag & BuildingFlags.UnderConstruction) != 0 || BuildingHealth[b] <= 0f) continue;
                int2 tile = (int2)math.floor(BuildingPositions[b]);
                if (!Forced.Contains(BuildingIds[b]) && !SteppedOn(b, tile)) continue;
                Blast(b, tile);
            }
        }

        private bool SteppedOn(int b, int2 tile)
        {
            int2 cell = math.clamp(tile / CellSize, 0, new int2(CellsX - 1, CellsY - 1));
            int c = cell.y * CellsX + cell.x;
            for (int k = CellStart[c]; k < CellStart[c + 1]; k++)
            {
                int u = Sorted[k];
                if (UnitHealth[u] <= 0f || !tile.Equals((int2)math.floor(Positions[u]))) continue;
                if (BombRules.Triggers(AttackMask, BuildingOwnerSlot[b], OwnerSlot[u])) return true;
            }
            return false;
        }

        private void Blast(int bomb, int2 tile)
        {
            float2 centre = (float2)tile + 0.5f;
            float rSq = Radius * Radius;
            int owner = BuildingOwnerId[bomb];

            int reach = (int)math.ceil(Radius / CellSize) + 1;
            int2 cell = math.clamp(tile / CellSize, 0, new int2(CellsX - 1, CellsY - 1));
            for (int cy = math.max(cell.y - reach, 0); cy <= math.min(cell.y + reach, CellsY - 1); cy++)
            for (int cx = math.max(cell.x - reach, 0); cx <= math.min(cell.x + reach, CellsX - 1); cx++)
            {
                int c = cy * CellsX + cx;
                for (int k = CellStart[c]; k < CellStart[c + 1]; k++)
                {
                    int u = Sorted[k];
                    if (UnitHealth[u] <= 0f || math.distancesq(Positions[u], centre) > rSq) continue;
                    UnitHealth[u] = math.max(0f, UnitHealth[u] - Damage);
                    LastHitBy[u] = owner;
                }
            }

            for (int other = 0; other < BuildingCount; other++)
            {
                if (other == bomb)
                {
                    BuildingDamage[other] += BuildingHealth[other];
                    BuildingHealth[other] = 0f;
                    continue;
                }
                if (BuildingHealth[other] <= 0f) continue;
                float2 lo = Footprint.Start(BuildingAnchor[other], BuildingSize[other]), hi = lo + BuildingSize[other];
                float2 gap = math.max(math.max(lo - centre, centre - hi), 0f);
                if (math.lengthsq(gap) > rSq) continue;
                BuildingDamage[other] += Damage;
                BuildingHealth[other] = math.max(0f, BuildingHealth[other] - Damage);
            }

            int r = (int)math.ceil(Radius);
            for (int y = math.max(tile.y - r, 0); y <= math.min(tile.y + r, Height - 1); y++)
            for (int x = math.max(tile.x - r, 0); x <= math.min(tile.x + r, Width - 1); x++)
            {
                int w = y * Width + x;
                if (WallState[w] == WallStates.None || math.distancesq(new float2(x + 0.5f, y + 0.5f), centre) > rSq) continue;
                WallDamage.Enqueue(new TileWork { Tile = w, Amount = Damage, OwnerId = owner });
            }

            Blasts.Enqueue(new BombBlast { Position = centre, OwnerId = owner });
        }
    }
}

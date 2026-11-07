using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Hidden bombs. A finished, paid bomb goes off when a living unit of an owner it attacks comes within
    /// <see cref="SimData.MineTriggerRadius"/> tiles of the bomb's tile centre (or when it is in <see cref="SimData.ForcedBombs"/>;
    /// playtest 2026-11-04: it used to need the unit's centre on the very tile). The blast hurts every unit, building and wall tile
    /// within <see cref="SimData.BlastRadius"/>, those its owner doesn't attack at <see cref="SimData.MineFriendlyDamage"/> (less each unit's v0.20 blast resistance; a Bomber in the air is untouched), and destroys the
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
                Radius = data.BlastRadius, Damage = data.BlastDamage, TriggerRadius = data.MineTriggerRadius, FriendlyDamage = data.MineFriendlyDamage,
                Positions = data.Positions, UnitHealth = data.Health, OwnerSlot = data.OwnerSlot, OwnerId = data.OwnerId, LastHitBy = data.LastHitBy,
                CellStart = data.CellStart, Sorted = data.Sorted, AttackMask = data.AttackMask,
                BuildingPositions = data.BuildingPositions, BuildingAnchor = data.BuildingAnchor, BuildingSize = data.BuildingSize,
                BuildingFlag = data.BuildingFlag, BuildingHealth = data.BuildingHealth, BuildingDamage = data.BuildingDamage,
                BuildingOwnerSlot = data.BuildingOwnerSlot, BuildingOwnerId = data.BuildingOwnerId, BuildingIds = data.BuildingIds,
                WallState = data.WallState, WallOwnerSlot = data.WallOwnerSlot, WallDamage = data.WallDamage,
                Blasts = data.BombBlasts, Forced = data.ForcedBombs,
                Type = data.Type, IdOf = data.IdOf, BlastResistByType = data.BlastResistByType, Combat = data.Combat, LastHitKind = data.LastHitKind, Tick = clock.Tick,
            }.Schedule(state.Dependency);
        }
    }

    /// <summary>One pass over the buildings; bombs are few.</summary>
    [BurstCompile]
    public struct BombJob : IJob
    {
        public int UnitCount, BuildingCount, Width, Height, CellSize, CellsX, CellsY;
        public float Radius, Damage, TriggerRadius, FriendlyDamage;
        [ReadOnly] public NativeArray<float2> Positions, BuildingPositions;
        [ReadOnly] public NativeArray<byte> OwnerSlot, BuildingFlag, BuildingOwnerSlot, WallState, WallOwnerSlot;
        [ReadOnly] public NativeArray<int> OwnerId, CellStart, Sorted, BuildingOwnerId, BuildingIds;
        [ReadOnly] public NativeArray<ushort> AttackMask;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;
        public NativeArray<float> UnitHealth, BuildingHealth, BuildingDamage;
        public NativeArray<int> LastHitBy;
        public NativeQueue<TileWork> WallDamage;
        public NativeQueue<BombBlast> Blasts;
        [ReadOnly] public NativeParallelHashSet<int> Forced;
        /// <summary>v0.20: blast resistance, and who is in the air (untouched) and what last hurt each unit (a Mine's blast).</summary>
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<float> BlastResistByType;
        [ReadOnly] public NativeArray<UnitCombat> Combat;
        public NativeArray<byte> LastHitKind;
        public int Tick;

        public void Execute()
        {
            for (int b = 0; b < BuildingCount; b++)
            {
                byte flag = BuildingFlag[b];
                if ((flag & BuildingFlags.Hidden) == 0 || (flag & BuildingFlags.UnderConstruction) != 0 || BuildingHealth[b] <= 0f) continue;
                int2 tile = (int2)math.floor(BuildingPositions[b]);
                // An unpaid bomb is a dud until it is paid; its owner can still set it off.
                if (!Forced.Contains(BuildingIds[b]) && ((flag & BuildingFlags.Unpaid) != 0 || !SteppedOn(b, tile))) continue;
                Blast(b, tile);
            }
        }

        private bool SteppedOn(int b, int2 tile)
        {
            float2 centre = (float2)tile + 0.5f;
            float rSq = TriggerRadius * TriggerRadius;
            int reach = (int)math.ceil(TriggerRadius / CellSize);
            int2 cell = math.clamp(tile / CellSize, 0, new int2(CellsX - 1, CellsY - 1));
            for (int cy = math.max(cell.y - reach, 0); cy <= math.min(cell.y + reach, CellsY - 1); cy++)
            for (int cx = math.max(cell.x - reach, 0); cx <= math.min(cell.x + reach, CellsX - 1); cx++)
            {
                int c = cy * CellsX + cx;
                for (int k = CellStart[c]; k < CellStart[c + 1]; k++)
                {
                    int u = Sorted[k];
                    if (UnitHealth[u] <= 0f || math.distancesq(Positions[u], centre) > rSq) continue;
                    // A Bomber in the air touches nothing: it flies over a Mine as over units (its blast would miss it anyway).
                    int id = IdOf[u];
                    if (UnitCombat.For(Combat[NetIdAllocator.IndexOf(id)], id).AirborneAt(id, Tick)) continue;
                    if (BombRules.Triggers(AttackMask, BuildingOwnerSlot[b], OwnerSlot[u])) return true;
                }
            }
            return false;
        }

        private void Blast(int bomb, int2 tile)
        {
            float2 centre = (float2)tile + 0.5f;
            float rSq = Radius * Radius;
            int owner = BuildingOwnerId[bomb], slot = BuildingOwnerSlot[bomb];

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
                    int id = IdOf[u], index = NetIdAllocator.IndexOf(id);
                    UnitCombat state = UnitCombat.For(Combat[index], id);
                    if (state.AirborneAt(id, Tick)) continue;
                    UnitHealth[u] = math.max(0f, UnitHealth[u] - CombatRules.Resisted(Damage * Share(slot, OwnerSlot[u]), BlastResistByType[Type[u]]));
                    LastHitBy[u] = owner;
                    LastHitKind[index] = HitKinds.Blast;
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
                float amount = Damage * Share(slot, BuildingOwnerSlot[other]);
                BuildingDamage[other] += amount;
                BuildingHealth[other] = math.max(0f, BuildingHealth[other] - amount);
            }

            int r = (int)math.ceil(Radius);
            for (int y = math.max(tile.y - r, 0); y <= math.min(tile.y + r, Height - 1); y++)
            for (int x = math.max(tile.x - r, 0); x <= math.min(tile.x + r, Width - 1); x++)
            {
                int w = y * Width + x;
                if (WallState[w] == WallStates.None || math.distancesq(new float2(x + 0.5f, y + 0.5f), centre) > rSq) continue;
                WallDamage.Enqueue(new TileWork { Tile = w, Amount = Damage * Share(slot, WallOwnerSlot[w]), OwnerId = owner });
            }

            Blasts.Enqueue(new BombBlast { Position = centre, OwnerId = owner });
        }

        private float Share(int from, int to) => SimData.Attacks(AttackMask, from, to) ? 1f : FriendlyDamage;
    }
}

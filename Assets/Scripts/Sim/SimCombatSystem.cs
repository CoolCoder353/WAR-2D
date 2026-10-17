using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Rules;

namespace WAR2D.Sim
{
    /// <summary>One hit, queued by the attack job and applied by a single writer.</summary>
    public struct DamageHit
    {
        public byte Kind;
        public int Slot;
        public float Amount;
        /// <summary>The attacker's owner (who gets the kill).</summary>
        public int AttackerOwner;
    }

    /// <summary>
    /// Combat: resolve last tick's targets to this tick's slots, run the sliced nearest-enemy search, then
    /// attack on cooldown. Damage is per unit type times the damage table's multiplier for the target
    /// class. Unit damage lands in the SoA; building damage accumulates in
    /// <see cref="SimData.BuildingDamage"/> for the next boundary to apply.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimHashSystem))]
    public partial struct SimCombatSystem : ISystem
    {
        private NativeQueue<DamageHit> hits;
        private NativeList<SplashRequest> splashes;
        private EntityQuery units;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimData>();
            hits = new NativeQueue<DamageHit>(Allocator.Persistent);
            splashes = new NativeList<SplashRequest>(1024, Allocator.Persistent);
            units = state.GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        public void OnDestroy(ref SystemState state)
        {
            state.Dependency.Complete();
            hits.Dispose();
            splashes.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            int count = clock.UnitCount;
            int buildings = math.min(data.BuildingCount[0], data.BuildingCapacity);
            if (data.AttackEvents.Capacity < count) data.AttackEvents.Capacity = count;

            state.Dependency = new ResolveTargetsJob
            {
                Positions = data.Positions,
                OwnerSlot = data.OwnerSlot,
                AttackMask = data.AttackMask,
                Health = data.Health,
                Type = data.Type,
                Stance = data.Stance,
                OrderSlot = data.OrderSlot,
                OrderLive = data.OrderLive,
                RangeSqByType = data.SearchSqByType,
                MinRangeSqByType = data.MinRangeSqByType,
                IdOf = data.IdOf,
                IndexOfId = data.IndexOfId,
                UnitCount = count,
                BuildingPositions = data.BuildingPositions,
                BuildingOwnerSlot = data.BuildingOwnerSlot,
                BuildingHealth = data.BuildingHealth,
                BuildingIds = data.BuildingIds,
                BuildingSlotOfIndex = data.BuildingSlotOfIndex,
                BuildingCount = buildings,
                OpaqueTile = data.OpaqueTile,
                DamageByType = data.DamageByType,
                WallState = data.WallState,
                WallOwnerSlot = data.WallOwnerSlot,
                BuildingFlag = data.BuildingFlag,
                Width = data.Width,
                Height = data.Height,
                Visible = data.Visible,
                FogCellSize = data.FogCellSize, FogW = data.FogW, FogH = data.FogH, FogCells = data.FogCells,
                Target = data.Target,
                TargetKind = data.TargetKind,
            }.Schedule(count, 256, state.Dependency);

            state.Dependency = new NearestEnemyJob
            {
                Positions = data.Positions,
                OwnerSlot = data.OwnerSlot,
                AttackMask = data.AttackMask,
                Health = data.Health,
                Type = data.Type,
                Stance = data.Stance,
                OrderSlot = data.OrderSlot,
                OrderLive = data.OrderLive,
                RangeSqByType = data.SearchSqByType,
                MinRangeSqByType = data.MinRangeSqByType,
                CellStart = data.CellStart,
                Sorted = data.Sorted,
                BuildingPositions = data.BuildingPositions,
                BuildingOwnerSlot = data.BuildingOwnerSlot,
                BuildingHealth = data.BuildingHealth,
                BuildingCellStart = data.BuildingCellStart,
                BuildingSorted = data.BuildingSorted,
                InvCellSize = 1f / data.CellSize,
                CellsX = data.CellsX,
                CellsY = data.CellsY,
                SearchCells = data.SearchCells,
                Visible = data.Visible,
                FogCellSize = data.FogCellSize, FogW = data.FogW, FogH = data.FogH, FogCells = data.FogCells,
                Tick = clock.Tick % data.Slice,
                Slice = data.Slice,
                OpaqueTile = data.OpaqueTile,
                DamageByType = data.DamageByType,
                WallState = data.WallState,
                WallOwnerSlot = data.WallOwnerSlot,
                BuildingFlag = data.BuildingFlag,
                Width = data.Width,
                Height = data.Height,
                Target = data.Target,
                TargetKind = data.TargetKind,
            }.Schedule(count, 256, state.Dependency);

            // Every unit may queue one splash this tick; the parallel writer can't grow the list.
            splashes.Clear();
            if (splashes.Capacity < count) splashes.Capacity = count;
            state.Dependency = new AttackJob
            {
                Health = data.Health,
                Type = data.Type,
                OwnerId = data.OwnerId,
                OwnerSlot = data.OwnerSlot,
                Cooldown = data.Cooldown,
                Target = data.Target,
                TargetKind = data.TargetKind,
                IdOf = data.IdOf,
                BuildingIds = data.BuildingIds,
                Positions = data.Positions,
                BuildingPositions = data.BuildingPositions,
                Width = data.Width,
                DamageByType = data.DamageByType,
                CooldownByType = data.CooldownByType,
                ArmourByType = data.ArmourByType,
                SplashByType = data.SplashByType,
                BlastRadiusByType = data.BlastRadiusByType,
                DamageTable = data.DamageTable,
                Hits = hits.AsParallelWriter(),
                Splashes = splashes.AsParallelWriter(),
                Events = data.AttackEvents.AsParallelWriter(),
                Dt = data.Dt,
            }.Schedule(count, 256, state.Dependency);

            state.Dependency = new SplashJob
            {
                Requests = splashes.AsDeferredJobArray(),
                Hits = hits.AsParallelWriter(),
                UnitCount = count,
                BuildingCount = buildings,
                Width = data.Width, Height = data.Height,
                CellSize = data.CellSize, CellsX = data.CellsX, CellsY = data.CellsY,
                Positions = data.Positions, UnitHealth = data.Health, OwnerSlot = data.OwnerSlot, Type = data.Type,
                CellStart = data.CellStart, Sorted = data.Sorted, AttackMask = data.AttackMask,
                BuildingPositions = data.BuildingPositions, BuildingAnchor = data.BuildingAnchor, BuildingSize = data.BuildingSize,
                BuildingFlag = data.BuildingFlag, BuildingHealth = data.BuildingHealth, BuildingOwnerSlot = data.BuildingOwnerSlot,
                BuildingCellStart = data.BuildingCellStart, BuildingSorted = data.BuildingSorted,
                WallState = data.WallState, WallOwnerSlot = data.WallOwnerSlot,
                ArmourByType = data.ArmourByType, DamageTable = data.DamageTable,
            }.Schedule(splashes, 8, state.Dependency);

            state.Dependency = new BomberJob
            {
                Hits = hits,
                Blasts = data.BombBlasts,
                Forced = data.ForcedBombers,
                UnitCount = count,
                BuildingCount = buildings,
                Width = data.Width, Height = data.Height,
                CellSize = data.CellSize, CellsX = data.CellsX, CellsY = data.CellsY,
                Positions = data.Positions, UnitHealth = data.Health, Radius = data.Radius, OwnerSlot = data.OwnerSlot, OwnerId = data.OwnerId,
                Type = data.Type, IdOf = data.IdOf, Target = data.Target, TargetKind = data.TargetKind,
                CellStart = data.CellStart, Sorted = data.Sorted,
                BuildingPositions = data.BuildingPositions, BuildingAnchor = data.BuildingAnchor, BuildingSize = data.BuildingSize,
                BuildingHealth = data.BuildingHealth, WallState = data.WallState,
                RangeSqByType = data.RangeSqByType, BlastRadiusByType = data.BlastRadiusByType, BlastDamageByType = data.BlastDamageByType,
                ArmourByType = data.ArmourByType, DamageTable = data.DamageTable,
            }.Schedule(state.Dependency);

            state.Dependency = new ApplyDamageJob
            {
                Hits = hits,
                Health = data.Health,
                LastHitBy = data.LastHitBy,
                BuildingHealth = data.BuildingHealth,
                BuildingDamage = data.BuildingDamage,
                WallDamage = data.WallDamage,
            }.Schedule(state.Dependency);

            state.Dependency = new WriteBackCombatJob
            {
                Health = data.Health,
                Cooldown = data.Cooldown,
                LastHitBy = data.LastHitBy,
                Target = data.Target,
                TargetKind = data.TargetKind,
                IdOf = data.IdOf,
                BuildingIds = data.BuildingIds,
                Count = count,
            }.ScheduleParallel(units, state.Dependency);
        }
    }

    /// <summary>
    /// Turns each unit's stored target id into this tick's slot, dropping targets that died, left range,
    /// changed hands, are no longer attacked (diplomacy), went into the fog of the attacker's side or no longer exist,
    /// and the targets of units on a live Move order.
    /// </summary>
    [BurstCompile]
    public struct ResolveTargetsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<byte> OwnerSlot;
        [ReadOnly] public NativeArray<ushort> AttackMask;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<byte> Stance;
        [ReadOnly] public NativeArray<int> OrderSlot;
        [ReadOnly] public NativeArray<byte> OrderLive;
        [ReadOnly] public NativeArray<float> RangeSqByType;
        [ReadOnly] public NativeArray<float> MinRangeSqByType;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> IndexOfId;
        public int UnitCount;
        [ReadOnly] public NativeArray<float2> BuildingPositions;
        [ReadOnly] public NativeArray<byte> BuildingOwnerSlot;
        [ReadOnly] public NativeArray<float> BuildingHealth;
        [ReadOnly] public NativeArray<int> BuildingIds;
        [ReadOnly] public NativeArray<int> BuildingSlotOfIndex;
        public int BuildingCount;
        [ReadOnly] public NativeArray<byte> OpaqueTile;
        [ReadOnly] public NativeArray<float> DamageByType;
        [ReadOnly] public NativeArray<byte> WallState, WallOwnerSlot;
        [ReadOnly] public NativeArray<byte> BuildingFlag;
        public int Width, Height;
        /// <summary>Fog grids: a kept unit or building target must still be seen by the attacker's side, as in the search.</summary>
        [ReadOnly] public NativeArray<byte> Visible;
        public int FogCellSize, FogW, FogH, FogCells;
        public NativeArray<int> Target;
        public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            int id = Target[i];
            Target[i] = -1;
            if (Health[i] <= 0f || id <= 0 || TargetKinds.IgnoresEnemies(Stance[i], OrderSlot[i], OrderLive) || DamageByType[Type[i]] <= 0f) return;
            int type = Type[i];
            float rangeSq = type < RangeSqByType.Length ? RangeSqByType[type] : 0f;
            float minSq = type < MinRangeSqByType.Length ? MinRangeSqByType[type] : 0f;
            int index = TargetKind[i] == TargetKinds.Wall ? 0 : NetIdAllocator.IndexOf(id);
            if (index >= IndexOfId.Length) return;

            if (TargetKind[i] == TargetKinds.Wall)
            {
                if ((uint)id >= (uint)WallState.Length || WallState[id] == WallStates.None || !SimData.Attacks(AttackMask, OwnerSlot[i], WallOwnerSlot[id])) return;
                float2 centre = new float2(id % Width + 0.5f, id / Width + 0.5f);
                if (math.distancesq(Positions[i], centre) > rangeSq || !LineOfSight.ClearTiles(OpaqueTile, Width, Height, Positions[i], centre)) return;
                Target[i] = id;
                return;
            }
            if (TargetKind[i] == TargetKinds.Unit)
            {
                int j = IndexOfId[index];
                if ((uint)j >= (uint)UnitCount || IdOf[j] != id || Health[j] <= 0f || !SimData.Attacks(AttackMask, OwnerSlot[i], OwnerSlot[j]) ||
                    math.distancesq(Positions[i], Positions[j]) > rangeSq || math.distancesq(Positions[i], Positions[j]) < minSq ||
                    !NearestEnemyJob.Sees(Visible, OwnerSlot[i] * FogCells, Positions[j], FogCellSize, FogW, FogH) ||
                    !LineOfSight.ClearTiles(OpaqueTile, Width, Height, Positions[i], Positions[j])) return;
                Target[i] = j;
            }
            else
            {
                int j = BuildingSlotOfIndex[index];
                if ((uint)j >= (uint)BuildingCount || BuildingIds[j] != id || BuildingHealth[j] <= 0f || (BuildingFlag[j] & BuildingFlags.Hidden) != 0 ||
                    !SimData.Attacks(AttackMask, OwnerSlot[i], BuildingOwnerSlot[j]) || math.distancesq(Positions[i], BuildingPositions[j]) > rangeSq ||
                    math.distancesq(Positions[i], BuildingPositions[j]) < minSq ||
                    !NearestEnemyJob.Sees(Visible, OwnerSlot[i] * FogCells, BuildingPositions[j], FogCellSize, FogW, FogH) ||
                    !LineOfSight.ClearTiles(OpaqueTile, Width, Height, Positions[i], BuildingPositions[j])) return;
                Target[i] = j;
            }
        }
    }

    /// <summary>A splash hit to spread around a point: queued by the attack job, spread by <see cref="SplashJob"/>.</summary>
    public struct SplashRequest
    {
        public float2 Centre;
        public float Radius;
        /// <summary>Damage before the damage table's multiplier (the attacker's damage).</summary>
        public float Damage;
        public byte AttackerType;
        public byte AttackerSlot;
        public int AttackerOwner;
        /// <summary>The target of the direct hit, which the splash skips (the direct hit already landed on it).</summary>
        public byte PrimaryKind;
        public int PrimarySlot;
    }

    /// <summary>
    /// Attacks the resolved target when the cooldown allows; queues the hit (times the damage table's
    /// multiplier for the target's armour class, building or wall), a splash request for units that have
    /// one, and a tracer event. A Suicide bomber never attacks: <see cref="BomberJob"/> detonates it.
    /// </summary>
    [BurstCompile]
    public struct AttackJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<int> OwnerId;
        public NativeArray<float> Cooldown;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<byte> TargetKind;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> BuildingIds;
        [ReadOnly] public NativeArray<float2> Positions, BuildingPositions;
        public int Width;
        [ReadOnly] public NativeArray<float> DamageByType;
        [ReadOnly] public NativeArray<float> CooldownByType;
        [ReadOnly] public NativeArray<byte> ArmourByType;
        [ReadOnly] public NativeArray<float> SplashByType, BlastRadiusByType;
        [ReadOnly] public NativeArray<float> DamageTable;
        [ReadOnly] public NativeArray<byte> OwnerSlot;
        public NativeQueue<DamageHit>.ParallelWriter Hits;
        public NativeList<SplashRequest>.ParallelWriter Splashes;
        public NativeList<int2>.ParallelWriter Events;
        public float Dt;

        public void Execute(int i)
        {
            float cooldown = math.max(0f, Cooldown[i] - Dt);
            int target = Target[i];
            int type = Type[i];
            if (Health[i] <= 0f || target < 0 || cooldown > 0f || (type < BlastRadiusByType.Length && BlastRadiusByType[type] > 0f))
            {
                Cooldown[i] = cooldown;
                return;
            }
            byte kind = TargetKind[i];
            TargetClass targetClass = kind == TargetKinds.Unit ? (TargetClass)ArmourByType[Type[target]] : kind == TargetKinds.Wall ? TargetClass.Wall : TargetClass.Building;
            float multiplier = WAR2D.Rules.DamageTable.Get(DamageTable, type, targetClass);
            float baseDamage = type < DamageByType.Length ? DamageByType[type] : 0f;
            Hits.Enqueue(new DamageHit { Kind = kind, Slot = target, Amount = baseDamage * multiplier, AttackerOwner = OwnerId[i] });
            float splash = type < SplashByType.Length ? SplashByType[type] : 0f;
            if (splash > 0f)
            {
                float2 centre = kind == TargetKinds.Unit ? Positions[target] : kind == TargetKinds.Wall ? new float2(target % Width + 0.5f, target / Width + 0.5f) : BuildingPositions[target];
                Splashes.AddNoResize(new SplashRequest
                {
                    Centre = centre, Radius = splash, Damage = baseDamage, AttackerType = (byte)type, AttackerSlot = OwnerSlot[i],
                    AttackerOwner = OwnerId[i], PrimaryKind = kind, PrimarySlot = target,
                });
            }
            if (kind != TargetKinds.Wall) // a wall is a tile, not an entity: its damage state travels in the terrain delta (plan P4)
            Events.AddNoResize(new int2(IdOf[i], kind == TargetKinds.Unit ? IdOf[target] : BuildingIds[target]));
            Cooldown[i] = type < CooldownByType.Length ? CooldownByType[type] : 1f;
        }
    }

    /// <summary>
    /// Spreads each splash request: every enemy unit, building and wall tile within the radius of the impact
    /// (the target of the direct hit excepted) takes the attacker's damage, 100 % at the centre to 50 % at
    /// the edge, times the damage table's multiplier for its class. Allies are not hit. The hits go through
    /// the same queue, so <see cref="ApplyDamageJob"/> stays the single writer.
    /// </summary>
    [BurstCompile]
    public struct SplashJob : IJobParallelForDefer
    {
        [ReadOnly] public NativeArray<SplashRequest> Requests;
        public NativeQueue<DamageHit>.ParallelWriter Hits;
        public int UnitCount, BuildingCount, Width, Height, CellSize, CellsX, CellsY;
        [ReadOnly] public NativeArray<float2> Positions, BuildingPositions;
        [ReadOnly] public NativeArray<float> UnitHealth, BuildingHealth;
        [ReadOnly] public NativeArray<byte> OwnerSlot, Type, BuildingFlag, BuildingOwnerSlot, WallState, WallOwnerSlot, ArmourByType;
        [ReadOnly] public NativeArray<int> CellStart, Sorted, BuildingCellStart, BuildingSorted;
        [ReadOnly] public NativeArray<ushort> AttackMask;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;
        [ReadOnly] public NativeArray<float> DamageTable;

        /// <summary>100 % at the centre falling to 50 % at the edge.</summary>
        public static float Falloff(float distance, float radius) => 1f - 0.5f * math.saturate(distance / radius);

        public void Execute(int index)
        {
            SplashRequest r = Requests[index];
            {
                float rSq = r.Radius * r.Radius;
                int2 cell = math.clamp((int2)math.floor(r.Centre / CellSize), 0, new int2(CellsX - 1, CellsY - 1));

                int reach = (int)math.ceil(r.Radius / CellSize) + 1;
                for (int cy = math.max(cell.y - reach, 0); cy <= math.min(cell.y + reach, CellsY - 1); cy++)
                for (int cx = math.max(cell.x - reach, 0); cx <= math.min(cell.x + reach, CellsX - 1); cx++)
                {
                    int c = cy * CellsX + cx;
                    for (int k = CellStart[c]; k < CellStart[c + 1]; k++)
                    {
                        int u = Sorted[k];
                        if (UnitHealth[u] <= 0f || (r.PrimaryKind == TargetKinds.Unit && r.PrimarySlot == u) || !SimData.Attacks(AttackMask, r.AttackerSlot, OwnerSlot[u])) continue;
                        float dSq = math.distancesq(Positions[u], r.Centre);
                        if (dSq > rSq) continue;
                        float amount = r.Damage * Falloff(math.sqrt(dSq), r.Radius) * WAR2D.Rules.DamageTable.Get(DamageTable, r.AttackerType, (TargetClass)ArmourByType[Type[u]]);
                        Hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = u, Amount = amount, AttackerOwner = r.AttackerOwner });
                    }
                }

                // Buildings are hashed by centre; a footprint reaches up to half its size (at most a few tiles) further.
                int buildingReach = (int)math.ceil((r.Radius + 4f) / CellSize);
                for (int cy = math.max(cell.y - buildingReach, 0); cy <= math.min(cell.y + buildingReach, CellsY - 1); cy++)
                for (int cx = math.max(cell.x - buildingReach, 0); cx <= math.min(cell.x + buildingReach, CellsX - 1); cx++)
                {
                    int c = cy * CellsX + cx;
                    for (int k = BuildingCellStart[c]; k < BuildingCellStart[c + 1]; k++)
                    {
                        int b = BuildingSorted[k];
                        if (BuildingHealth[b] <= 0f || (BuildingFlag[b] & BuildingFlags.Hidden) != 0 || (r.PrimaryKind == TargetKinds.Building && r.PrimarySlot == b) ||
                            !SimData.Attacks(AttackMask, r.AttackerSlot, BuildingOwnerSlot[b])) continue;
                        float2 lo = Footprint.Start(BuildingAnchor[b], BuildingSize[b]), hi = lo + BuildingSize[b];
                        float2 gap = math.max(math.max(lo - r.Centre, r.Centre - hi), 0f);
                        float gapSq = math.lengthsq(gap);
                        if (gapSq > rSq) continue;
                        float amount = r.Damage * Falloff(math.sqrt(gapSq), r.Radius) * WAR2D.Rules.DamageTable.Get(DamageTable, r.AttackerType, TargetClass.Building);
                        Hits.Enqueue(new DamageHit { Kind = TargetKinds.Building, Slot = b, Amount = amount, AttackerOwner = r.AttackerOwner });
                    }
                }

                int2 tile = (int2)math.floor(r.Centre);
                int reachTiles = (int)math.ceil(r.Radius);
                for (int y = math.max(tile.y - reachTiles, 0); y <= math.min(tile.y + reachTiles, Height - 1); y++)
                for (int x = math.max(tile.x - reachTiles, 0); x <= math.min(tile.x + reachTiles, Width - 1); x++)
                {
                    int w = y * Width + x;
                    if (WallState[w] == WallStates.None || (r.PrimaryKind == TargetKinds.Wall && r.PrimarySlot == w) || !SimData.Attacks(AttackMask, r.AttackerSlot, WallOwnerSlot[w])) continue;
                    float dSq = math.distancesq(new float2(x + 0.5f, y + 0.5f), r.Centre);
                    if (dSq > rSq) continue;
                    float amount = r.Damage * Falloff(math.sqrt(dSq), r.Radius) * WAR2D.Rules.DamageTable.Get(DamageTable, r.AttackerType, TargetClass.Wall);
                    Hits.Enqueue(new DamageHit { Kind = TargetKinds.Wall, Slot = w, Amount = amount, AttackerOwner = r.AttackerOwner });
                }
            }
        }
    }

    /// <summary>
    /// Suicide bombers: one that has reached its target (within its range of the target's edge) or was
    /// ordered to detonate blows up. The blast hurts every unit, building and wall tile in its radius,
    /// friends included, with the damage table's multipliers for the bomber; the bomber itself dies. A
    /// bomber that was killed first never gets here (it has no health), so it does not explode.
    /// </summary>
    [BurstCompile]
    public struct BomberJob : IJob
    {
        public NativeQueue<DamageHit> Hits;
        public NativeQueue<BombBlast> Blasts;
        [ReadOnly] public NativeParallelHashSet<int> Forced;
        public int UnitCount, BuildingCount, Width, Height, CellSize, CellsX, CellsY;
        [ReadOnly] public NativeArray<float2> Positions, BuildingPositions;
        [ReadOnly] public NativeArray<float> UnitHealth, Radius, BuildingHealth, RangeSqByType, BlastRadiusByType, BlastDamageByType, DamageTable;
        [ReadOnly] public NativeArray<byte> OwnerSlot, Type, TargetKind, WallState, ArmourByType;
        [ReadOnly] public NativeArray<int> OwnerId, IdOf, Target, CellStart, Sorted;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;

        public void Execute()
        {
            for (int i = 0; i < UnitCount; i++)
            {
                int type = Type[i];
                if (UnitHealth[i] <= 0f || type >= BlastRadiusByType.Length || BlastRadiusByType[type] <= 0f) continue;
                if (!Forced.Contains(IdOf[i]) && !InContact(i, type)) continue;
                Detonate(i, type);
            }
        }

        private bool InContact(int i, int type)
        {
            int target = Target[i];
            if (target < 0) return false;
            float reach = math.sqrt(RangeSqByType[type]) + Radius[i];
            float2 p = Positions[i];
            switch (TargetKind[i])
            {
                case TargetKinds.Unit:
                    return math.distance(p, Positions[target]) <= reach + Radius[target];
                case TargetKinds.Wall:
                {
                    float2 lo = new float2(target % Width, target / Width);
                    return math.lengthsq(math.max(math.max(lo - p, p - (lo + 1f)), 0f)) <= reach * reach;
                }
                default:
                {
                    float2 lo = Footprint.Start(BuildingAnchor[target], BuildingSize[target]), hi = lo + BuildingSize[target];
                    return math.lengthsq(math.max(math.max(lo - p, p - hi), 0f)) <= reach * reach;
                }
            }
        }

        private void Detonate(int bomber, int type)
        {
            float2 centre = Positions[bomber];
            float radius = BlastRadiusByType[type], rSq = radius * radius, damage = BlastDamageByType[type];
            int owner = OwnerId[bomber];

            int reach = (int)math.ceil(radius / CellSize) + 1;
            int2 cell = math.clamp((int2)math.floor(centre / CellSize), 0, new int2(CellsX - 1, CellsY - 1));
            for (int cy = math.max(cell.y - reach, 0); cy <= math.min(cell.y + reach, CellsY - 1); cy++)
            for (int cx = math.max(cell.x - reach, 0); cx <= math.min(cell.x + reach, CellsX - 1); cx++)
            {
                int c = cy * CellsX + cx;
                for (int k = CellStart[c]; k < CellStart[c + 1]; k++)
                {
                    int u = Sorted[k];
                    if (u == bomber || UnitHealth[u] <= 0f || math.distancesq(Positions[u], centre) > rSq) continue;
                    float amount = damage * WAR2D.Rules.DamageTable.Get(DamageTable, type, (TargetClass)ArmourByType[Type[u]]);
                    Hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = u, Amount = amount, AttackerOwner = owner });
                }
            }

            float buildingAmount = damage * WAR2D.Rules.DamageTable.Get(DamageTable, type, TargetClass.Building);
            for (int b = 0; b < BuildingCount; b++)
            {
                if (BuildingHealth[b] <= 0f) continue;
                float2 lo = Footprint.Start(BuildingAnchor[b], BuildingSize[b]), hi = lo + BuildingSize[b];
                if (math.lengthsq(math.max(math.max(lo - centre, centre - hi), 0f)) > rSq) continue;
                Hits.Enqueue(new DamageHit { Kind = TargetKinds.Building, Slot = b, Amount = buildingAmount, AttackerOwner = owner });
            }

            float wallAmount = damage * WAR2D.Rules.DamageTable.Get(DamageTable, type, TargetClass.Wall);
            int2 tile = (int2)math.floor(centre);
            int r = (int)math.ceil(radius);
            for (int y = math.max(tile.y - r, 0); y <= math.min(tile.y + r, Height - 1); y++)
            for (int x = math.max(tile.x - r, 0); x <= math.min(tile.x + r, Width - 1); x++)
            {
                int w = y * Width + x;
                if (WallState[w] == WallStates.None || math.distancesq(new float2(x + 0.5f, y + 0.5f), centre) > rSq) continue;
                Hits.Enqueue(new DamageHit { Kind = TargetKinds.Wall, Slot = w, Amount = wallAmount, AttackerOwner = owner });
            }

            // The bomber itself is spent.
            Hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = bomber, Amount = UnitHealth[bomber], AttackerOwner = owner });
            Blasts.Enqueue(new BombBlast { Position = centre, OwnerId = owner, Bomber = 1 });
        }
    }

    /// <summary>The single writer of damage: units lose health (and remember who hit them last) in the SoA, buildings accumulate it.</summary>
    [BurstCompile]
    public struct ApplyDamageJob : IJob
    {
        public NativeQueue<DamageHit> Hits;
        public NativeArray<float> Health;
        public NativeArray<int> LastHitBy;
        public NativeArray<float> BuildingHealth;
        public NativeArray<float> BuildingDamage;
        public NativeQueue<TileWork> WallDamage;

        public void Execute()
        {
            while (Hits.TryDequeue(out DamageHit hit))
            {
                if (hit.Kind == TargetKinds.Wall)
                {
                    WallDamage.Enqueue(new TileWork { Tile = hit.Slot, Amount = hit.Amount, OwnerId = hit.AttackerOwner });
                }
                else if (hit.Kind == TargetKinds.Unit)
                {
                    Health[hit.Slot] = math.max(0f, Health[hit.Slot] - hit.Amount);
                    LastHitBy[hit.Slot] = hit.AttackerOwner;
                }
                else
                {
                    BuildingHealth[hit.Slot] = math.max(0f, BuildingHealth[hit.Slot] - hit.Amount);
                    BuildingDamage[hit.Slot] += hit.Amount;
                }
            }
        }
    }

    /// <summary>Writes health, cooldown and the target (as a stable id) back to each unit.</summary>
    [BurstCompile]
    public partial struct WriteBackCombatJob : IJobEntity
    {
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> Cooldown;
        [ReadOnly] public NativeArray<int> LastHitBy;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<byte> TargetKind;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> BuildingIds;
        public int Count;

        private void Execute(ref Unit unit, [EntityIndexInQuery] int index)
        {
            if (index >= Count) return;
            unit.Health = Health[index];
            unit.Cooldown = Cooldown[index];
            unit.LastHitBy = LastHitBy[index];
            int target = Target[index];
            byte kind = TargetKind[index];
            unit.TargetKind = kind;
            unit.TargetId = target < 0 ? -1 : kind == TargetKinds.Unit ? IdOf[target] : kind == TargetKinds.Wall ? target : BuildingIds[target];
        }
    }
}

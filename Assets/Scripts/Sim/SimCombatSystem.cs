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

            SearchInput search = SearchInput.From(data, count, buildings);
            state.Dependency = new ResolveTargetsJob
            {
                Search = search,
                Type = data.Type,
                Stance = data.Stance,
                OrderSlot = data.OrderSlot,
                OrderLive = data.OrderLive,
                RangeSqByType = data.SearchSqByType,
                MinRangeSqByType = data.MinRangeSqByType,
                DamageByType = data.DamageByType,
                Target = data.Target,
                TargetKind = data.TargetKind,
            }.Schedule(count, 256, state.Dependency);

            state.Dependency = new NearestEnemyJob
            {
                Search = search,
                Type = data.Type,
                Stance = data.Stance,
                OrderSlot = data.OrderSlot,
                OrderLive = data.OrderLive,
                RangeSqByType = data.SearchSqByType,
                MinRangeSqByType = data.MinRangeSqByType,
                DamageByType = data.DamageByType,
                Tick = clock.Tick % data.Slice,
                Slice = data.Slice,
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

            // Attacker buildings (v0.13: Gun turrets), with the units' rules (TargetSearch); their hits go
            // through the same queue, so ApplyDamageJob stays the single writer.
            NativeArray<int> attackers = data.AttackerBuildings.AsArray();
            if (attackers.Length > 0)
            {
                var turret = new BuildingWeaponInput
                {
                    Attackers = attackers,
                    BuildingAnchor = data.BuildingAnchor,
                    BuildingSize = data.BuildingSize,
                    BuildingKind = data.BuildingKind,
                    RangeSqByType = data.BuildingRangeSqByType,
                };
                state.Dependency = new ResolveBuildingTargetsJob
                {
                    Search = search,
                    Weapon = turret,
                    Target = data.BuildingTarget,
                    TargetKind = data.BuildingTargetKind,
                }.Schedule(attackers.Length, 32, state.Dependency);
                state.Dependency = new BuildingTargetJob
                {
                    Search = search,
                    Weapon = turret,
                    Tick = clock.Tick % data.Slice,
                    Slice = data.Slice,
                    Target = data.BuildingTarget,
                    TargetKind = data.BuildingTargetKind,
                }.Schedule(attackers.Length, 32, state.Dependency);
                state.Dependency = new BuildingAttackJob
                {
                    Weapon = turret,
                    BuildingOwnerId = data.BuildingOwnerId,
                    BuildingIds = data.BuildingIds,
                    IdOf = data.IdOf,
                    Type = data.Type,
                    ArmourByType = data.ArmourByType,
                    DamageByType = data.BuildingDamageByType,
                    IntervalByType = data.BuildingIntervalByType,
                    DamageTable = data.DamageTable,
                    Dt = data.Dt,
                    Target = data.BuildingTarget,
                    TargetKind = data.BuildingTargetKind,
                    Cooldown = data.BuildingCooldown,
                    Hits = hits,
                    Events = data.BuildingAttackEvents,
                }.Schedule(state.Dependency);
            }

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
    /// changed hands, are no longer attacked (diplomacy), went into the fog of the attacker's side or no longer exist
    /// (<see cref="TargetSearch.Keep"/>), and the targets of units on a live Move order.
    /// </summary>
    [BurstCompile]
    public struct ResolveTargetsJob : IJobParallelFor
    {
        [ReadOnly] public SearchInput Search;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<byte> Stance;
        [ReadOnly] public NativeArray<int> OrderSlot;
        [ReadOnly] public NativeArray<byte> OrderLive;
        [ReadOnly] public NativeArray<float> RangeSqByType;
        [ReadOnly] public NativeArray<float> MinRangeSqByType;
        [ReadOnly] public NativeArray<float> DamageByType;
        public NativeArray<int> Target;
        public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            int id = Target[i];
            Target[i] = -1;
            if (Search.Health[i] <= 0f || id <= 0 || TargetKinds.IgnoresEnemies(Stance[i], OrderSlot[i], OrderLive) || DamageByType[Type[i]] <= 0f) return;
            int type = Type[i];
            float rangeSq = type < RangeSqByType.Length ? RangeSqByType[type] : 0f;
            float minSq = type < MinRangeSqByType.Length ? MinRangeSqByType[type] : 0f;
            Target[i] = TargetSearch.Keep(Search, Search.Positions[i], Search.OwnerSlot[i], rangeSq, minSq, id, TargetKind[i]);
        }
    }

    /// <summary>What the attacker-building jobs read about each turret (v0.13).</summary>
    public struct BuildingWeaponInput
    {
        /// <summary>The slots of the complete, living attacker buildings (<see cref="SimData.AttackerBuildings"/>).</summary>
        [ReadOnly] public NativeArray<int> Attackers;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;
        [ReadOnly] public NativeArray<byte> BuildingKind;
        [ReadOnly] public NativeArray<float> RangeSqByType;

        /// <summary>Where a building fires from: its footprint's centre.</summary>
        public float2 Centre(int b) => Footprint.VisualCenter(BuildingAnchor[b], BuildingSize[b]);

        /// <summary>The building's squared range (0 for a type without a weapon).</summary>
        public float RangeSq(int b) => BuildingKind[b] < RangeSqByType.Length ? RangeSqByType[BuildingKind[b]] : 0f;

        /// <summary>
        /// True when the building may hold or look for a target: alive, complete and paid, with an owner slot (every
        /// building gets one at creation). Blueprints never reach the attacker list; upkeep can stop a finished one.
        /// </summary>
        public static bool Armed(in SearchInput s, int b) =>
            s.BuildingHealth[b] > 0f && (s.BuildingFlag[b] & (BuildingFlags.UnderConstruction | BuildingFlags.Unpaid)) == 0 && s.BuildingOwnerSlot[b] < SimData.MaxOwners;
    }

    /// <summary>
    /// Turns each attacker building's stored target id into this tick's slot, with the units' checks
    /// (<see cref="TargetSearch.Keep"/>): it drops a target that died, left range, lost the line of fire, went into
    /// the fog of the turret owner's grid, changed hands or is no longer attacked. An unpaid turret drops its target.
    /// </summary>
    [BurstCompile]
    public struct ResolveBuildingTargetsJob : IJobParallelFor
    {
        [ReadOnly] public SearchInput Search;
        [ReadOnly] public BuildingWeaponInput Weapon;
        // Each index writes its own building slot (the attacker list holds each slot once).
        [NativeDisableParallelForRestriction] public NativeArray<int> Target;
        [NativeDisableParallelForRestriction] public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            int b = Weapon.Attackers[i];
            int id = Target[b];
            Target[b] = -1;
            if (id <= 0 || !BuildingWeaponInput.Armed(Search, b)) return;
            Target[b] = TargetSearch.Keep(Search, Weapon.Centre(b), Search.BuildingOwnerSlot[b], Weapon.RangeSq(b), 0f, id, TargetKind[b]);
        }
    }

    /// <summary>
    /// The attacker buildings' target search, sliced over ticks like the units' (<c>(slot + Tick) % Slice</c>): the
    /// nearest enemy unit, else building, else wall tile in range (spec §2.1: walls always, like a unit on Attack-move).
    /// </summary>
    [BurstCompile]
    public struct BuildingTargetJob : IJobParallelFor
    {
        [ReadOnly] public SearchInput Search;
        [ReadOnly] public BuildingWeaponInput Weapon;
        public int Tick, Slice;
        [NativeDisableParallelForRestriction] public NativeArray<int> Target;
        [NativeDisableParallelForRestriction] public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            int b = Weapon.Attackers[i];
            if (Slice > 1 && (b + Tick) % Slice != 0) return;
            if (!BuildingWeaponInput.Armed(Search, b)) { Target[b] = -1; return; }
            TargetSearch.Nearest(Search, Weapon.Centre(b), Search.BuildingOwnerSlot[b], Weapon.RangeSq(b), 0f, true, out int target, out byte kind);
            Target[b] = target;
            TargetKind[b] = kind;
        }
    }

    /// <summary>
    /// Fires each attacker building at its resolved target when its cooldown allows: a <see cref="DamageHit"/> of the
    /// type's damage times the damage table's multiplier (<see cref="WAR2D.Rules.DamageTable.BuildingAttacker(int)"/>
    /// row) for the target's class, into the shared queue, and a building attack event (not for walls, as for units).
    /// Then it turns the target back into a stable id for the boundary's write-back to <see cref="BuildingWeapon"/>.
    /// </summary>
    [BurstCompile]
    public struct BuildingAttackJob : IJob
    {
        [ReadOnly] public BuildingWeaponInput Weapon;
        [ReadOnly] public NativeArray<int> BuildingOwnerId, BuildingIds, IdOf;
        [ReadOnly] public NativeArray<byte> Type, ArmourByType;
        [ReadOnly] public NativeArray<float> DamageByType, IntervalByType, DamageTable;
        public float Dt;
        public NativeArray<int> Target;
        public NativeArray<byte> TargetKind;
        public NativeArray<float> Cooldown;
        public NativeQueue<DamageHit> Hits;
        public NativeList<int2> Events;

        public void Execute()
        {
            for (int i = 0; i < Weapon.Attackers.Length; i++)
            {
                int b = Weapon.Attackers[i];
                int type = Weapon.BuildingKind[b];
                float cooldown = math.max(0f, Cooldown[b] - Dt);
                int target = Target[b];
                byte kind = TargetKind[b];
                if (target >= 0 && cooldown <= 0f && type < DamageByType.Length && DamageByType[type] > 0f)
                {
                    TargetClass targetClass = kind == TargetKinds.Unit ? (TargetClass)ArmourByType[Type[target]] : kind == TargetKinds.Wall ? TargetClass.Wall : TargetClass.Building;
                    float multiplier = WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.BuildingAttacker(type), targetClass);
                    Hits.Enqueue(new DamageHit { Kind = kind, Slot = target, Amount = DamageByType[type] * multiplier, AttackerOwner = BuildingOwnerId[b] });
                    if (kind != TargetKinds.Wall) // a wall's damage travels in the terrain delta, as for units
                        Events.Add(new int2(BuildingIds[b], kind == TargetKinds.Unit ? IdOf[target] : BuildingIds[target]));
                    cooldown = IntervalByType[type];
                }
                Cooldown[b] = cooldown;
                Target[b] = target < 0 ? -1 : kind == TargetKinds.Unit ? IdOf[target] : kind == TargetKinds.Wall ? target : BuildingIds[target];
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
            float multiplier = WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.UnitAttacker(type), targetClass);
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
                        float amount = r.Damage * Falloff(math.sqrt(dSq), r.Radius) * WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.UnitAttacker(r.AttackerType), (TargetClass)ArmourByType[Type[u]]);
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
                        float amount = r.Damage * Falloff(math.sqrt(gapSq), r.Radius) * WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.UnitAttacker(r.AttackerType), TargetClass.Building);
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
                    float amount = r.Damage * Falloff(math.sqrt(dSq), r.Radius) * WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.UnitAttacker(r.AttackerType), TargetClass.Wall);
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
                    float amount = damage * WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.UnitAttacker(type), (TargetClass)ArmourByType[Type[u]]);
                    Hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = u, Amount = amount, AttackerOwner = owner });
                }
            }

            float buildingAmount = damage * WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.UnitAttacker(type), TargetClass.Building);
            for (int b = 0; b < BuildingCount; b++)
            {
                if (BuildingHealth[b] <= 0f) continue;
                float2 lo = Footprint.Start(BuildingAnchor[b], BuildingSize[b]), hi = lo + BuildingSize[b];
                if (math.lengthsq(math.max(math.max(lo - centre, centre - hi), 0f)) > rSq) continue;
                Hits.Enqueue(new DamageHit { Kind = TargetKinds.Building, Slot = b, Amount = buildingAmount, AttackerOwner = owner });
            }

            float wallAmount = damage * WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.UnitAttacker(type), TargetClass.Wall);
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

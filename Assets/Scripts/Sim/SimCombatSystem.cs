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
        /// <summary>Where a shot came from, which raises its victim's owner's alarm; (0, 0) for a blast, which doesn't.</summary>
        public float2 From;
        /// <summary>v0.20: <see cref="HitKinds"/> (the death's cause), and 1 when the hit sets its target burning (a Flamethrower's).</summary>
        public byte HitKind;
        public byte Ignite;
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
        private NativeList<ShellLaunch> launches;
        private EntityQuery units;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimData>();
            hits = new NativeQueue<DamageHit>(Allocator.Persistent);
            splashes = new NativeList<SplashRequest>(1024, Allocator.Persistent);
            launches = new NativeList<ShellLaunch>(64, Allocator.Persistent);
            units = state.GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        public void OnDestroy(ref SystemState state)
        {
            state.Dependency.Complete();
            hits.Dispose();
            splashes.Dispose();
            launches.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            int count = clock.UnitCount;
            int buildings = math.min(data.BuildingCount[0], data.BuildingCapacity);
            // The parallel writers can't grow these lists: room for one event per unit beyond what they hold (the boundary empties them).
            if (data.AttackEvents.Capacity < data.AttackEvents.Length + count) data.AttackEvents.Capacity = data.AttackEvents.Length + count;
            if (data.AimEvents.Capacity < data.AimEvents.Length + count) data.AimEvents.Capacity = data.AimEvents.Length + count;

            // v0.20: who is in the air this tick, by slot, before anything targets or hurts them.
            state.Dependency = new AirborneJob { IdOf = data.IdOf, Combat = data.Combat, Type = data.Type, BlastRadiusByType = data.BlastRadiusByType, Tick = clock.Tick, Airborne = data.Airborne }.Schedule(count, 512, state.Dependency);

            SearchInput search = SearchInput.From(data, count, buildings);
            state.Dependency = new ResolveTargetsJob
            {
                OrderTarget = data.OrderTarget,
                OrderTargetKind = data.OrderTargetKind,
                Search = search,
                Type = data.Type,
                Stance = data.Stance,
                OrderSlot = data.OrderSlot,
                OrderLive = data.OrderLive,
                RangeSqByType = data.SearchSqByType,
                MinRangeSqByType = data.MinRangeSqByType,
                DamageByType = data.DamageByType,
                ShellRadiusByType = data.ShellRadiusByType,
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
                ShellRadiusByType = data.ShellRadiusByType,
                Tick = clock.Tick % data.Slice,
                Slice = data.Slice,
                Target = data.Target,
                TargetKind = data.TargetKind,
            }.Schedule(count, 256, state.Dependency);

            // Every unit may queue one splash and one shell this tick; the parallel writers can't grow the lists.
            splashes.Clear();
            if (splashes.Capacity < count) splashes.Capacity = count;
            launches.Clear();
            if (launches.Capacity < count) launches.Capacity = count;
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
                Velocity = data.Velocity,
                BuildingPositions = data.BuildingPositions,
                Width = data.Width,
                DamageByType = data.DamageByType,
                CooldownByType = data.CooldownByType,
                ArmourByType = data.ArmourByType,
                SplashByType = data.SplashByType,
                BlastRadiusByType = data.BlastRadiusByType,
                ShellRadiusByType = data.ShellRadiusByType,
                FlatArmourByType = data.FlatArmourByType,
                IgnitesByType = data.IgnitesByType,
                AimByType = data.AimByType,
                RangeSqByType = data.RangeSqByType,
                MinHit = data.MinHit,
                ShellSpeed = data.ShellSpeed,
                LeadAim = data.LeadAim,
                LeadCapTiles = data.LeadCapTiles,
                StillSpeed = data.StillSpeed,
                DamageTable = data.DamageTable,
                Combat = data.Combat,
                Hits = hits.AsParallelWriter(),
                Splashes = splashes.AsParallelWriter(),
                Launches = launches.AsParallelWriter(),
                Events = data.AttackEvents.AsParallelWriter(),
                AimEvents = data.AimEvents.AsParallelWriter(),
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
                Positions = data.Positions, UnitHealth = data.Health, OwnerSlot = data.OwnerSlot, Type = data.Type, Airborne = data.Airborne,
                CellStart = data.CellStart, Sorted = data.Sorted, AttackMask = data.AttackMask,
                BuildingPositions = data.BuildingPositions, BuildingAnchor = data.BuildingAnchor, BuildingSize = data.BuildingSize,
                BuildingFlag = data.BuildingFlag, BuildingHealth = data.BuildingHealth, BuildingOwnerSlot = data.BuildingOwnerSlot,
                BuildingCellStart = data.BuildingCellStart, BuildingSorted = data.BuildingSorted,
                WallState = data.WallState, WallOwnerSlot = data.WallOwnerSlot,
                ArmourByType = data.ArmourByType, DamageTable = data.DamageTable, FlatArmourByType = data.FlatArmourByType, MinHit = data.MinHit,
            }.Schedule(splashes, 8, state.Dependency);

            var blastWorld = new BlastWorld
            {
                UnitCount = count,
                BuildingCount = buildings,
                Width = data.Width, Height = data.Height,
                CellSize = data.CellSize, CellsX = data.CellsX, CellsY = data.CellsY,
                OwnerId = data.OwnerId, Type = data.Type,
                CellStart = data.CellStart, Sorted = data.Sorted,
                BuildingAnchor = data.BuildingAnchor, BuildingSize = data.BuildingSize,
                WallState = data.WallState,
                AttackMask = data.AttackMask, OwnerSlot = data.OwnerSlot, BuildingOwnerSlot = data.BuildingOwnerSlot, WallOwnerSlot = data.WallOwnerSlot,
                FriendlyDamage = data.BomberFriendlyDamage,
                BlastRadiusByType = data.BlastRadiusByType, BlastDamageByType = data.BlastDamageByType,
                ArmourByType = data.ArmourByType, DamageTable = data.DamageTable,
                BlastResistByType = data.BlastResistByType, Airborne = data.Airborne,
            };
            state.Dependency = new BomberJob
            {
                Search = search,
                Hits = hits,
                Blasts = data.BombBlasts,
                Spent = data.BomberSpent,
                Forced = data.ForcedBombers,
                World = blastWorld,
                Combat = data.Combat,
                Leaps = data.Leaps,
                Positions = data.Positions, UnitHealth = data.Health, BuildingHealth = data.BuildingHealth, Radius = data.Radius,
                IdOf = data.IdOf, Target = data.Target, TargetKind = data.TargetKind,
                RangeSqByType = data.RangeSqByType,
                Tiles = data.Tiles, Used = data.Used,
                LeapReach = data.LeapReach, ArmyTiles = data.ArmyTiles, ContactTiles = data.ContactTiles, LeapTicks = data.LeapTicks,
                Tick = clock.Tick,
            }.Schedule(state.Dependency);

            state.Dependency = new ShellJob
            {
                Launches = launches.AsDeferredJobArray(),
                Shells = data.Shells,
                Launched = data.ShellsLaunched,
                Serial = data.ShellSerial,
                World = blastWorld,
                DamageByType = data.DamageByType,
                ShellRadiusByType = data.ShellRadiusByType,
                FriendlyDamage = data.ShellFriendlyDamage,
                Positions = data.Positions, UnitHealth = data.Health, BuildingHealth = data.BuildingHealth,
                Hits = hits,
                Blasts = data.BombBlasts,
                ShellSpeed = data.ShellSpeed,
                Dt = data.Dt,
                Tick = clock.Tick,
            }.Schedule(state.Dependency);

            state.Dependency = new BurnJob
            {
                Count = count,
                BuildingCount = buildings,
                IdOf = data.IdOf, BuildingIds = data.BuildingIds, IndexOfId = data.IndexOfId, BuildingSlotOfIndex = data.BuildingSlotOfIndex,
                Health = data.Health, BuildingHealth = data.BuildingHealth,
                Type = data.Type, ArmourByType = data.ArmourByType, Airborne = data.Airborne,
                Combat = data.Combat, BuildingFires = data.BuildingFires,
                BurningUnits = data.BurningUnits, BurningBuildings = data.BurningBuildings,
                Dps = data.BurnDps, Light = data.BurnLight, Heavy = data.BurnHeavy, Building = data.BurnBuilding,
                Dt = data.Dt,
                Hits = hits,
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
                    Type = data.Type,
                    BlastRadiusByType = data.BlastRadiusByType,
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
                    FlatArmourByType = data.FlatArmourByType,
                    MinHit = data.MinHit,
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

            var apply = new ApplyDamageJob
            {
                Hits = hits,
                Health = data.Health,
                LastHitBy = data.LastHitBy,
                BuildingHealth = data.BuildingHealth,
                BuildingDamage = data.BuildingDamage,
                WallDamage = data.WallDamage,
                HqLastHitBy = data.HqLastHitBy,
                BuildingKind = data.BuildingKind,
                BuildingOwnerSlot = data.BuildingOwnerSlot,
                OwnerSlot = data.OwnerSlot,
                Positions = data.Positions,
                BuildingPositions = data.BuildingPositions,
                Alarms = data.Alarms,
                AlarmCount = data.AlarmCount,
                AlarmCellTiles = data.DefendAlarmCellTiles,
                IdOf = data.IdOf,
                BuildingIds = data.BuildingIds,
                Airborne = data.Airborne,
                Combat = data.Combat,
                BuildingFires = data.BuildingFires,
                LastHitKind = data.LastHitKind,
                BurningUnits = data.BurningUnits, BurningBuildings = data.BurningBuildings,
                BurnSeconds = data.BurnSeconds,
            };
            state.Dependency = apply.Schedule(state.Dependency);

            state.Dependency = new LastGaspJob
            {
                Apply = apply,
                Leaps = data.Leaps,
                Blasts = data.BombBlasts,
                Spent = data.BomberSpent,
                World = blastWorld,
                Positions = data.Positions,
                MaxHealth = data.MaxHealth,
                Threshold = data.LastGaspHealth,
                Passes = data.ChainPasses,
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

    /// <summary>v0.20: fills <see cref="SimData.Airborne"/> for this tick's slots from each unit's leap.</summary>
    [BurstCompile]
    public struct AirborneJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<UnitCombat> Combat;
        /// <summary>Only Bombers leap: no other unit's combat record is read.</summary>
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<float> BlastRadiusByType;
        public int Tick;
        public NativeArray<byte> Airborne;

        public void Execute(int i)
        {
            int type = Type[i];
            if (type >= BlastRadiusByType.Length || BlastRadiusByType[type] <= 0f) { Airborne[i] = 0; return; }
            int id = IdOf[i], index = NetIdAllocator.IndexOf(id);
            Airborne[i] = (byte)((uint)index < (uint)Combat.Length && Combat[index].AirborneAt(id, Tick) ? 1 : 0);
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
        [ReadOnly] public NativeArray<int> OrderTarget;
        [ReadOnly] public NativeArray<byte> OrderTargetKind;
        /// <summary>v0.20: per type, a shell blast radius (above 0: it fires over walls).</summary>
        [ReadOnly] public NativeArray<float> ShellRadiusByType;
        public NativeArray<int> Target;
        public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            int id = Target[i];
            Target[i] = -1;
            if (Search.Airborne[i] != 0) return; // a Bomber in the air holds no target: it lands where it chose
            if (Stance[i] == Stances.Attack)
            {
                // An Attack order fires at its target when it can, and at nothing else.
                id = OrderTarget[i];
                TargetKind[i] = OrderTargetKind[i];
                if (Search.Health[i] <= 0f || id <= 0 || DamageByType[Type[i]] <= 0f) return;
            }
            if (Search.Health[i] <= 0f || id <= 0 || TargetKinds.IgnoresEnemies(Stance[i], OrderSlot[i], OrderLive) || DamageByType[Type[i]] <= 0f) return;
            int type = Type[i];
            float rangeSq = type < RangeSqByType.Length ? RangeSqByType[type] : 0f;
            float minSq = type < MinRangeSqByType.Length ? MinRangeSqByType[type] : 0f;
            bool overWalls = type < ShellRadiusByType.Length && ShellRadiusByType[type] > 0f;
            Target[i] = TargetSearch.Keep(Search, Search.Positions[i], Search.OwnerSlot[i], rangeSq, minSq, id, TargetKind[i], overWalls);
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
    /// The attacker buildings' target search, sliced over ticks like the units' on the building's stable id index
    /// (<c>(IndexOf(id) + Tick) % Slice</c>, not its slot, which moves when an earlier slot is freed): the
    /// nearest enemy unit, else building, else wall tile in range (spec §2.1: walls always, like a unit on Attack-move).
    /// </summary>
    [BurstCompile]
    public struct BuildingTargetJob : IJobParallelFor
    {
        [ReadOnly] public SearchInput Search;
        [ReadOnly] public BuildingWeaponInput Weapon;
        /// <summary>v0.20: unit types and the Bombers among them (a blast radius), which a turret shoots first.</summary>
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<float> BlastRadiusByType;
        public int Tick, Slice;
        [NativeDisableParallelForRestriction] public NativeArray<int> Target;
        [NativeDisableParallelForRestriction] public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            int b = Weapon.Attackers[i];
            if (Slice > 1 && (NetIdAllocator.IndexOf(Search.BuildingIds[b]) + Tick) % Slice != 0) return;
            if (!BuildingWeaponInput.Armed(Search, b)) { Target[b] = -1; return; }
            // v0.20 (spec §6.3): a Bomber on the ground in range first, then the nearest unit, building, wall.
            int bomber = TargetSearch.NearestBomber(Search, Weapon.Centre(b), Search.BuildingOwnerSlot[b], Weapon.RangeSq(b), Type, BlastRadiusByType);
            if (bomber >= 0)
            {
                Target[b] = bomber;
                TargetKind[b] = TargetKinds.Unit;
                return;
            }
            TargetSearch.Nearest(Search, Weapon.Centre(b), Search.BuildingOwnerSlot[b], Weapon.RangeSq(b), 0f, true, false, out int target, out byte kind);
            Target[b] = target;
            TargetKind[b] = kind;
        }
    }

    /// <summary>
    /// Fires each attacker building at its resolved target when its cooldown allows: a <see cref="DamageHit"/> of the
    /// type's damage times the damage table's multiplier (<see cref="WAR2D.Rules.DamageTable.BuildingAttacker(int)"/>
    /// row) for the target's class, into the shared queue, and a building attack event: for a wall shot it names
    /// target 0 (a wall tile is no entity; its damage travels in the terrain delta), so clients still see the shot.
    /// Then it turns the target back into a stable id for the boundary's write-back to <see cref="BuildingWeapon"/>.
    /// </summary>
    [BurstCompile]
    public struct BuildingAttackJob : IJob
    {
        [ReadOnly] public BuildingWeaponInput Weapon;
        [ReadOnly] public NativeArray<int> BuildingOwnerId, BuildingIds, IdOf;
        [ReadOnly] public NativeArray<byte> Type, ArmourByType;
        [ReadOnly] public NativeArray<float> DamageByType, IntervalByType, DamageTable, FlatArmourByType;
        public float Dt, MinHit;
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
                    float amount = DamageByType[type] * multiplier;
                    if (kind == TargetKinds.Unit) amount = CombatRules.DirectHit(amount, FlatArmourByType[Type[target]], MinHit);
                    Hits.Enqueue(new DamageHit { Kind = kind, Slot = target, Amount = amount, AttackerOwner = BuildingOwnerId[b], From = Weapon.Centre(b) });
                    Events.Add(new int2(BuildingIds[b], kind == TargetKinds.Unit ? IdOf[target] : kind == TargetKinds.Wall ? 0 : BuildingIds[target]));
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
        /// <summary>Where the attacker stands.</summary>
        public float2 From;
        /// <summary>v0.20: 1 when every hit of the splash sets its target burning.</summary>
        public byte Ignite;
    }

    /// <summary>
    /// Attacks the resolved target when the cooldown allows; queues the hit (times the damage table's multiplier for the target's
    /// armour class, building or wall, less a unit's flat armour), a splash request for units that have one, and a tracer event. A
    /// Bomber never attacks: <see cref="BomberJob"/> or <see cref="LastGaspJob"/> detonates it. v0.20: a Flamethrower's hits set
    /// their targets burning; a unit that fires shells launches one at where its target will be (<see cref="ShellJob"/>) instead of
    /// hitting; a Sniper must stand still and aim for its aim time before each shot.
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
        [ReadOnly] public NativeArray<float2> Positions, Velocity, BuildingPositions;
        public int Width;
        [ReadOnly] public NativeArray<float> DamageByType;
        [ReadOnly] public NativeArray<float> CooldownByType;
        [ReadOnly] public NativeArray<byte> ArmourByType, IgnitesByType;
        [ReadOnly] public NativeArray<float> SplashByType, BlastRadiusByType, ShellRadiusByType, FlatArmourByType, AimByType, RangeSqByType;
        [ReadOnly] public NativeArray<float> DamageTable;
        [ReadOnly] public NativeArray<byte> OwnerSlot;
        public float MinHit, ShellSpeed, LeadCapTiles;
        /// <summary><c>Sniper/StillSpeed</c>: tiles a second under which a Sniper counts as standing still.</summary>
        public float StillSpeed;
        public byte LeadAim;
        /// <summary>Each unit writes only its own id index's record.</summary>
        [NativeDisableParallelForRestriction] public NativeArray<UnitCombat> Combat;
        public NativeQueue<DamageHit>.ParallelWriter Hits;
        public NativeList<SplashRequest>.ParallelWriter Splashes;
        public NativeList<ShellLaunch>.ParallelWriter Launches;
        public NativeList<int2>.ParallelWriter Events, AimEvents;
        public float Dt;

        public void Execute(int i)
        {
            float cooldown = math.max(0f, Cooldown[i] - Dt);
            int target = Target[i];
            int type = Type[i];
            float aimSeconds = type < AimByType.Length ? AimByType[type] : 0f;
            if (Health[i] <= 0f || target < 0 || cooldown > 0f || (type < BlastRadiusByType.Length && BlastRadiusByType[type] > 0f))
            {
                Cooldown[i] = cooldown;
                if (aimSeconds > 0f) StopAim(i);
                return;
            }
            byte kind = TargetKind[i];
            int targetId = kind == TargetKinds.Unit ? IdOf[target] : kind == TargetKinds.Wall ? -(target + 1) : BuildingIds[target];
            if (aimSeconds > 0f && !Aimed(i, targetId, aimSeconds))
            {
                Cooldown[i] = cooldown;
                return;
            }

            TargetClass targetClass = kind == TargetKinds.Unit ? (TargetClass)ArmourByType[Type[target]] : kind == TargetKinds.Wall ? TargetClass.Wall : TargetClass.Building;
            float multiplier = WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.UnitAttacker(type), targetClass);
            float baseDamage = type < DamageByType.Length ? DamageByType[type] : 0f;
            float2 at = kind == TargetKinds.Unit ? Positions[target] : kind == TargetKinds.Wall ? new float2(target % Width + 0.5f, target / Width + 0.5f) : BuildingPositions[target];
            if (type < ShellRadiusByType.Length && ShellRadiusByType[type] > 0f)
            {
                // v0.20: no direct hit; the shell lands where the target will be (spec §4.2).
                float2 from = Positions[i];
                float reach = math.sqrt(RangeSqByType[type]) + LeadCapTiles;
                float2 to = LeadAim != 0 && kind == TargetKinds.Unit ? CombatRules.Lead(from, at, Velocity[target], ShellSpeed, reach) : at;
                Launches.AddNoResize(new ShellLaunch { From = from, To = to, OwnerId = OwnerId[i], OwnerSlot = OwnerSlot[i], Type = (byte)type, ArtilleryId = IdOf[i] });
            }
            else
            {
                byte ignite = (byte)(type < IgnitesByType.Length && IgnitesByType[type] != 0 && kind != TargetKinds.Wall ? 1 : 0);
                float amount = baseDamage * multiplier;
                if (kind == TargetKinds.Unit) amount = CombatRules.DirectHit(amount, FlatArmourByType[Type[target]], MinHit);
                Hits.Enqueue(new DamageHit { Kind = kind, Slot = target, Amount = amount, AttackerOwner = OwnerId[i], From = Positions[i], HitKind = HitKinds.Shot, Ignite = ignite });
                float splash = type < SplashByType.Length ? SplashByType[type] : 0f;
                if (splash > 0f)
                {
                    Splashes.AddNoResize(new SplashRequest
                    {
                        Centre = at, Radius = splash, Damage = baseDamage, AttackerType = (byte)type, AttackerSlot = OwnerSlot[i],
                        AttackerOwner = OwnerId[i], PrimaryKind = kind, PrimarySlot = target, From = Positions[i],
                        Ignite = (byte)(type < IgnitesByType.Length ? IgnitesByType[type] : 0),
                    });
                }
            }
            // A wall is a tile, not an entity: its damage travels in the terrain delta. Its event names the tile as
            // -(tile + 1) for the shooter's owner only (v0.16 target lines; InterestRules.UnitAttack drops it for others).
            Events.AddNoResize(new int2(IdOf[i], targetId));
            Cooldown[i] = type < CooldownByType.Length ? CooldownByType[type] : 1f;
        }

        /// <summary>
        /// v0.20 (spec §5.2): a Sniper aims at <paramref name="targetId"/> while it stands still, and is ready once it has aimed for
        /// <paramref name="seconds"/>. Moving or a new target starts the aim again; the shot ends it. A new aim is an aim event.
        /// </summary>
        private bool Aimed(int i, int targetId, float seconds)
        {
            int id = IdOf[i], index = NetIdAllocator.IndexOf(id);
            if ((uint)index >= (uint)Combat.Length) return true;
            UnitCombat c = UnitCombat.For(Combat[index], id);
            bool still = math.lengthsq(Velocity[i]) <= StillSpeed * StillSpeed;
            if (c.AimTarget != targetId || !still)
            {
                bool fresh = c.AimTarget != targetId;
                c.AimTarget = still ? targetId : 0;
                c.Aim = 0f;
                if (still && fresh) AimEvents.AddNoResize(new int2(id, targetId));
                Combat[index] = c;
                return false;
            }
            c.Aim += Dt;
            bool ready = c.Aim >= seconds - 1e-4f;
            if (ready) { c.Aim = 0f; c.AimTarget = 0; }
            Combat[index] = c;
            return ready;
        }

        private void StopAim(int i)
        {
            int id = IdOf[i], index = NetIdAllocator.IndexOf(id);
            if ((uint)index >= (uint)Combat.Length || Combat[index].Id != id || Combat[index].AimTarget == 0) return;
            UnitCombat c = Combat[index];
            c.AimTarget = 0;
            c.Aim = 0f;
            Combat[index] = c;
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
        [ReadOnly] public NativeArray<byte> OwnerSlot, Type, BuildingFlag, BuildingOwnerSlot, WallState, WallOwnerSlot, ArmourByType, Airborne;
        [ReadOnly] public NativeArray<int> CellStart, Sorted, BuildingCellStart, BuildingSorted;
        [ReadOnly] public NativeArray<ushort> AttackMask;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;
        [ReadOnly] public NativeArray<float> DamageTable, FlatArmourByType;
        public float MinHit;

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
                        if (UnitHealth[u] <= 0f || Airborne[u] != 0 || (r.PrimaryKind == TargetKinds.Unit && r.PrimarySlot == u) || !SimData.Attacks(AttackMask, r.AttackerSlot, OwnerSlot[u])) continue;
                        float dSq = math.distancesq(Positions[u], r.Centre);
                        if (dSq > rSq) continue;
                        float amount = r.Damage * Falloff(math.sqrt(dSq), r.Radius) * WAR2D.Rules.DamageTable.Get(DamageTable, WAR2D.Rules.DamageTable.UnitAttacker(r.AttackerType), (TargetClass)ArmourByType[Type[u]]);
                        // v0.20: a flame's splash is a direct hit (spec §7.4): flat armour applies.
                        amount = CombatRules.DirectHit(amount, FlatArmourByType[Type[u]], MinHit);
                        Hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = u, Amount = amount, AttackerOwner = r.AttackerOwner, From = r.From, Ignite = r.Ignite });
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
                        Hits.Enqueue(new DamageHit { Kind = TargetKinds.Building, Slot = b, Amount = amount, AttackerOwner = r.AttackerOwner, From = r.From, Ignite = r.Ignite });
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
    /// v0.20 Bombers (spec §2): each tick a Bomber on the ground with a unit target picks its run-in point, the centre of the nearest
    /// army (every enemy unit its side sees within <c>Bomber/ArmyTiles</c> of that target), and leaps when the target is within its
    /// range and the straight line to the landing point (the centre, or <c>Bomber/LeapReach</c> along the line to it) crosses no wall,
    /// rock or building. Against a building it leaps onto the middle of the nearest side. In the air it can't be hit and holds no
    /// target; it lands on its leap's last tick and explodes there. A Bomber touching its target, or ordered to detonate, goes off
    /// where it stands, as before v0.20. The blast hurts everything in its radius (<see cref="BlastWorld"/>); the Bomber dies.
    /// </summary>
    [BurstCompile]
    public struct BomberJob : IJob
    {
        [ReadOnly] public SearchInput Search;
        public NativeQueue<DamageHit> Hits;
        public NativeQueue<BombBlast> Blasts;
        public NativeArray<byte> Spent;
        [ReadOnly] public NativeParallelHashSet<int> Forced;
        public BlastWorld World;
        public NativeArray<UnitCombat> Combat;
        public NativeList<LeapEvent> Leaps;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> UnitHealth, BuildingHealth, Radius, RangeSqByType;
        [ReadOnly] public NativeArray<byte> TargetKind, Tiles, Used;
        [ReadOnly] public NativeArray<int> IdOf, Target;
        public float LeapReach, ArmyTiles, ContactTiles;
        public int LeapTicks, Tick;

        public void Execute()
        {
            // Bombers of one owner after the same unit share its army's centre: worked out once a tick (perf gate 2026-11-07: a
            // crowd of Bombers each scanning the same army was most of the job's time at 80,000 units).
            var centres = new NativeHashMap<long, float2>(64, Allocator.Temp);
            for (int i = 0; i < World.UnitCount; i++)
            {
                Spent[i] = 0;
                int type = World.Type[i];
                if (UnitHealth[i] <= 0f || type >= World.BlastRadiusByType.Length || World.BlastRadiusByType[type] <= 0f) continue;
                int id = IdOf[i], index = NetIdAllocator.IndexOf(id);
                UnitCombat c = UnitCombat.For(Combat[index], id);
                c.HasRunPoint = 0;
                if (c.LeapEnd > 0)
                {
                    if (Tick < c.LeapEnd) { Combat[index] = c; continue; } // in the air: orders, Detonate and damage don't reach it
                    // Landing: it explodes where it chose, whatever has moved since (movement has carried it there).
                    c.LeapEnd = 0;
                    Combat[index] = c;
                    GoOff(i, type, c.LeapTo);
                    continue;
                }
                bool go = Forced.Contains(id);
                int target = Target[i];
                if (!go && target >= 0)
                {
                    float2 p = Positions[i];
                    float range = math.sqrt(RangeSqByType[type]);
                    switch (TargetKind[i])
                    {
                        case TargetKinds.Unit:
                        {
                            long key = ((long)target << 8) | (uint)Search.OwnerSlot[i];
                            if (!centres.TryGetValue(key, out float2 centre))
                            {
                                centre = ArmyCentre(i, target);
                                centres.Add(key, centre);
                            }
                            c.RunPoint = centre;
                            c.HasRunPoint = 1;
                            if (math.distance(p, Positions[target]) <= range + Radius[target])
                            {
                                float2 land = Reach(p, centre);
                                if (ClearLeap(p, land)) { StartLeap(ref c, id, p, land); break; }
                            }
                            go = InContact(i, type);
                            break;
                        }
                        case TargetKinds.Building:
                        {
                            float2 side = SideMiddle(target, p);
                            c.RunPoint = side;
                            c.HasRunPoint = 1;
                            if (math.distance(p, side) <= range && ClearLeap(p, side)) { StartLeap(ref c, id, p, side); break; }
                            go = InContact(i, type);
                            break;
                        }
                        default:
                            go = InContact(i, type); // a wall: on contact, as now
                            break;
                    }
                }
                Combat[index] = c;
                if (go) GoOff(i, type, Positions[i]);
            }
        }

        private void GoOff(int i, int type, float2 at)
        {
            Spent[i] = 1;
            World.Detonate(i, type, at, Positions, UnitHealth, BuildingHealth, Hits, Blasts);
            // The bomber itself is spent.
            Hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = i, Amount = UnitHealth[i], AttackerOwner = World.OwnerId[i], HitKind = HitKinds.Blast });
        }

        private void StartLeap(ref UnitCombat c, int id, float2 from, float2 to)
        {
            c.LeapFrom = from;
            c.LeapTo = to;
            c.LeapStart = Tick;
            c.LeapEnd = Tick + LeapTicks;
            c.AimTarget = 0;
            Leaps.Add(new LeapEvent { Id = id, From = from, To = to, Start = c.LeapStart, End = c.LeapEnd });
        }

        /// <summary>The landing point: <paramref name="centre"/>, or <see cref="LeapReach"/> along the line to it when it is further.</summary>
        private float2 Reach(float2 from, float2 centre)
        {
            float2 d = centre - from;
            float length = math.length(d);
            return length > LeapReach && length > 0f ? from + d * (LeapReach / length) : centre;
        }

        /// <summary>
        /// The average position of every living enemy unit on the ground the Bomber's side sees within <see cref="ArmyTiles"/> of the
        /// target (the nearest enemy unit), the target included. Only what its side sees: its route reaches its owner's client.
        /// </summary>
        private float2 ArmyCentre(int i, int target)
        {
            ref readonly SearchInput s = ref Search;
            int slot = s.OwnerSlot[i], grid = slot * s.FogCells;
            float2 about = s.Positions[target], sum = float2.zero;
            int n = 0;
            float rSq = ArmyTiles * ArmyTiles;
            int2 c = (int2)math.floor(about * s.InvCellSize);
            int reach = (int)math.ceil(ArmyTiles * s.InvCellSize) + 1;
            int2 lo = math.max(c - reach, 0), hi = math.min(c + reach, new int2(s.CellsX - 1, s.CellsY - 1));
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * s.CellsX + cx;
                for (int k = s.CellStart[cell]; k < s.CellStart[cell + 1]; k++)
                {
                    int u = s.Sorted[k];
                    if (s.Health[u] <= 0f || s.Airborne[u] != 0 || !SimData.Attacks(s.AttackMask, slot, s.OwnerSlot[u])) continue;
                    if (math.distancesq(s.Positions[u], about) > rSq || !TargetSearch.Sees(s, grid, s.Positions[u])) continue;
                    sum += s.Positions[u];
                    n++;
                }
            }
            return n > 0 ? sum / n : about;
        }

        /// <summary>The point half a tile outside the middle of the building's side nearest <paramref name="p"/>.</summary>
        private float2 SideMiddle(int b, float2 p)
        {
            float2 lo = Footprint.Start(World.BuildingAnchor[b], World.BuildingSize[b]), hi = lo + World.BuildingSize[b], mid = (lo + hi) * 0.5f;
            float2 best = mid;
            float bestD = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float2 q = k switch
                {
                    0 => new float2(lo.x - 0.5f, mid.y),
                    1 => new float2(hi.x + 0.5f, mid.y),
                    2 => new float2(mid.x, lo.y - 0.5f),
                    _ => new float2(mid.x, hi.y + 0.5f),
                };
                float d = math.distancesq(p, q);
                if (d < bestD) { bestD = d; best = q; }
            }
            return best;
        }

        /// <summary>True when the straight line crosses no wall, rock or building tile (spec §2.2: a leap goes over units only).</summary>
        private bool ClearLeap(float2 a, float2 b)
        {
            int steps = (int)math.ceil(math.distance(a, b) * 4f);
            for (int k = 1; k <= steps; k++)
            {
                int2 t = (int2)math.floor(math.lerp(a, b, k / (float)steps));
                if ((uint)t.x >= (uint)World.Width || (uint)t.y >= (uint)World.Height) return false;
                int index = t.y * World.Width + t.x;
                if (!TileRules.IsFloor(Tiles[index]) || Used[index] != 0) return false;
            }
            return true;
        }

        private bool InContact(int i, int type)
        {
            int target = Target[i];
            if (target < 0) return false;
            float reach = Radius[i] + ContactTiles; // Bomber/ContactTiles: the pre-v0.20 contact distance (its range then)
            float2 p = Positions[i];
            switch (TargetKind[i])
            {
                case TargetKinds.Unit:
                    return math.distance(p, Positions[target]) <= reach + Radius[target];
                case TargetKinds.Wall:
                {
                    float2 lo = new float2(target % World.Width, target / World.Width);
                    return math.lengthsq(math.max(math.max(lo - p, p - (lo + 1f)), 0f)) <= reach * reach;
                }
                default:
                {
                    float2 lo = Footprint.Start(World.BuildingAnchor[target], World.BuildingSize[target]), hi = lo + World.BuildingSize[target];
                    return math.lengthsq(math.max(math.max(lo - p, p - hi), 0f)) <= reach * reach;
                }
            }
        }
    }

    /// <summary>
    /// What a blast reaches: everything in its radius, friends at <see cref="FriendlyDamage"/> of the damage (playtest 2026-11-04), less
    /// each unit's blast resistance (v0.20); a unit in the air is untouched. Shared by Bombers (contact, leap, Detonate and the last
    /// gasp) and, with a falloff and their own friendly share, by shells (<see cref="Shell"/>).
    /// </summary>
    public struct BlastWorld
    {
        public int UnitCount, BuildingCount, Width, Height, CellSize, CellsX, CellsY;
        [ReadOnly] public NativeArray<float> BlastRadiusByType, BlastDamageByType, DamageTable, BlastResistByType;
        [ReadOnly] public NativeArray<byte> Type, WallState, ArmourByType, Airborne;
        [ReadOnly] public NativeArray<int> OwnerId, CellStart, Sorted;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;
        [ReadOnly] public NativeArray<ushort> AttackMask;
        [ReadOnly] public NativeArray<byte> OwnerSlot, BuildingOwnerSlot, WallOwnerSlot;
        public float FriendlyDamage;

        /// <summary>The share of a blast that reaches an owner: all of it for an enemy, <paramref name="friendly"/> for anyone else.</summary>
        private float Share(int from, int to, float friendly) => SimData.Attacks(AttackMask, from, to) ? 1f : friendly;

        /// <summary>Queues a Bomber's blast and its hits on everything but the bomber (full damage across the radius, as in v0.18).</summary>
        public void Detonate(int bomber, int type, float2 at, NativeArray<float2> positions, NativeArray<float> unitHealth, NativeArray<float> buildingHealth, NativeQueue<DamageHit> hits, NativeQueue<BombBlast> blasts)
        {
            Blast(at, BlastRadiusByType[type], BlastDamageByType[type], type, OwnerId[bomber], OwnerSlot[bomber], FriendlyDamage, false, bomber, positions, unitHealth, buildingHealth, hits);
            blasts.Enqueue(new BombBlast { Position = at, OwnerId = OwnerId[bomber], Bomber = 1 });
        }

        /// <summary>
        /// One blast at <paramref name="centre"/>: every unit (but <paramref name="skip"/>), building and wall tile within
        /// <paramref name="radius"/> takes <paramref name="damage"/> times the damage table's multiplier for <paramref name="type"/>'s
        /// row, times its owner's share, less a unit's resistance; with <paramref name="falloff"/>, 100 % at the centre to 50 % at the edge.
        /// </summary>
        public void Blast(float2 centre, float radius, float damage, int type, int owner, int slot, float friendly, bool falloff, int skip,
            NativeArray<float2> positions, NativeArray<float> unitHealth, NativeArray<float> buildingHealth, NativeQueue<DamageHit> hits)
        {
            float rSq = radius * radius;
            int row = WAR2D.Rules.DamageTable.UnitAttacker(type);
            int reach = (int)math.ceil(radius / CellSize) + 1;
            int2 cell = math.clamp((int2)math.floor(centre / CellSize), 0, new int2(CellsX - 1, CellsY - 1));
            for (int cy = math.max(cell.y - reach, 0); cy <= math.min(cell.y + reach, CellsY - 1); cy++)
            for (int cx = math.max(cell.x - reach, 0); cx <= math.min(cell.x + reach, CellsX - 1); cx++)
            {
                int c = cy * CellsX + cx;
                for (int k = CellStart[c]; k < CellStart[c + 1]; k++)
                {
                    int u = Sorted[k];
                    if (u == skip || unitHealth[u] <= 0f || Airborne[u] != 0) continue;
                    float dSq = math.distancesq(positions[u], centre);
                    if (dSq > rSq) continue;
                    int t = Type[u];
                    float amount = damage * Share(slot, OwnerSlot[u], friendly) * WAR2D.Rules.DamageTable.Get(DamageTable, row, (TargetClass)ArmourByType[t]);
                    if (falloff) amount *= SplashJob.Falloff(math.sqrt(dSq), radius);
                    amount = CombatRules.Resisted(amount, BlastResistByType[t]);
                    hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = u, Amount = amount, AttackerOwner = owner, HitKind = HitKinds.Blast });
                }
            }

            float buildingAmount = damage * WAR2D.Rules.DamageTable.Get(DamageTable, row, TargetClass.Building);
            for (int b = 0; b < BuildingCount; b++)
            {
                if (buildingHealth[b] <= 0f) continue;
                float2 lo = Footprint.Start(BuildingAnchor[b], BuildingSize[b]), hi = lo + BuildingSize[b];
                float gapSq = math.lengthsq(math.max(math.max(lo - centre, centre - hi), 0f));
                if (gapSq > rSq) continue;
                float amount = buildingAmount * Share(slot, BuildingOwnerSlot[b], friendly);
                if (falloff) amount *= SplashJob.Falloff(math.sqrt(gapSq), radius);
                hits.Enqueue(new DamageHit { Kind = TargetKinds.Building, Slot = b, Amount = amount, AttackerOwner = owner, HitKind = HitKinds.Blast });
            }

            float wallAmount = damage * WAR2D.Rules.DamageTable.Get(DamageTable, row, TargetClass.Wall);
            int2 tile = (int2)math.floor(centre);
            int r = (int)math.ceil(radius);
            for (int y = math.max(tile.y - r, 0); y <= math.min(tile.y + r, Height - 1); y++)
            for (int x = math.max(tile.x - r, 0); x <= math.min(tile.x + r, Width - 1); x++)
            {
                int w = y * Width + x;
                if (WallState[w] == WallStates.None) continue;
                float dSq = math.distancesq(new float2(x + 0.5f, y + 0.5f), centre);
                if (dSq > rSq) continue;
                float amount = wallAmount * Share(slot, WallOwnerSlot[w], friendly);
                if (falloff) amount *= SplashJob.Falloff(math.sqrt(dSq), radius);
                hits.Enqueue(new DamageHit { Kind = TargetKinds.Wall, Slot = w, Amount = amount, AttackerOwner = owner, HitKind = HitKinds.Blast });
            }
        }
    }

    /// <summary>
    /// v0.20 Artillery shells (spec §4.2): this tick's launches join the shells in flight (landing after their flight at
    /// <c>Artillery/ShellSpeed</c>, at least a tick), and every shell due lands: a blast of its gun's damage over its blast radius,
    /// falling to half at the edge, own and allied at <c>Artillery/FriendlyDamage</c>, less blast resistance. A shell lands even if its
    /// gun has died; its kills go to the gun's owner.
    /// </summary>
    [BurstCompile]
    public struct ShellJob : IJob
    {
        [ReadOnly] public NativeArray<ShellLaunch> Launches;
        public NativeList<Shell> Shells, Launched;
        public NativeArray<int> Serial;
        public BlastWorld World;
        [ReadOnly] public NativeArray<float> DamageByType, ShellRadiusByType;
        public float FriendlyDamage, ShellSpeed, Dt;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> UnitHealth, BuildingHealth;
        public NativeQueue<DamageHit> Hits;
        public NativeQueue<BombBlast> Blasts;
        public int Tick;

        public void Execute()
        {
            for (int k = 0; k < Launches.Length; k++)
            {
                ShellLaunch l = Launches[k];
                float seconds = math.distance(l.From, l.To) / math.max(0.01f, ShellSpeed);
                int flight = math.max(1, (int)math.ceil(seconds / Dt - 1e-3f));
                int serial = Serial[0] + 1;
                Serial[0] = serial;
                var shell = new Shell
                {
                    Id = serial, From = l.From, To = l.To, LaunchTick = Tick, LandTick = Tick + flight,
                    OwnerId = l.OwnerId, OwnerSlot = l.OwnerSlot, Type = l.Type, ArtilleryId = l.ArtilleryId,
                };
                Shells.Add(shell);
                Launched.Add(shell);
            }
            for (int k = Shells.Length - 1; k >= 0; k--)
            {
                Shell shell = Shells[k];
                if (shell.LandTick > Tick) continue;
                World.Blast(shell.To, ShellRadiusByType[shell.Type], DamageByType[shell.Type], shell.Type, shell.OwnerId, shell.OwnerSlot, FriendlyDamage, true, -1,
                    Positions, UnitHealth, BuildingHealth, Hits);
                Blasts.Enqueue(new BombBlast { Position = shell.To, OwnerId = shell.OwnerId, Bomber = 2 });
                Shells.RemoveAtSwapBack(k);
            }
        }
    }

    /// <summary>
    /// v0.20 fire (spec §7.3): every burning unit and building (from <see cref="SimData.BurningUnits"/> and
    /// <see cref="SimData.BurningBuildings"/>, not a pass over everything) takes <c>Burn/Dps</c> times its class's rate each second, once
    /// a tick, through the hit queue, credited to whoever lit it last; its fire burns down with time, and it leaves the list when out
    /// or gone. A unit in the air takes none.
    /// </summary>
    [BurstCompile]
    public struct BurnJob : IJob
    {
        public int Count, BuildingCount;
        [ReadOnly] public NativeArray<int> IdOf, BuildingIds, IndexOfId, BuildingSlotOfIndex;
        [ReadOnly] public NativeArray<float> Health, BuildingHealth;
        [ReadOnly] public NativeArray<byte> Type, ArmourByType, Airborne;
        public NativeArray<UnitCombat> Combat;
        public NativeArray<BuildingFire> BuildingFires;
        public NativeList<int> BurningUnits, BurningBuildings;
        public float Dps, Light, Heavy, Building, Dt;
        public NativeQueue<DamageHit> Hits;

        public void Execute()
        {
            for (int k = BurningUnits.Length - 1; k >= 0; k--)
            {
                int id = BurningUnits[k], index = NetIdAllocator.IndexOf(id);
                int i = (uint)index < (uint)IndexOfId.Length ? IndexOfId[index] : -1;
                bool live = (uint)i < (uint)Count && IdOf[i] == id && Health[i] > 0f;
                UnitCombat c = Combat[index];
                if (!live || c.Id != id || c.BurnLeft <= 0f)
                {
                    if (c.Id == id) { c.BurnLeft = 0f; Combat[index] = c; }
                    BurningUnits.RemoveAtSwapBack(k);
                    continue;
                }
                c.BurnLeft = math.max(0f, c.BurnLeft - Dt);
                Combat[index] = c;
                if (Airborne[i] != 0) continue;
                float rate = (TargetClass)ArmourByType[Type[i]] == TargetClass.HeavyUnit ? Heavy : Light;
                Hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = i, Amount = Dps * rate * Dt, AttackerOwner = c.BurnBy, HitKind = HitKinds.Fire });
            }
            for (int k = BurningBuildings.Length - 1; k >= 0; k--)
            {
                int id = BurningBuildings[k], index = NetIdAllocator.IndexOf(id);
                int b = (uint)index < (uint)BuildingSlotOfIndex.Length ? BuildingSlotOfIndex[index] : -1;
                bool live = (uint)b < (uint)BuildingCount && BuildingIds[b] == id && BuildingHealth[b] > 0f;
                BuildingFire f = (uint)index < (uint)BuildingFires.Length ? BuildingFires[index] : default;
                if (!live || f.Id != id || f.Left <= 0f)
                {
                    if (f.Id == id) { f.Left = 0f; BuildingFires[index] = f; }
                    BurningBuildings.RemoveAtSwapBack(k);
                    continue;
                }
                f.Left = math.max(0f, f.Left - Dt);
                BuildingFires[index] = f;
                Hits.Enqueue(new DamageHit { Kind = TargetKinds.Building, Slot = b, Amount = Dps * Building * Dt, AttackerOwner = f.By, HitKind = HitKinds.Fire });
            }
        }
    }

    /// <summary>
    /// A bomber at or under <c>Bomber/LastGaspHealth</c> of its health explodes where it stands, a killing hit included
    /// (v0.18; v0.20: whatever killed it, fire too). A blast can set off more bombers: at most <see cref="Passes"/> rounds a tick. Nothing
    /// dead has left the arrays yet (the lifecycle stage removes it), so a bomber shot down this tick still has its slot and position.
    /// Its death keeps <c>LastHitBy</c>, so whoever shot it down gets the kill. A Bomber in the air is untouched.
    /// </summary>
    [BurstCompile]
    public struct LastGaspJob : IJob
    {
        public ApplyDamageJob Apply;
        /// <summary>v0.20: this tick's leaps, so a Bomber shot down on the tick it leapt takes its leap back (it never left the ground).</summary>
        public NativeList<LeapEvent> Leaps;
        public NativeQueue<BombBlast> Blasts;
        public NativeArray<byte> Spent;
        public BlastWorld World;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> MaxHealth;
        public float Threshold;
        public int Passes;

        public void Execute()
        {
            NativeArray<float> health = Apply.Health;
            for (int pass = 0; pass < Passes; pass++)
            {
                bool any = false;
                for (int i = 0; i < World.UnitCount; i++)
                {
                    int type = World.Type[i];
                    if (Spent[i] != 0 || World.Airborne[i] != 0 || type >= World.BlastRadiusByType.Length || World.BlastRadiusByType[type] <= 0f) continue;
                    if (health[i] > MaxHealth[i] * Threshold) continue;
                    Spent[i] = 1;
                    any = true;
                    CancelLeap(i);
                    World.Detonate(i, type, Positions[i], Positions, health, Apply.BuildingHealth, Apply.Hits, Blasts);
                }
                if (!any) return;
                for (int i = 0; i < World.UnitCount; i++) if (Spent[i] != 0) health[i] = 0f;
                Apply.Execute();
            }
        }

        private void CancelLeap(int i)
        {
            int id = Apply.IdOf[i], index = NetIdAllocator.IndexOf(id);
            UnitCombat c = Apply.Combat[index];
            if (c.Id != id || c.LeapEnd <= 0) return;
            c.LeapEnd = 0;
            Apply.Combat[index] = c;
            for (int k = Leaps.Length - 1; k >= 0; k--) if (Leaps[k].Id == id) Leaps.RemoveAtSwapBack(k);
        }
    }

    /// <summary>
    /// The single writer of damage: units lose health (and remember who hit them last, and v0.20 how) in the SoA, buildings accumulate
    /// it. v0.20: a unit in the air takes nothing; a hit that ignites sets its unit or building burning for <c>Burn/Seconds</c> (one
    /// fire per target: a new flame only resets the timer and who lit it).
    /// </summary>
    [BurstCompile]
    public struct ApplyDamageJob : IJob
    {
        public NativeQueue<DamageHit> Hits;
        public NativeArray<float> Health;
        public NativeArray<int> LastHitBy;
        public NativeArray<float> BuildingHealth;
        public NativeArray<float> BuildingDamage;
        public NativeQueue<TileWork> WallDamage;
        public NativeArray<int> HqLastHitBy;
        [ReadOnly] public NativeArray<byte> BuildingKind, BuildingOwnerSlot;
        /// <summary>For the alarms (only hits with a <see cref="DamageHit.From"/> read them, so the last gasp's blasts leave them unset).</summary>
        [ReadOnly] public NativeArray<byte> OwnerSlot;
        [ReadOnly] public NativeArray<float2> Positions, BuildingPositions;
        public NativeArray<DefendAlarm> Alarms;
        public NativeArray<int> AlarmCount;
        public float AlarmCellTiles;
        [ReadOnly] public NativeArray<int> IdOf, BuildingIds;
        [ReadOnly] public NativeArray<byte> Airborne;
        public NativeArray<UnitCombat> Combat;
        public NativeArray<BuildingFire> BuildingFires;
        public NativeArray<byte> LastHitKind;
        public NativeList<int> BurningUnits, BurningBuildings;
        public float BurnSeconds;

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
                    if (Airborne[hit.Slot] != 0) continue;
                    Health[hit.Slot] = math.max(0f, Health[hit.Slot] - hit.Amount);
                    LastHitBy[hit.Slot] = hit.AttackerOwner;
                    int id = IdOf[hit.Slot], index = NetIdAllocator.IndexOf(id);
                    LastHitKind[index] = hit.HitKind;
                    if (hit.Ignite != 0)
                    {
                        UnitCombat c = UnitCombat.For(Combat[index], id);
                        if (c.BurnLeft <= 0f) BurningUnits.Add(id); // newly lit: the fire pass visits it from now
                        c.BurnLeft = BurnSeconds;
                        c.BurnBy = hit.AttackerOwner;
                        Combat[index] = c;
                    }
                    if (hit.From.x != 0f || hit.From.y != 0f) DefendAlarms.Raise(Alarms, AlarmCount, AlarmCellTiles, OwnerSlot[hit.Slot], Positions[hit.Slot], hit.From);
                }
                else
                {
                    if (hit.From.x != 0f || hit.From.y != 0f) DefendAlarms.Raise(Alarms, AlarmCount, AlarmCellTiles, BuildingOwnerSlot[hit.Slot], BuildingPositions[hit.Slot], hit.From);
                    BuildingHealth[hit.Slot] = math.max(0f, BuildingHealth[hit.Slot] - hit.Amount);
                    BuildingDamage[hit.Slot] += hit.Amount;
                    if (BuildingKind[hit.Slot] == (byte)BuildingType.Base && BuildingOwnerSlot[hit.Slot] < HqLastHitBy.Length) HqLastHitBy[BuildingOwnerSlot[hit.Slot]] = hit.AttackerOwner;
                    if (hit.Ignite != 0 && BuildingHealth[hit.Slot] > 0f)
                    {
                        int id = BuildingIds[hit.Slot], index = NetIdAllocator.IndexOf(id);
                        if ((uint)index < (uint)BuildingFires.Length)
                        {
                            BuildingFire was = BuildingFires[index];
                            if (was.Id != id || was.Left <= 0f) BurningBuildings.Add(id);
                            BuildingFires[index] = new BuildingFire { Id = id, Left = BurnSeconds, By = hit.AttackerOwner };
                        }
                    }
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

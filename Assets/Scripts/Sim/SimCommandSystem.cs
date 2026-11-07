using WAR2D.Dev.Logging;
using System.Collections.Generic;
using Config;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace WAR2D.Sim
{
    /// <summary>
    /// Applies the main thread's <see cref="SimCommand"/>s right after the boundary, while no tick job is
    /// running. Every structural change to units and buildings happens here. Building creation and kills
    /// apply in any game state; spawns and moves wait until the tick is running.
    /// </summary>
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimBoundarySystem))]
    public partial class SimCommandSystem : SystemBase
    {
        private EntityArchetype unitArchetype;
        private EntityQuery unitQuery;
        private EntityQuery buildingQuery;
        private readonly List<SimCommand> deferred = new List<SimCommand>();
        /// <summary>Buildings created in this drain: not in the building arrays until the next gather.</summary>
        private readonly HashSet<int> createdNow = new HashSet<int>();

        protected override void OnCreate()
        {
            RequireForUpdate<SimData>();
            unitArchetype = EntityManager.CreateArchetype(typeof(Unit));
            unitQuery = GetEntityQuery(ComponentType.ReadWrite<Unit>());
            buildingQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingData>(), ComponentType.ReadWrite<HealthComponent>());
        }

        /// <summary>Drops per-match state when a match's simulation is disposed.</summary>
        internal void ResetForNewMatch()
        {
            deferred.Clear();
            legs.Clear();
            chases.Clear();
            attackEnded.Clear();
            pendingRead = true;
        }

        /// <summary>True when the last boundary's pending orders and moves were read by its gather (it runs only while the clock does).</summary>
        private bool pendingRead = true;

        protected override void OnUpdate()
        {
            double mark = SimBoundarySystem.TickWatch.Elapsed.TotalMilliseconds;
            ApplyCommands();
            SimTiming.LastCommandsMs = SimBoundarySystem.Split(ref mark);
            SimTiming.CommandsEndMs = mark;
        }

        private void ApplyCommands()
        {
            SimContext context = SimContext.Current;
            if (context == null) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            SimClock clock = SystemAPI.GetSingleton<SimClock>();

            double split = PerfSplits.Now, ordersMs = 0;
            // Cleared first: a wall rising below applies tiles that push units off them (PushUnitsOffTiles). Only once a gather has
            // read them: one set at a paused boundary waits for the first running tick (bug review 2026-11-05: pause, order, step lost it).
            if (pendingRead)
            {
                data.PendingOrders.Clear();
                data.PendingMoves.Clear();
            }
            pendingRead = clock.Running; // the gather of this tick reads them only while the clock runs
            WriteBackBuildingDamage(data);
            WriteBackBuildingWeapons(data);
            bool wallsChanged = ApplyWallWork(data, context) | ApplyDigDone(data, context);
            ApplyBuildingWork(data, context);
            if (clock.Running)
            {
                ClearFinishedWork(data, context, clock.UnitCount, clock.Tick);
                RetryWaitingDiggers(data, context, clock.UnitCount, clock.Tick);
                ApplyDigMoves(data, context, clock.UnitCount, clock.Tick);
                PerfSplits.Mark(PerfSplits.CmdWork, ref split);
                UpdateChases(data, context, clock.UnitCount, clock.Tick);
                AdvanceWaypoints(data, context, clock.UnitCount, clock.Tick);
            }
            else PerfSplits.Mark(PerfSplits.CmdWork, ref split);
            PerfSplits.Mark(PerfSplits.CmdWaypoints, ref split);

            List<SimCommand> commands = context.Commands.Drain();
            deferred.Clear();
            createdNow.Clear();
            bool terrainChanged = wallsChanged;
            foreach (SimCommand command in commands)
            {
                switch (command.Kind)
                {
                    case SimCommandKind.CreateBuilding:
                        CreateBuilding(context, command.Building);
                        createdNow.Add(command.Building.Data.id);
                        PushUnitsOutOf(data, context, command.Building, clock.UnitCount);
                        break;
                    case SimCommandKind.SetTile:
                        terrainChanged |= ApplySetTile(data, context, command.Tile, command.TileType, clock.UnitCount);
                        break;
                    case SimCommandKind.PlaceWall:
                    {
                        bool placed = ApplyPlaceWall(data, context, command.OwnerId, command.Tile, command.TryPay);
                        terrainChanged |= placed;
                        command.OnApplied?.Invoke(placed);
                        break;
                    }
                    case SimCommandKind.RemoveWall:
                    {
                        byte was = ApplyRemoveOwnWall(data, context, command.OwnerId, command.Tile, clock.UnitCount);
                        terrainChanged |= was != WallStates.None;
                        if (was != WallStates.None) command.OnWallRemoved?.Invoke(was);
                        break;
                    }
                    case SimCommandKind.DetonateBomb:
                    {
                        // Only where the placer's side sees the tile, so building at random never probes for bombs.
                        int sightGrid = context.VisionOf(command.OwnerId);
                        if (sightGrid >= 0 && data.Sees(sightGrid, (float2)command.Tile + 0.5f)) data.ForcedBombs.Add(command.TargetId);
                        break;
                    }
                    case SimCommandKind.KillUnits:
                        KillIds(data, context, command.Ids);
                        break;
                    case SimCommandKind.KillOwner:
                        KillUnits(data, context, command.OwnerId, all: false);
                        KillBuildings(command.OwnerId);
                        terrainChanged |= RemoveOwnerWalls(data, context, command.OwnerId, clock.UnitCount);
                        break;
                    case SimCommandKind.DestroyAll:
                        KillUnits(data, context, 0, all: true);
                        break;
                    case SimCommandKind.SetAttack:
                    case SimCommandKind.SetShareVision:
                        ApplyDiplomacy(context, command);
                        break;
                    case SimCommandKind.SpawnUnit:
                        if (clock.Running) Spawn(data, context, command);
                        else deferred.Add(command);
                        break;
                    case SimCommandKind.OrderUnits:
                        if (clock.Running && !TargetsBuildingCreatedNow(context, command))
                        {
                            double start = PerfSplits.Now;
                            ApplyOrder(data, context, command, clock.UnitCount);
                            ordersMs += PerfSplits.Now - start;
                        }
                        else deferred.Add(command);
                        break;
                    case SimCommandKind.DigArea:
                        if (clock.Running) ApplyDigArea(data, context, command, clock.UnitCount);
                        else deferred.Add(command);
                        break;
                }
            }
            if (terrainChanged) data.RebuildOpaqueSum();
            if (deferred.Count > 0) context.Commands.Requeue(deferred);
            PerfSplits.Mark(PerfSplits.CmdOther, ref split);
            PerfSplits.Ms[PerfSplits.CmdOrders] = ordersMs;
            PerfSplits.Ms[PerfSplits.CmdOther] -= ordersMs;
            if (clock.Running) AutoWork(data, context, clock.UnitCount, clock.Tick); // v0.16: after the players' orders, which win
            PerfSplits.Mark(PerfSplits.CmdAutoWork, ref split);
            if (clock.Running) AnswerAlarms(data, context, clock.UnitCount, clock.Tick);
            PerfSplits.Mark(PerfSplits.CmdAlarms, ref split);
            if (context.Diplomacy.Dirty) context.Diplomacy.CopyTo(data);
        }

        /// <summary>
        /// True for a Build or Repair order on a building created earlier in this drain. It waits one boundary:
        /// the gather has not put the building in the arrays its target check reads, so it would be dropped
        /// (placing a blueprint sends the selected Builders in the same frame).
        /// </summary>
        private bool TargetsBuildingCreatedNow(SimContext context, in SimCommand command) =>
            createdNow.Count > 0 && (command.Order == OrderKind.Build || command.Order == OrderKind.Repair) && context.Map.Grid.Contains(command.Tile) &&
            context.BuildingAt.TryGetValue(context.Map.Grid.Index(command.Tile), out int id) && createdNow.Contains(id);

        /// <summary>Applies a diplomacy change between two owners with slots, and reports it.</summary>
        private static void ApplyDiplomacy(SimContext context, in SimCommand command)
        {
            int from = context.SlotOf(command.OwnerId), to = context.SlotOf(command.TargetOwnerId);
            if (from < 0 || to < 0 || from == to) return;
            bool before = command.Kind == SimCommandKind.SetAttack ? context.Diplomacy.Attacks(from, to) : context.Diplomacy.SharesVisionWith(from, to);
            if (before == command.Flag) return;
            if (command.Kind == SimCommandKind.SetAttack) context.Diplomacy.SetAttack(from, to, command.Flag);
            else context.Diplomacy.SetShareVision(from, to, command.Flag);
            context.RaiseDiplomacyChanged(command.OwnerId, command.TargetOwnerId);
            if (command.Kind == SimCommandKind.SetShareVision) context.RaiseVisionShareChanged(command.OwnerId, command.TargetOwnerId, command.Flag);
        }

        /// <summary>Turns each tile a Digger finished into floor, paying the owner for gems. True when any changed.</summary>
        private bool ApplyDigDone(SimData data, SimContext context)
        {
            bool changed = false;
            int unitCount = SystemAPI.GetSingleton<SimClock>().UnitCount;
            while (data.DigDone.TryDequeue(out DigEvent done))
            {
                if ((uint)done.Tile >= (uint)data.Tiles.Length) continue;
                var type = (TileType)data.Tiles[done.Tile];
                if (type != TileType.Wall && !TileRules.IsGem(type)) continue;
                int2 tile = new int2(done.Tile % data.Width, done.Tile / data.Width);
                if (!ApplySetTile(data, context, tile, TileType.Ground, unitCount)) continue;
                changed = true;
                if (type == TileType.Gem) context.Income?.Invoke(done.OwnerId, context.Config.Siege.GemPayout);
                else if (type == TileType.RichGem) context.Income?.Invoke(done.OwnerId, context.Config.Siege.RichGemPayout); // v0.17
            }
            return changed;
        }

        /// <summary>
        /// Lays a wall blueprint on a free ground tile for the owner, paying through <paramref name="tryPay"/> once
        /// the tile is known to be seen and free. False when refused.
        /// </summary>
        private static bool ApplyPlaceWall(SimData data, SimContext context, int ownerId, int2 tile, System.Func<bool> tryPay)
        {
            int slot = context.SlotOf(ownerId);
            if (slot < 0) return false; // an owner without a slot would be recorded as slot 0's wall
            // Only on tiles the owner's side sees now: a refusal on an unseen tile must not tell it what stands there.
            if (context.WallsNeedSight)
            {
                int grid = context.VisionOf(ownerId);
                if (grid < 0 || !data.Sees(grid, (float2)tile + 0.5f)) return false;
            }
            if (!WallRules.CanPlace(tile, context.Map.Grid.TileAt, context.Map.IsUsed)) return false;
            if (tryPay != null && !tryPay()) return false;
            if (!context.Map.SetTile(tile, TileType.WallBlueprint)) return false;
            int index = context.Map.Grid.Index(tile);
            data.WallOwnerSlot[index] = (byte)slot;
            data.WallState[index] = WallStates.Blueprint;
            context.UnfinishedWalls.Add(index);
            data.WallHealth[index] = 0f;
            data.RefreshTerrain(tile);
            context.RaiseTerrainChanged(tile);
            return true;
        }

        /// <summary>
        /// Cancels or demolishes the owner's own wall tile (it becomes ground). Returns the <see cref="WallStates"/> it was in,
        /// or <see cref="WallStates.None"/> when the tile is not that owner's wall.
        /// </summary>
        private static byte ApplyRemoveOwnWall(SimData data, SimContext context, int ownerId, int2 tile, int unitCount)
        {
            int slot = context.SlotOf(ownerId);
            if (slot < 0 || !context.Map.Grid.Contains(tile)) return WallStates.None;
            int index = context.Map.Grid.Index(tile);
            byte state = data.WallState[index];
            if (state == WallStates.None || data.WallOwnerSlot[index] != (byte)slot) return WallStates.None;
            RemoveWall(data, context, index, unitCount);
            return state;
        }

        /// <summary>
        /// An eliminated owner's walls go with its buildings: nobody could remove them or build there again (bug review 2026-11-05).
        /// True when any went.
        /// </summary>
        private static bool RemoveOwnerWalls(SimData data, SimContext context, int ownerId, int unitCount)
        {
            int slot = context.SlotOf(ownerId);
            if (slot < 0) return false;
            bool any = false;
            for (int index = 0; index < data.WallState.Length; index++)
            {
                if (data.WallState[index] == WallStates.None || data.WallOwnerSlot[index] != (byte)slot) continue;
                RemoveWall(data, context, index, unitCount);
                any = true;
            }
            return any;
        }

        /// <summary>Removes a wall tile: it becomes ground again.</summary>
        private static void RemoveWall(SimData data, SimContext context, int index, int unitCount)
        {
            int2 tile = new int2(index % data.Width, index / data.Width);
            data.WallOwnerSlot[index] = SimData.NoSlot;
            data.WallState[index] = WallStates.None;
            context.UnfinishedWalls.Remove(index);
            context.DamagedWalls.Remove(index);
            data.WallHealth[index] = 0f;
            ApplySetTile(data, context, tile, TileType.Ground, unitCount);
        }

        /// <summary>Applies the last tick's work and damage to wall tiles. True when any tile changed kind.</summary>
        private bool ApplyWallWork(SimData data, SimContext context)
        {
            bool changed = false;
            int unitCount = SystemAPI.GetSingleton<SimClock>().UnitCount;
            while (data.WallWork.TryDequeue(out TileWork work))
            {
                if ((uint)work.Tile >= (uint)data.WallState.Length) continue;
                byte state = data.WallState[work.Tile];
                int2 tile = new int2(work.Tile % data.Width, work.Tile / data.Width);
                if (state == WallStates.Blueprint)
                {
                    if (!ApplySetTile(data, context, tile, TileType.PlayerWall, unitCount)) continue;
                    changed = true;
                    data.WallState[work.Tile] = WallStates.Rising;
                    data.WallHealth[work.Tile] = math.min(work.Amount, data.WallMaxHealth);
                }
                else if (state == WallStates.Rising)
                {
                    data.WallHealth[work.Tile] = math.min(data.WallMaxHealth, data.WallHealth[work.Tile] + work.Amount);
                    context.Map.MarkChanged(work.Tile);
                }
                else if (state == WallStates.Built)
                {
                    float budget = context.BudgetOf != null ? context.BudgetOf(work.OwnerId) : float.MaxValue;
                    float missing = data.WallMaxHealth - data.WallHealth[work.Tile];
                    float restore = ConstructionRules.AffordableRepair(work.Amount, missing, data.RepairCostPerHealth, budget);
                    if (restore <= 0f)
                    {
                        if (missing > 0f && work.Amount > 0f) context.RaiseRepairStalled(work.OwnerId, new int2(work.Tile % data.Width, work.Tile / data.Width));
                        continue;
                    }
                    data.WallHealth[work.Tile] += restore;
                    if (data.WallHealth[work.Tile] >= data.WallMaxHealth) context.DamagedWalls.Remove(work.Tile);
                    context.Map.MarkChanged(work.Tile);
                    context.Spend?.Invoke(work.OwnerId, restore * data.RepairCostPerHealth);
                }
                if (data.WallState[work.Tile] == WallStates.Rising && data.WallHealth[work.Tile] >= data.WallMaxHealth)
                {
                    data.WallState[work.Tile] = WallStates.Built;
                    context.UnfinishedWalls.Remove(work.Tile);
                }
            }
            while (data.WallDamage.TryDequeue(out TileWork hit))
            {
                if ((uint)hit.Tile >= (uint)data.WallState.Length || data.WallState[hit.Tile] == WallStates.None) continue;
                if (data.WallState[hit.Tile] != WallStates.Blueprint)
                {
                    data.WallHealth[hit.Tile] -= hit.Amount;
                    if (data.WallState[hit.Tile] == WallStates.Built) context.DamagedWalls.Add(hit.Tile);
                    context.Map.MarkChanged(hit.Tile);
                }
                if (data.WallState[hit.Tile] == WallStates.Blueprint || data.WallHealth[hit.Tile] <= 0f)
                {
                    RemoveWall(data, context, hit.Tile, unitCount);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>Applies the last tick's construction work to blueprints (completing them) and repairs to finished buildings.</summary>
        private void ApplyBuildingWork(SimData data, SimContext context)
        {
            int count = data.BuildingCount[0];
            while (data.RepairWork.TryDequeue(out RepairWork repair))
            {
                if ((uint)repair.Slot >= (uint)count) continue;
                Entity target = data.BuildingEntities[repair.Slot];
                if (!EntityManager.Exists(target) || EntityManager.HasComponent<Construction>(target)) continue;
                HealthComponent health = EntityManager.GetComponentData<HealthComponent>(target);
                if (health.currentHealth <= 0f) continue;
                float budget = context.BudgetOf != null ? context.BudgetOf(repair.Payer) : float.MaxValue;
                float missing = health.maxHealth - health.currentHealth;
                float restore = ConstructionRules.AffordableRepair(repair.Work, missing, data.RepairCostPerHealth, budget);
                if (restore <= 0f)
                {
                    if (missing > 0f && repair.Work > 0f) context.RaiseRepairStalled(repair.Payer, data.BuildingAnchor[repair.Slot]);
                    continue;
                }
                health.currentHealth = math.min(health.maxHealth, health.currentHealth + restore);
                EntityManager.SetComponentData(target, health);
                context.Spend?.Invoke(repair.Payer, restore * data.RepairCostPerHealth);
            }
            for (int b = 0; b < count; b++)
            {
                float work = data.BuildingWork[b];
                if (work <= 0f) continue;
                data.BuildingWork[b] = 0f;
                Entity entity = data.BuildingEntities[b];
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Construction>(entity)) continue;
                Construction c = EntityManager.GetComponentData<Construction>(entity);
                HealthComponent h = EntityManager.GetComponentData<HealthComponent>(entity);
                if (h.currentHealth <= 0f) continue;
                bool done = ConstructionRules.AddWork(ref c.Progress, ref h.currentHealth, h.maxHealth, work);
                EntityManager.SetComponentData(entity, h);
                if (!done) { EntityManager.SetComponentData(entity, c); continue; }
                EntityManager.RemoveComponent<Construction>(entity);
                BuildingData building = EntityManager.GetComponentData<BuildingData>(entity);
                EntityManager.AddComponentData(entity, new UpkeepComponent
                {
                    ownerId = building.ownerId,
                    runningCostPerSecond = context.Config.GetBuilding(building.buildingType).RunningCost,
                });
                context.RaiseBuildingCompleted(building.id, building.ownerId);
            }
        }

        /// <summary>Applies the damage the last tick's attacks dealt to buildings.</summary>
        private void WriteBackBuildingDamage(SimData data)
        {
            int count = data.BuildingCount[0];
            // A lookup, not four EntityManager calls a building: a siege hits many buildings a tick (perf gate 2026-11-06).
            // TryGetComponent is false for a gone entity as for one without the component, as the checks were.
            EntityManager.CompleteDependencyBeforeRW<HealthComponent>();
            ComponentLookup<HealthComponent> healths = GetComponentLookup<HealthComponent>(false);
            for (int b = 0; b < count; b++)
            {
                float damage = data.BuildingDamage[b];
                if (damage <= 0f) continue;
                data.BuildingDamage[b] = 0f;
                Entity entity = data.BuildingEntities[b];
                if (!healths.TryGetComponent(entity, out HealthComponent health)) continue;
                health.currentHealth = math.max(0f, health.currentHealth - damage);
                healths[entity] = health;
            }
        }

        /// <summary>
        /// Keeps each attacker building's target (as a stable id, see <c>BuildingAttackJob</c>) and cooldown on its
        /// <see cref="BuildingWeapon"/>, since the next gather refills the slots in a new order.
        /// </summary>
        private void WriteBackBuildingWeapons(SimData data)
        {
            int count = data.BuildingCount[0];
            EntityManager.CompleteDependencyBeforeRW<BuildingWeapon>();
            ComponentLookup<BuildingWeapon> weapons = GetComponentLookup<BuildingWeapon>(false);
            for (int i = 0; i < data.AttackerBuildings.Length; i++)
            {
                int b = data.AttackerBuildings[i];
                if ((uint)b >= (uint)count) continue;
                Entity entity = data.BuildingEntities[b];
                if (!weapons.HasComponent(entity)) continue;
                weapons[entity] = new BuildingWeapon
                {
                    TargetId = data.BuildingTarget[b],
                    TargetKind = data.BuildingTargetKind[b],
                    Cooldown = data.BuildingCooldown[b],
                };
            }
        }

        private void Spawn(SimData data, SimContext context, in SimCommand command)
        {
            int slot = context.SlotOf(command.OwnerId);
            if (slot < 0 || !context.Config.Units.TryGetValue(command.UnitType, out UnitConfig unit) || unit.SizeClass < 0 || unit.SizeClass >= WAR2D.Pathing.FlowSizeClass.Count)
            {
                command.OnSpawned?.Invoke(-1);
                return;
            }
            if (data.UnitsBySlot[slot] >= data.MaxUnitsPerPlayer || context.Ids.Capacity <= 0)
            {
                command.OnSpawned?.Invoke(SimCommand.SpawnRefusedAtCap); // the producer says why (playtest 2026-11-05)
                return;
            }
            // The spawner chose this tile when it queued the unit; a building placed over it since (applied later in this same
            // drain, whose push only moves units already in the arrays) would trap it, so a tile no longer free is chosen again.
            float2 position = command.Position;
            int2 spawnTile = (int2)math.floor(position);
            if (!context.Map.IsWalkable(spawnTile))
            {
                if (!TryFindFreeTile(data, context, spawnTile, unit.SizeClass == WAR2D.Pathing.FlowSizeClass.Large, out int2 free))
                {
                    command.OnSpawned?.Invoke(-1);
                    return;
                }
                position = (float2)free + 0.5f;
            }
            int id;
            try { id = context.Ids.Allocate(); }
            catch (System.InvalidOperationException)
            {
                command.OnSpawned?.Invoke(-1);
                return;
            }
            // Load-bearing: the economy job recounts UnitsBySlot every tick, but several spawns can drain in one
            // boundary, so each counts itself here or a burst would pass the per-player cap check above.
            data.UnitsBySlot[slot]++;
            // v0.20: the id index's combat records start fresh (fire, aim, leap and the last hit are the dead unit's otherwise).
            int idIndex = NetIdAllocator.IndexOf(id);
            if ((uint)idIndex < (uint)data.Combat.Length)
            {
                data.Combat[idIndex] = new UnitCombat { Id = id };
                data.LastHitKind[idIndex] = HitKinds.Shot;
            }
            Entity entity = EntityManager.CreateEntity(unitArchetype);
            EntityManager.SetComponentData(entity, new Unit
            {
                Id = id,
                OwnerId = command.OwnerId,
                OwnerSlot = (byte)slot,
                Type = (byte)command.UnitType,
                SizeClass = (byte)unit.SizeClass,
                Radius = unit.Radius,
                Health = unit.Health,
                MaxHealth = unit.Health,
                Position = position,
                TargetId = -1,
                OrderSlot = -1,
            });
            context.RaiseUnitSpawned(command.OwnerId);
            command.OnSpawned?.Invoke(id);
        }

        /// <summary>Kills the named units (dev tools), like <see cref="KillUnits"/> for an owner.</summary>
        private void KillIds(SimData data, SimContext context, int[] ids)
        {
            if (ids == null || ids.Length == 0) return;
            var wanted = new System.Collections.Generic.HashSet<int>(ids);
            using NativeArray<Entity> entities = unitQuery.ToEntityArray(Allocator.Temp);
            using NativeArray<Unit> units = unitQuery.ToComponentDataArray<Unit>(Allocator.Temp);
            var doomed = new NativeList<Entity>(ids.Length, Allocator.Temp);
            for (int i = 0; i < units.Length; i++)
            {
                if (!wanted.Contains(units[i].Id)) continue;
                doomed.Add(entities[i]);
                // Removed, not killed by a hit: its Leave says a plain death, not what last hurt it.
                int idIndex = NetIdAllocator.IndexOf(units[i].Id);
                if ((uint)idIndex < (uint)data.LastHitKind.Length) data.LastHitKind[idIndex] = HitKinds.Shot;
                context.Ids.Free(units[i].Id);
                if (units[i].OwnerSlot < data.UnitsBySlot.Length) data.UnitsBySlot[units[i].OwnerSlot]--;
                context.RaiseUnitDied(units[i].Id, units[i].Position);
            }
            EntityManager.DestroyEntity(doomed.AsArray());
            doomed.Dispose();
        }

        /// <summary>Destroys every unit of the owner (or every unit), recording each death for its explosion.</summary>
        /// <summary>
        /// Drops every building of the owner to 0 health (DestructionSystem removes it). The owner's kill sets the ones it knew of, but a
        /// placement still queued then is created earlier in this same drain, and would stand for good with a dead owner (bug review
        /// 2026-11-05).
        /// </summary>
        private void KillBuildings(int ownerId)
        {
            using NativeArray<Entity> entities = buildingQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity e in entities)
            {
                if (EntityManager.GetComponentData<BuildingData>(e).ownerId != ownerId) continue;
                HealthComponent hp = EntityManager.GetComponentData<HealthComponent>(e);
                if (hp.currentHealth <= 0f) continue;
                hp.currentHealth = 0f;
                EntityManager.SetComponentData(e, hp);
            }
        }

        private void KillUnits(SimData data, SimContext context, int ownerId, bool all)
        {
            using NativeArray<Entity> entities = unitQuery.ToEntityArray(Allocator.Temp);
            using NativeArray<Unit> units = unitQuery.ToComponentDataArray<Unit>(Allocator.Temp);
            var doomed = new NativeList<Entity>(entities.Length, Allocator.Temp);
            for (int i = 0; i < units.Length; i++)
            {
                if (!all && units[i].OwnerId != ownerId) continue;
                doomed.Add(entities[i]);
                context.Ids.Free(units[i].Id);
                // The economy recounts this next tick; until then, a spawn later in this same drain sees the room.
                if (units[i].OwnerSlot < data.UnitsBySlot.Length) data.UnitsBySlot[units[i].OwnerSlot]--;
                context.RaiseUnitDied(units[i].Id, units[i].Position);
            }
            EntityManager.DestroyEntity(doomed.AsArray());
            doomed.Dispose();
        }

        /// <summary>
        /// Moves every unit standing on a new building's footprint to the nearest free tile (the footprint
        /// is already marked used), so no unit is trapped inside a building.
        /// </summary>
        private static void PushUnitsOutOf(SimData data, SimContext context, in BuildingSpec spec, int unitCount)
        {
            int2 anchor = (int2)math.round(spec.Data.position);
            int2 lo = int2.zero, hi = int2.zero;
            bool first = true;
            foreach (int2 tile in Footprint.Tiles(anchor, spec.Config.Size))
            {
                lo = first ? tile : math.min(lo, tile);
                hi = first ? tile : math.max(hi, tile);
                first = false;
            }
            if (first) return;
            PushUnitsOffTiles(data, context, lo, hi, unitCount);
        }

        /// <summary>Changes a tile (refusing the border), refreshes what depends on it and moves units off it when it now blocks. True when it changed.</summary>
        private static bool ApplySetTile(SimData data, SimContext context, int2 tile, TileType type, int unitCount)
        {
            if (!context.Map.SetTile(tile, type)) return false;
            data.RefreshTerrain(tile);
            if (!TileRules.IsFloor(type)) PushUnitsOffTiles(data, context, tile, tile, unitCount);
            context.RaiseTerrainChanged(tile);
            return true;
        }

        /// <summary>Moves every unit overlapping the inclusive tile box to the nearest free floor tile.</summary>
        private static void PushUnitsOffTiles(SimData data, SimContext context, int2 lo, int2 hi, int unitCount)
        {
            // The overlap scan over every unit runs in Burst: each wall tile that rises runs it (perf gate 2026-11-06).
            using var hits = new NativeList<int>(16, Allocator.TempJob);
            new OverlapTilesJob { Positions = data.Positions, Radius = data.Radius, Count = unitCount, Lo = lo, Hi = hi, Hits = hits }.Run();
            foreach (int slot in hits)
            {
                float2 p = data.Positions[slot];
                bool large = data.SizeClass[slot] == WAR2D.Pathing.FlowSizeClass.Large;
                int2 at = (int2)math.floor(p);
                if (!TryFindFreeTile(data, context, at, large, out int2 free))
                {
                    WarLog.Warn(LogCategory.Sim, $"no free tile near {at} for unit {data.IdOf[slot]}; it stays where it is");
                    continue;
                }
                if (data.PendingMoves.Capacity <= data.PendingMoves.Count()) data.PendingMoves.Capacity *= 2;
                data.PendingMoves[data.IdOf[slot]] = (float2)free + 0.5f;
            }
        }

        /// <summary>
        /// The nearest free floor tile to <paramref name="at"/> (searching through blocked tiles). A large unit needs a spot with
        /// its clearance (no rock or wall within a tile), not just a free tile; clearance is part of the goal, not the walk, since
        /// the tiles next to a new wall or footprint have none. With no clear spot near, a free tile without the clearance still
        /// beats staying inside a wall or a building.
        /// </summary>
        private static bool TryFindFreeTile(SimData data, SimContext context, int2 at, bool large, out int2 free)
        {
            WAR2D.World.MapStore map = context.Map;
            WAR2D.World.MapGrid grid = map.Grid;
            return TileSearch.FindNearest(at, t => map.IsWalkable(t) && (!large || data.LargeGrid[t.y * data.Width + t.x] == 0), t => TileRules.IsFloor(grid.TileAt(t)), 4096, out free) ||
                   (large && TileSearch.FindNearest(at, t => map.IsWalkable(t), t => TileRules.IsFloor(grid.TileAt(t)), 4096, out free));
        }

        private void CreateBuilding(SimContext context, in BuildingSpec spec)
        {
            BuildingData buildingData = spec.Data;
            BuildingConfig buildingConfig = spec.Config;
            context.SlotOf(buildingData.ownerId); // gives the owner a slot, and so a team
            Entity building = EntityManager.CreateEntity();
            EntityManager.AddComponentData(building, buildingData);
            EntityManager.AddComponentData(building, new LocalTransform
            {
                Position = new float3(buildingData.position.x, buildingData.position.y, 0),
                Rotation = quaternion.Euler(0, 0, math.radians(spec.Rotation)),
                Scale = 1f
            });

            // The HQ is complete at once and pays its running cost; every other building starts as a
            // blueprint (one health, no upkeep until Builders finish it).
            bool blueprint = buildingData.buildingType != BuildingType.Base && !spec.Complete;
            if (blueprint) EntityManager.AddComponentData(building, new Construction { Progress = 0f });
            else
                EntityManager.AddComponentData(building, new UpkeepComponent
                {
                    ownerId = buildingData.ownerId,
                    runningCostPerSecond = buildingConfig.RunningCost,
                });
            EntityManager.AddComponentData(building, new HealthComponent
            {
                entityId = buildingData.id,
                currentHealth = blueprint ? ConstructionRules.StartHealth : buildingConfig.Health,
                maxHealth = buildingConfig.Health
            });
            context.SetBuildingTiles(buildingData.id, Footprint.Tiles((int2)math.round(buildingData.position), buildingConfig.Size));

            switch (buildingData.buildingType)
            {
                case BuildingType.Base:
                    EntityManager.AddComponentData(building, new HQComponent { ownerId = buildingData.ownerId });
                    // The HQ produces Builders.
                    EntityManager.AddComponentData(building, new SpawnerData { ownerId = buildingData.ownerId, position = buildingData.position, unitType = (byte)UnitType.Builder });
                    break;
                case BuildingType.Miner:
                    EntityManager.AddComponentData(building, new MiningComponent { timeSinceLastMining = 0f, isActive = false });
                    break;
                case BuildingType.Barracks:
                case BuildingType.HeavyFactory:
                case BuildingType.SiegeWorkshop:
                    EntityManager.AddComponentData(building, new SpawnerData
                    {
                        ownerId = buildingData.ownerId,
                        position = buildingData.position,
                        unitType = (byte)SpawnerRules.DefaultUnit(buildingData.buildingType),
                    });
                    break;
                case BuildingType.Mine:
                case BuildingType.Radar:
                case BuildingType.GunTurret:
                    break; // no components of their own: their stages read the building's slot flags (the turret's weapon is added below)
                case BuildingType.Jammer:
                    EntityManager.AddComponent<Jammer>(building);
                    break;
                default:
                    WarLog.Error(LogCategory.Sim, $"no components for building type {buildingData.buildingType}");
                    break;
            }

            // Any building with a weapon in the config attacks once complete (v0.13: the Gun turret).
            if (buildingConfig.IsAttacker) EntityManager.AddComponentData(building, new BuildingWeapon { TargetId = -1 });

            context.RaiseBuildingCreated(buildingData.id, building);
        }
    }

    /// <summary>The unit slots whose bodies overlap an inclusive tile box (<see cref="SimCommandSystem"/>'s push off new walls and footprints).</summary>
    [Unity.Burst.BurstCompile]
    public struct OverlapTilesJob : Unity.Jobs.IJob
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Radius;
        public int Count;
        public int2 Lo, Hi;
        public NativeList<int> Hits;

        public void Execute()
        {
            for (int slot = 0; slot < Count; slot++)
            {
                float2 p = Positions[slot];
                float r = Radius[slot];
                if (p.x + r < Lo.x || p.y + r < Lo.y || p.x - r > Hi.x + 1 || p.y - r > Hi.y + 1) continue;
                Hits.Add(slot);
            }
        }
    }
}

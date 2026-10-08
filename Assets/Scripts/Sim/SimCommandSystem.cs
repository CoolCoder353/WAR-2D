using System.Collections.Generic;
using Config;
using Unity.Collections;
using Unity.Entities;
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
        private readonly List<SimCommand> deferred = new List<SimCommand>();

        protected override void OnCreate()
        {
            RequireForUpdate<SimData>();
            unitArchetype = EntityManager.CreateArchetype(typeof(Unit));
            unitQuery = GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        /// <summary>Drops per-match state when a match's simulation is disposed.</summary>
        internal void ResetForNewMatch()
        {
            deferred.Clear();
        }

        protected override void OnUpdate()
        {
            SimContext context = SimContext.Current;
            if (context == null) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            SimClock clock = SystemAPI.GetSingleton<SimClock>();

            WriteBackBuildingDamage(data);
            data.PendingOrders.Clear();
            data.PendingMoves.Clear();

            List<SimCommand> commands = context.Commands.Drain();
            deferred.Clear();
            foreach (SimCommand command in commands)
            {
                switch (command.Kind)
                {
                    case SimCommandKind.CreateBuilding:
                        CreateBuilding(context, command.Building);
                        PushUnitsOutOf(data, context, command.Building, clock.UnitCount);
                        break;
                    case SimCommandKind.KillOwner:
                        KillUnits(data, context, command.OwnerId, all: false);
                        break;
                    case SimCommandKind.DestroyAll:
                        KillUnits(data, context, 0, all: true);
                        break;
                    case SimCommandKind.SpawnUnit:
                        if (clock.Running) Spawn(data, context, command);
                        else deferred.Add(command);
                        break;
                    case SimCommandKind.MoveUnits:
                        if (clock.Running) Move(data, context, command, clock.UnitCount);
                        else deferred.Add(command);
                        break;
                }
            }
            if (deferred.Count > 0) context.Commands.Requeue(deferred);
        }

        /// <summary>Applies the damage the last tick's attacks dealt to buildings.</summary>
        private void WriteBackBuildingDamage(SimData data)
        {
            int count = data.BuildingCount[0];
            for (int b = 0; b < count; b++)
            {
                float damage = data.BuildingDamage[b];
                if (damage <= 0f) continue;
                data.BuildingDamage[b] = 0f;
                Entity entity = data.BuildingEntities[b];
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<HealthComponent>(entity)) continue;
                HealthComponent health = EntityManager.GetComponentData<HealthComponent>(entity);
                health.currentHealth = math.max(0f, health.currentHealth - damage);
                EntityManager.SetComponentData(entity, health);
            }
        }

        private void Spawn(SimData data, SimContext context, in SimCommand command)
        {
            int slot = context.SlotOf(command.OwnerId);
            if (slot < 0 || !context.Config.Units.TryGetValue(command.UnitType, out UnitConfig unit))
            {
                command.OnSpawned?.Invoke(-1);
                return;
            }
            if (data.UnitsBySlot[slot] >= data.MaxUnitsPerPlayer || context.Ids.Capacity <= 0)
            {
                command.OnSpawned?.Invoke(-1);
                return;
            }
            int id;
            try { id = context.Ids.Allocate(); }
            catch (System.InvalidOperationException)
            {
                command.OnSpawned?.Invoke(-1);
                return;
            }
            data.UnitsBySlot[slot]++;
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
                Position = command.Position,
                TargetId = -1,
                OrderSlot = -1,
            });
            command.OnSpawned?.Invoke(id);
        }

        /// <summary>Order handling arrives with the flow fields (Task 8).</summary>
        partial void ApplyMove(SimData data, SimContext context, in SimCommand command, int unitCount);

        private void Move(SimData data, SimContext context, in SimCommand command, int unitCount)
            => ApplyMove(data, context, command, unitCount);

        /// <summary>Destroys every unit of the owner (or every unit), recording each death for its explosion.</summary>
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
            WAR2D.World.MapGrid grid = context.Map.Grid;
            for (int slot = 0; slot < unitCount; slot++)
            {
                float2 p = data.Positions[slot];
                float r = data.Radius[slot];
                if (p.x + r < lo.x || p.y + r < lo.y || p.x - r > hi.x + 1 || p.y - r > hi.y + 1) continue;
                if (!TileSearch.FindNearest((int2)math.floor(p), grid.IsWalkable, t => grid.TileAt(t) == TileType.Ground, 4096, out int2 free)) continue;
                if (data.PendingMoves.Capacity <= data.PendingMoves.Count()) data.PendingMoves.Capacity *= 2;
                data.PendingMoves[data.IdOf[slot]] = (float2)free + 0.5f;
            }
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

            // Every building pays its running cost; unpaid buildings decay.
            EntityManager.AddComponentData(building, new UpkeepComponent
            {
                ownerId = buildingData.ownerId,
                runningCostPerSecond = buildingConfig.RunningCost,
            });
            EntityManager.AddComponentData(building, new HealthComponent
            {
                entityId = buildingData.id,
                currentHealth = buildingConfig.Health,
                maxHealth = buildingConfig.Health
            });

            switch (buildingData.buildingType)
            {
                case BuildingType.Base:
                    EntityManager.AddComponentData(building, new HQComponent { ownerId = buildingData.ownerId });
                    break;
                case BuildingType.Miner:
                    EntityManager.AddComponentData(building, new MiningComponent { timeSinceLastMining = 0f, isActive = false });
                    break;
                case BuildingType.SmallUnitSpawner:
                    EntityManager.AddComponentData(building, new SpawnerData
                    {
                        count = 0,
                        ownerId = buildingData.ownerId,
                        position = buildingData.position,
                        unitType = UnitType.Tank,
                        spawnRate = buildingConfig.SpawnRate
                    });
                    break;
                default:
                    Debug.LogError($"[Sim] no components for building type {buildingData.buildingType}");
                    break;
            }

            context.RaiseBuildingCreated(buildingData.id, building);
        }
    }
}

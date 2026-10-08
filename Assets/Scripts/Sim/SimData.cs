using System;
using Config;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.World;

namespace WAR2D.Sim
{
    /// <summary>A unit removed at a boundary: the main thread frees its id and records its explosion.</summary>
    public struct DeathRecord
    {
        public Entity Entity;
        public int Id;
        public float2 Position;
    }

    /// <summary>
    /// Everything the tick's stages share, as one singleton component: the map, the per-type balance
    /// tables and the SoA scratch arrays the stages run over. Every array is sized once at creation and
    /// never grows, so jobs may hold them across ticks.
    ///
    /// <para>Unit slots are dense: the gather visits the live units in query order and writes slot i of
    /// every array for the same unit, and <see cref="SimClock.UnitCount"/> bounds every stage, so slots
    /// past the live count are never read.</para>
    ///
    /// <para>The dependency manager tracks components, not the arrays inside this one, so every stage
    /// declares write access to <see cref="Unit"/>; that orders the stages' jobs one after another.</para>
    /// </summary>
    public struct SimData : IComponentData
    {
        /// <summary>Owner slots in every per-owner table.</summary>
        public const int MaxOwners = 16;

        // ---- sizes ----
        /// <summary>Unit slots in every SoA array.</summary>
        public int Capacity;
        /// <summary>Length of the by-id-index arrays (<c>NetIdAllocator.IndexOf(id)</c> is below this).</summary>
        public int IdCapacity;
        /// <summary>Building slots in the building target arrays.</summary>
        public int BuildingCapacity;
        /// <summary>Entries in the per-type tables (<c>UnitType</c> values).</summary>
        public int TypeCount;

        // ---- rules ----
        /// <summary>Seconds per tick.</summary>
        public float Dt;
        /// <summary>Ticks per second.</summary>
        public int TickRate;
        /// <summary>Ticks between two target searches of the same idle unit.</summary>
        public int Slice;
        /// <summary>Ticks between separation passes for a unit.</summary>
        public int SeparationInterval;
        /// <summary>Separation strength k.</summary>
        public float SeparationStrength;
        /// <summary>The largest body radius, so a separation scan reaches every possible neighbour.</summary>
        public float SeparationMargin;
        /// <summary>Percent of max health an unpaid unit loses per second.</summary>
        public float DecayPercentPerSecond;
        /// <summary>Most units one owner may have.</summary>
        public int MaxUnitsPerPlayer;

        // ---- spatial hash ----
        /// <summary>Cell size in tiles, and the grid's dimensions.</summary>
        public int CellSize, CellsX, CellsY;
        /// <summary>Cells a search reaches each way to cover the longest attack range.</summary>
        public int SearchCells;

        // ---- map (not owned: MapStore owns Tiles and Used) ----
        /// <summary>Tiles across and down.</summary>
        public int Width, Height;
        /// <summary>Tile kinds, row-major (see <see cref="MapGrid"/>).</summary>
        public NativeArray<byte> Tiles;
        /// <summary>Building footprint flags, row-major.</summary>
        public NativeArray<byte> Used;

        // ---- per-type tables ----
        /// <summary>Tiles per second per unit type.</summary>
        public NativeArray<float> SpeedByType;
        /// <summary>Squared attack range per unit type.</summary>
        public NativeArray<float> RangeSqByType;
        /// <summary>Damage per attack per unit type.</summary>
        public NativeArray<float> DamageByType;
        /// <summary>Seconds between attacks per unit type.</summary>
        public NativeArray<float> CooldownByType;
        /// <summary>Upkeep per second per unit type.</summary>
        public NativeArray<float> RunningCostByType;
        /// <summary>Damage multipliers, <c>type * 3 + (int)TargetClass</c> (see <see cref="DamageTable"/>).</summary>
        public NativeArray<float> DamageTable;

        // ---- unit SoA ----
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocity;
        public NativeArray<int> OwnerId;
        public NativeArray<byte> OwnerSlot;
        public NativeArray<byte> Type;
        public NativeArray<byte> SizeClass;
        public NativeArray<float> Health;
        public NativeArray<float> MaxHealth;
        public NativeArray<float> Radius;
        public NativeArray<float> Cooldown;
        /// <summary>Order slot per unit, or -1.</summary>
        public NativeArray<int> OrderSlot;
        /// <summary>Set for a unit that reached its goal this tick: its order is complete.</summary>
        public NativeArray<byte> Arrived;
        /// <summary>1 when the unit's upkeep went unpaid at the last charge.</summary>
        public NativeArray<byte> Unpaid;
        /// <summary>Network id per unit slot.</summary>
        public NativeArray<int> IdOf;
        /// <summary>Slot by id index; valid only while <c>IdOf[slot] == id</c>.</summary>
        public NativeArray<int> IndexOfId;
        /// <summary>Hash cell per unit.</summary>
        public NativeArray<int> Cell;
        /// <summary>Counting-sort offsets: cell c's units are <c>Sorted[CellStart[c] .. CellStart[c + 1])</c>.</summary>
        public NativeArray<int> CellStart;
        /// <summary>Unit slots grouped by cell.</summary>
        public NativeArray<int> Sorted;
        /// <summary>Combat target per unit: a stable id from the gather, a slot after resolution, or -1.</summary>
        public NativeArray<int> Target;
        /// <summary>What <see cref="Target"/> names: 0 a unit slot, 1 a building slot.</summary>
        public NativeArray<byte> TargetKind;

        // ---- building targets (gathered on the main thread each tick) ----
        /// <summary>Live building slots this tick, at index 0.</summary>
        public NativeArray<int> BuildingCount;
        public NativeArray<float2> BuildingPositions;
        public NativeArray<int> BuildingOwnerId;
        public NativeArray<float> BuildingHealth;
        /// <summary>Damage dealt to each building slot this tick; written back to the entity at the next boundary.</summary>
        public NativeArray<float> BuildingDamage;
        public NativeArray<int> BuildingIds;
        /// <summary>The entity behind each building slot, for the damage write-back.</summary>
        public NativeArray<Entity> BuildingEntities;
        /// <summary>Building slot by id index; valid only while <c>BuildingIds[slot] == id</c>.</summary>
        public NativeArray<int> BuildingSlotOfIndex;
        public NativeArray<int> BuildingCell;
        public NativeArray<int> BuildingCellStart;
        public NativeArray<int> BuildingSorted;

        // ---- events and edits ----
        /// <summary>(attackerId, targetId) per attack this tick, for clients' tracers.</summary>
        public NativeList<int2> AttackEvents;
        /// <summary>Units the lifecycle found dead this tick.</summary>
        public NativeQueue<DeathRecord> Deaths;
        /// <summary>Position to move a unit to (out of a new building's footprint), applied by the next gather.</summary>
        public NativeParallelHashMap<int, float2> PendingMoves;
        /// <summary>Order slot to assign per unit id, applied by the next gather.</summary>
        public NativeParallelHashMap<int, int> PendingOrders;

        // ---- orders (owned by OrderBook; see SimContext) ----
        /// <summary>Flow direction table: (handle, sector) → block, and the blocks.</summary>
        public WAR2D.Pathing.OrderFieldTable Orders;
        /// <summary>Per order handle: goal centre, live flag, ready flag and follower count.</summary>
        public NativeArray<float2> OrderGoal;
        public NativeArray<byte> OrderLive;
        public NativeArray<byte> OrderReady;
        public NativeArray<int> OrderFollowers;
        /// <summary>(handle, sector) pairs where a unit of a ready order found no direction.</summary>
        public NativeQueue<int2> RouteMisses;
        /// <summary>Non-zero where a large unit may not stand (a tile next to a blocked tile).</summary>
        public NativeArray<byte> LargeGrid;

        // ---- economy ----
        /// <summary>Owner id per slot, or 0 when the slot is free.</summary>
        public NativeArray<int> OwnerIdBySlot;
        /// <summary>Resources each owner has for this second's upkeep, written at the boundary.</summary>
        public NativeArray<float> UpkeepBudget;
        /// <summary>Resources each owner's units spent at the last charge, read back at the boundary.</summary>
        public NativeArray<float> UpkeepSpent;
        /// <summary>Live units per owner slot, counted every tick.</summary>
        public NativeArray<int> UnitsBySlot;
        /// <summary>1 on the tick an upkeep charge ran, so the boundary applies the spend once.</summary>
        public NativeArray<byte> ChargedThisTick;

        /// <summary>Allocates every array for a map and config.</summary>
        public static SimData Create(in MapGrid map, GameConfigData config, Allocator allocator)
        {
            SimulationConfig sim = config.Simulation;
            int typeCount = Enum.GetValues(typeof(UnitType)).Length;
            float maxRange = 0f, maxRadius = 0.1f;
            foreach (UnitConfig unit in config.Units.Values)
            {
                maxRange = math.max(maxRange, unit.Range);
                maxRadius = math.max(maxRadius, unit.Radius);
            }
            int capacity = sim.MaxEntities;
            int buildingCapacity = 4096;
            int cellsX = (map.Width + sim.HashCellSize - 1) / sim.HashCellSize;
            int cellsY = (map.Height + sim.HashCellSize - 1) / sim.HashCellSize;
            int cells = cellsX * cellsY;

            var data = new SimData
            {
                Capacity = capacity,
                IdCapacity = sim.MaxEntities,
                BuildingCapacity = buildingCapacity,
                TypeCount = typeCount,
                Dt = sim.TickSeconds,
                TickRate = sim.TickRate,
                Slice = math.max(1, sim.TargetSearchSliceTicks),
                SeparationInterval = math.max(1, sim.SeparationIntervalTicks),
                SeparationStrength = sim.SeparationStrength,
                SeparationMargin = maxRadius,
                DecayPercentPerSecond = config.Resources.DecayPercentPerSecond,
                MaxUnitsPerPlayer = sim.MaxUnitsPerPlayer,
                CellSize = sim.HashCellSize,
                CellsX = cellsX,
                CellsY = cellsY,
                SearchCells = math.max(1, (int)math.ceil(maxRange / sim.HashCellSize)),
                Width = map.Width,
                Height = map.Height,
                Tiles = map.Tiles,
                Used = map.Used,
                SpeedByType = new NativeArray<float>(typeCount, allocator),
                RangeSqByType = new NativeArray<float>(typeCount, allocator),
                DamageByType = new NativeArray<float>(typeCount, allocator),
                CooldownByType = new NativeArray<float>(typeCount, allocator),
                RunningCostByType = new NativeArray<float>(typeCount, allocator),
                DamageTable = WAR2D.Rules.DamageTable.Build(config.Damage, typeCount, allocator),
                Positions = new NativeArray<float2>(capacity, allocator),
                Velocity = new NativeArray<float2>(capacity, allocator),
                OwnerId = new NativeArray<int>(capacity, allocator),
                OwnerSlot = new NativeArray<byte>(capacity, allocator),
                Type = new NativeArray<byte>(capacity, allocator),
                SizeClass = new NativeArray<byte>(capacity, allocator),
                Health = new NativeArray<float>(capacity, allocator),
                MaxHealth = new NativeArray<float>(capacity, allocator),
                Radius = new NativeArray<float>(capacity, allocator),
                Cooldown = new NativeArray<float>(capacity, allocator),
                OrderSlot = new NativeArray<int>(capacity, allocator),
                Arrived = new NativeArray<byte>(capacity, allocator),
                Unpaid = new NativeArray<byte>(capacity, allocator),
                IdOf = new NativeArray<int>(capacity, allocator),
                IndexOfId = new NativeArray<int>(sim.MaxEntities, allocator),
                Cell = new NativeArray<int>(capacity, allocator),
                CellStart = new NativeArray<int>(cells + 1, allocator),
                Sorted = new NativeArray<int>(capacity, allocator),
                Target = new NativeArray<int>(capacity, allocator),
                TargetKind = new NativeArray<byte>(capacity, allocator),
                BuildingCount = new NativeArray<int>(1, allocator),
                BuildingPositions = new NativeArray<float2>(buildingCapacity, allocator),
                BuildingOwnerId = new NativeArray<int>(buildingCapacity, allocator),
                BuildingHealth = new NativeArray<float>(buildingCapacity, allocator),
                BuildingDamage = new NativeArray<float>(buildingCapacity, allocator),
                BuildingIds = new NativeArray<int>(buildingCapacity, allocator),
                BuildingEntities = new NativeArray<Entity>(buildingCapacity, allocator),
                BuildingSlotOfIndex = new NativeArray<int>(sim.MaxEntities, allocator),
                BuildingCell = new NativeArray<int>(buildingCapacity, allocator),
                BuildingCellStart = new NativeArray<int>(cells + 1, allocator),
                BuildingSorted = new NativeArray<int>(buildingCapacity, allocator),
                AttackEvents = new NativeList<int2>(1024, allocator),
                Deaths = new NativeQueue<DeathRecord>(allocator),
                PendingOrders = new NativeParallelHashMap<int, int>(1024, allocator),
                PendingMoves = new NativeParallelHashMap<int, float2>(64, allocator),
                OwnerIdBySlot = new NativeArray<int>(MaxOwners, allocator),
                UpkeepBudget = new NativeArray<float>(MaxOwners, allocator),
                UpkeepSpent = new NativeArray<float>(MaxOwners, allocator),
                UnitsBySlot = new NativeArray<int>(MaxOwners, allocator),
                ChargedThisTick = new NativeArray<byte>(1, allocator),
                LargeGrid = BuildLargeGrid(map, allocator),
            };

            foreach (var pair in config.Units)
            {
                int t = (int)pair.Key;
                if (t >= typeCount) continue;
                data.SpeedByType[t] = pair.Value.MoveSpeed;
                data.RangeSqByType[t] = pair.Value.Range * pair.Value.Range;
                data.DamageByType[t] = pair.Value.Damage;
                data.CooldownByType[t] = pair.Value.AttackInterval;
                data.RunningCostByType[t] = pair.Value.RunningCost;
            }
            return data;
        }

        /// <summary>Frees every array this struct owns (not the map's).</summary>
        public void Dispose()
        {
            SpeedByType.Dispose(); RangeSqByType.Dispose(); DamageByType.Dispose(); CooldownByType.Dispose();
            RunningCostByType.Dispose(); DamageTable.Dispose();
            Positions.Dispose(); Velocity.Dispose(); OwnerId.Dispose(); OwnerSlot.Dispose(); Type.Dispose();
            SizeClass.Dispose(); Health.Dispose(); MaxHealth.Dispose(); Radius.Dispose(); Cooldown.Dispose();
            OrderSlot.Dispose(); Arrived.Dispose(); Unpaid.Dispose(); IdOf.Dispose(); IndexOfId.Dispose();
            Cell.Dispose(); CellStart.Dispose(); Sorted.Dispose(); Target.Dispose(); TargetKind.Dispose();
            BuildingCount.Dispose(); BuildingPositions.Dispose(); BuildingOwnerId.Dispose(); BuildingHealth.Dispose();
            BuildingDamage.Dispose(); BuildingIds.Dispose(); BuildingEntities.Dispose(); BuildingSlotOfIndex.Dispose(); BuildingCell.Dispose();
            BuildingCellStart.Dispose(); BuildingSorted.Dispose();
            AttackEvents.Dispose(); Deaths.Dispose(); PendingOrders.Dispose(); PendingMoves.Dispose();
            OwnerIdBySlot.Dispose(); UpkeepBudget.Dispose(); UpkeepSpent.Dispose(); UnitsBySlot.Dispose();
            ChargedThisTick.Dispose(); LargeGrid.Dispose();
        }

        private static NativeArray<byte> BuildLargeGrid(in MapGrid map, Allocator allocator)
        {
            var grid = new NativeArray<byte>(map.Width * map.Height, allocator);
            for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                byte value = 0;
                for (int dy = -1; dy <= 1 && value == 0; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (map.TileAt(new int2(x + dx, y + dy)) != TileType.Ground) { value = 1; break; }
                grid[y * map.Width + x] = value;
            }
            return grid;
        }

        /// <summary>
        /// Slot of a live unit id, or -1. Only valid on the main thread right after a boundary, or inside
        /// the tick's jobs after the gather.
        /// </summary>
        public int SlotOf(int id, int unitCount)
        {
            if (id <= 0) return -1;
            int index = NetIdAllocator.IndexOf(id);
            if (index >= IndexOfId.Length) return -1;
            int slot = IndexOfId[index];
            return (uint)slot < (uint)unitCount && IdOf[slot] == id ? slot : -1;
        }
    }
}

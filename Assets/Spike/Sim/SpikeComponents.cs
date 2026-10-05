using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// The spike's unit: the plan's one-component layout (about 48 bytes). The tick copies it into
    /// <see cref="SpikeWorldData"/>'s SoA scratch (the gather), runs every stage over those arrays,
    /// then writes the moving parts back.
    /// </summary>
    public struct SpikeUnit : IComponentData
    {
        /// <summary>Stable id, allocated from a monotonic counter that never reuses a value.</summary>
        public int Id;

        /// <summary>Player index (0 to <see cref="SpikeWorldData.Players"/> - 1).</summary>
        public byte Owner;

        /// <summary>Team index; anything on another team is an enemy.</summary>
        public byte Team;

        /// <summary>Unit type; the spike has one, so this stays 0.</summary>
        public byte Type;

        /// <summary>Size class: 0 small (<see cref="SpikeScenario.SmallRadius"/>), 1 large (0.7).</summary>
        public byte SizeClass;

        /// <summary>Body radius in tiles.</summary>
        public float Radius;

        /// <summary>Health in hit points; 0 (never negative) means dead and due for removal.</summary>
        public float Health;

        /// <summary>Body centre, in tile coordinates.</summary>
        public float2 Position;

        /// <summary>Tiles per second; the movement stage's output.</summary>
        public float2 Velocity;

        /// <summary>Stable id of the enemy this unit is attacking, or -1.</summary>
        public int TargetId;

        /// <summary>Seconds until this unit can attack again.</summary>
        public float Cooldown;

        /// <summary>Index of the order slot this unit follows, or -1. See <see cref="SpikeWorldData.OrderBase"/>.</summary>
        public int FieldGoal;
    }

    /// <summary>
    /// The spike's balance and behaviour constants. They mirror the v0.3 plan's reference table
    /// (today's Tank) rather than <c>GameConfig.xml</c>: the spike is a standalone measurement and
    /// deliberately does not read the game's config.
    /// </summary>
    public static class SpikeSimRules
    {
        /// <summary>Ticks per second, the spike's simulation rate.</summary>
        public const float TickSeconds = 1f / 20f;

        /// <summary>Move speed in tiles per second.</summary>
        public const float Speed = 5f;

        /// <summary>Attack range in tiles.</summary>
        public const float AttackRange = 5f;

        /// <summary>Damage per attack.</summary>
        public const float AttackDamage = 10f;

        /// <summary>Seconds between attacks.</summary>
        public const float AttackCooldown = 1f;

        /// <summary>Ticks between two searches of the same unit (the plan's slice).</summary>
        public const int Slice = 4;

        /// <summary>Hash cell size in tiles; the plan's sweep and every swept cell size is above 2 x 0.7.</summary>
        public const int CellSize = 5;

        /// <summary>Units the lifecycle spawns per player per tick while that army is below its target.</summary>
        public const int SpawnPerTick = 20;

        /// <summary>
        /// Separation strength k. It scales the per-tick push: each side takes
        /// <c>k * share * overlap</c> of it, so two equal units at k = 1 separate by exactly their
        /// overlap in one tick and the crowd settles with the overlap the 10 % tolerance allows.
        /// </summary>
        public const float SeparationStrength = 1f;

        /// <summary>Separation passes per tick (the plan allows up to two).</summary>
        public const int SeparationIterations = 1;

        /// <summary>
        /// The largest body radius the spike simulates (<see cref="SpikeScenario.LargeRadius"/>), used
        /// as the separation scan's reach: a unit's neighbours can be at most
        /// <c>radius + SeparationMargin</c> tiles away.
        /// </summary>
        public const float SeparationMargin = SpikeScenario.LargeRadius;
    }

    /// <summary>
    /// Everything the spike's systems share, as one singleton component: the map's tile grid, the
    /// prebuilt order fields and the SoA scratch arrays the stages run over. Every array is sized once
    /// at creation and never grows, so jobs may hold them across ticks without watching for a
    /// reallocation.
    ///
    /// <para>Unit slots are dense: the ECS query visits the live units in the same order the gather
    /// writes them, so slot i of every array is the same unit. Slots at or beyond the live count hold
    /// the previous tick's data: a destroyed unit's reads as dead and every stage skips it, and one
    /// whose unit has moved to a lower slot can pass for that unit for a single tick (a small phantom
    /// attacker or neighbour, never a corrupting write - every write-back is keyed by the entity's own
    /// query index).</para>
    /// </summary>
    public struct SpikeWorldData : IComponentData
    {
        // ---- the match ----
        /// <summary>Armies in the run.</summary>
        public int Players;

        /// <summary>Unit slots in every SoA array; the peak live count, fixed for the run.</summary>
        public int Capacity;

        /// <summary>Ids are allocated below this, so <see cref="IndexOfId"/> is exactly this long.</summary>
        public int IdCapacity;

        /// <summary>The order slot table's length.</summary>
        public int OrderCount;

        /// <summary>The order slot table's capacity.</summary>
        public int OrderCapacity;

        // ---- balance ----
        /// <summary>Seconds per tick (0.05).</summary>
        public float Dt;
        /// <summary>Move speed, tiles per second.</summary>
        public float Speed;
        /// <summary>Attack range in tiles, and its square.</summary>
        public float AttackRange, AttackRangeSq;
        /// <summary>Damage per attack and the fixed-point scale the damage queue carries (x100).</summary>
        public float AttackDamage;
        /// <summary>Seconds between attacks.</summary>
        public float AttackCooldown;
        /// <summary>Separation strength k.</summary>
        public float SeparationStrength;
        /// <summary>Separation passes per tick.</summary>
        public int SeparationIterations;
        /// <summary>
        /// Ticks between separation passes for a unit. 1 is every tick; with 2, half the units
        /// separate each tick (staggered by slot, like the target search), so every unit is served
        /// every other tick and the per-tick cost is halved.
        /// </summary>
        public int SeparationInterval;
        /// <summary>True when integration runs in two halves on alternating ticks (the plan's ladder step 3).</summary>
        public bool MoveHalves;

        /// <summary>Ticks between two searches of the same unit.</summary>
        public int Slice;

        // ---- the spatial hash ----
        /// <summary>Cell size in tiles, and the grid's dimensions.</summary>
        public int CellSize, CellsX, CellsY;
        /// <summary>Cells a search reaches each way to cover <see cref="AttackRange"/>.</summary>
        public int SearchCells;

        // ---- lifecycle ----
        /// <summary>Units spawned per player per tick while the army is below its target; 0 disables spawning.</summary>
        public int SpawnPerTick;
        /// <summary>Live units each army is kept around; 0 disables spawning.</summary>
        public int SpawnTarget;
        /// <summary>Health a spawned unit starts with.</summary>
        public float SpawnHealth;
        /// <summary>The archetype spawned units are created from.</summary>
        public EntityArchetype UnitArchetype;
        /// <summary>True when the run measures the asynchronous schedule: the caller does not complete at the end of a tick.</summary>
        public bool Async;
        /// <summary>
        /// True while the tick group is timing each stage's completion: the lifecycle then leaves the
        /// completion and playback to the group, so the stage timings cover the tick's own jobs.
        /// </summary>
        public bool Breakdown;

        // ---- the map ----
        /// <summary>Tiles across and down (square).</summary>
        public int Width, Height;
        /// <summary>The map's tile grid; the sim reads it, never owns it.</summary>
        public NativeArray<byte> Tiles;
        /// <summary>
        /// Non-zero where a large unit may not stand: a floor tile whose 3 x 3 neighbourhood has a
        /// blocked tile. It is the same predicate the large flow field paths on, built once at
        /// creation (the bench does not change terrain mid-run), so a large unit is never separated
        /// into a tile its own field cannot leave.
        /// </summary>
        public NativeArray<byte> LargeGrid;

        // ---- orders ----
        /// <summary>
        /// Every order's direction grid, concatenated: order o's direction for cell c is
        /// <c>Directions[OrderBase[o] + c]</c>, with <c>c = y * Width + x</c>. Filled from the flow
        /// cache before the tick loop (outside the measured section) and never during it.
        /// </summary>
        public NativeArray<byte> Directions;
        /// <summary>Offset of each order's direction grid in <see cref="Directions"/>.</summary>
        public NativeArray<int> OrderBase;
        /// <summary>The flow-field cache handle each order was copied from, or -1 (see <see cref="SpikeSim"/>).</summary>
        public NativeArray<int> OrderHandle;
        /// <summary>Order slot per (player * 2 + size class), or -1 when that army/class has no order.</summary>
        public NativeArray<int> PlayerSlot;

        // ---- SoA scratch ----
        /// <summary>Position per unit.</summary>
        public NativeArray<float2> Positions;
        /// <summary>Velocity per unit, written by movement.</summary>
        public NativeArray<float2> Velocity;
        /// <summary>Team per unit.</summary>
        public NativeArray<byte> Team;
        /// <summary>Size class per unit.</summary>
        public NativeArray<byte> SizeClass;
        /// <summary>Health per unit; damage clamps it at 0.</summary>
        public NativeArray<float> Health;
        /// <summary>Body radius per unit.</summary>
        public NativeArray<float> Radius;
        /// <summary>Squared attack range per unit (a constant at creation).</summary>
        public NativeArray<float> RangeSq;
        /// <summary>Seconds until each unit can attack again.</summary>
        public NativeArray<float> Cooldown;
        /// <summary>Order slot per unit, or -1.</summary>
        public NativeArray<int> FieldGoal;
        /// <summary>Set for a unit that reached a goal cell this tick: its order is complete.</summary>
        public NativeArray<byte> Arrived;
        /// <summary>Stable id per unit.</summary>
        public NativeArray<int> IdOf;
        /// <summary>Reverse map: <c>IndexOfId[id]</c> is the unit's slot, or -1.</summary>
        public NativeArray<int> IndexOfId;
        /// <summary>Hash cell per unit.</summary>
        public NativeArray<int> Cell;
        /// <summary>Counting sort offsets: cell c's units are <c>Sorted[CellStart[c] .. CellStart[c + 1])</c>.</summary>
        public NativeArray<int> CellStart;
        /// <summary>Unit slots grouped by cell.</summary>
        public NativeArray<int> Sorted;
        /// <summary>Combat's target per unit: a slot this tick, or -1.</summary>
        public NativeArray<int> Target;

        // ---- lifecycle counters ----
        /// <summary>Rotating spawn tile cursor per player.</summary>
        public NativeArray<int> SpawnCursor;
        /// <summary>First tile of each player's spawn list in <see cref="SpawnTiles"/>.</summary>
        public NativeArray<int> SpawnBase;
        /// <summary>Tiles in each player's spawn list.</summary>
        public NativeArray<int> SpawnCount;
        /// <summary>Free tiles around each player's HQ, concatenated.</summary>
        public NativeArray<int2> SpawnTiles;
        /// <summary>The monotonic id counter, at index 0; the spawn job is its only writer.</summary>
        public NativeArray<int> NextId;
    }

    /// <summary>Inputs for <see cref="SpikeSim.Create"/>: how big the run is and which knobs the bench sweeps.</summary>
    public struct SpikeSimConfig
    {
        /// <summary>Armies in the run.</summary>
        public int Players;

        /// <summary>Unit slots for the SoA arrays: the peak live count. At least the starting army.</summary>
        public int Capacity;

        /// <summary>Ids below this; the spawner stops when the counter reaches it.</summary>
        public int IdCapacity;

        /// <summary>Order slots to allocate direction grids for (goals x size classes in use).</summary>
        public int OrderCapacity;

        /// <summary>Units the lifecycle spawns per player per tick while below the target; 0 disables spawning.</summary>
        public int SpawnPerTick;

        /// <summary>Live units each army is kept around; 0 disables spawning.</summary>
        public int SpawnTarget;

        /// <summary>Health a spawned unit starts with.</summary>
        public float SpawnHealth;

        /// <summary>Ticks between two searches of the same unit.</summary>
        public int Slice;

        /// <summary>Hash cell size in tiles.</summary>
        public int CellSize;

        /// <summary>Separation strength k.</summary>
        public float SeparationStrength;

        /// <summary>Separation passes per tick (1 or 2).</summary>
        public int SeparationIterations;

        /// <summary>Ticks between separation passes; 1 is every tick.</summary>
        public int SeparationInterval;

        /// <summary>Integrate half the units on alternating ticks.</summary>
        public bool MoveHalves;

        /// <summary>Measure the asynchronous schedule: the caller does not complete at the end of a tick.</summary>
        public bool Async;

        /// <summary>HQ of each player; spawn tiles are spiralled out from these.</summary>
        public NativeArray<int2> SpawnOrigins;

        /// <summary>The plan's reference scenario, as the sim's defaults.</summary>
        public static SpikeSimConfig Defaults => new SpikeSimConfig
        {
            Players = 8,
            Capacity = 80000,
            IdCapacity = 96000,
            OrderCapacity = 16,
            SpawnPerTick = SpikeSimRules.SpawnPerTick,
            SpawnTarget = 10000,
            SpawnHealth = 100f,
            Slice = SpikeSimRules.Slice,
            CellSize = SpikeSimRules.CellSize,
            SeparationStrength = SpikeSimRules.SeparationStrength,
            SeparationIterations = SpikeSimRules.SeparationIterations,
            SeparationInterval = 1,
            MoveHalves = false,
            Async = false,
        };
    }
}

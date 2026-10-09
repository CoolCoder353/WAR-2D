using Unity.Entities;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// A unit: the simulation's one component (about 60 bytes). Each tick the gather copies it into
    /// <see cref="SimData"/>'s SoA arrays, every stage runs over those arrays, and the stages write the
    /// moving parts back. Units are created only by <see cref="SimCommandSystem"/>.
    /// </summary>
    public struct Unit : IComponentData
    {
        /// <summary>Network id from <see cref="NetIdAllocator"/> (never an ECS Entity.Index).</summary>
        public int Id;

        /// <summary>Owning player's netId as int.</summary>
        public int OwnerId;

        /// <summary>The owner's slot (0..<see cref="SimData.MaxOwners"/> - 1) for per-owner tables.</summary>
        public byte OwnerSlot;

        /// <summary><c>(byte)UnitType</c>.</summary>
        public byte Type;

        /// <summary>Pathing size class: 0 small, 1 large.</summary>
        public byte SizeClass;

        /// <summary>What <see cref="TargetId"/> names: 0 a unit, 1 a building.</summary>
        public byte TargetKind;

        /// <summary>Body radius in tiles.</summary>
        public float Radius;

        /// <summary>Hit points; 0 means dead and due for removal at the next boundary.</summary>
        public float Health;

        /// <summary>Hit points when undamaged.</summary>
        public float MaxHealth;

        /// <summary>Body centre in tile coordinates.</summary>
        public float2 Position;

        /// <summary>Tiles per second, written by movement.</summary>
        public float2 Velocity;

        /// <summary>Network id of the unit or building being attacked, or -1.</summary>
        public int TargetId;

        /// <summary>Seconds until the next attack.</summary>
        public float Cooldown;

        /// <summary>Order slot this unit follows (<see cref="OrderBook"/>), or -1.</summary>
        public int OrderSlot;

        /// <summary>1 while this unit's upkeep went unpaid at the last charge.</summary>
        public byte Unpaid;

        /// <summary>One of <see cref="Stances"/>.</summary>
        public byte Stance;
    }

    /// <summary>The tick's clock and state, a singleton next to <see cref="SimData"/>.</summary>
    public struct SimClock : IComponentData
    {
        /// <summary>Ticks run since the sim was created (only counted while running).</summary>
        public int Tick;

        /// <summary>True while the match is in <c>GameState.Playing</c> on a server; the stages do nothing otherwise.</summary>
        public bool Running;

        /// <summary>Seconds per tick.</summary>
        public float Dt;

        /// <summary>Live unit entities this tick: the SoA slots <c>[0, UnitCount)</c> hold them.</summary>
        public int UnitCount;
    }

    /// <summary>Main-thread timings of the last tick, for the performance gate.</summary>
    public static class SimTiming
    {
        /// <summary>Main-thread milliseconds from the boundary's start to the last stage's return.</summary>
        public static double LastMainThreadMs;

        /// <summary>Milliseconds the boundary spent waiting for the previous tick's jobs.</summary>
        public static double LastBoundaryWaitMs;

        /// <summary>Frame on which the last boundary completed, so main-thread readers know the SoA is settled.</summary>
        public static int LastBoundaryFrame = -1;
    }
}

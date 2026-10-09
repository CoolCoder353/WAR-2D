using System.Collections.Generic;
using Config;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>What a <see cref="SimCommand"/> asks the tick to do.</summary>
    public enum SimCommandKind : byte
    {
        /// <summary>Create one unit of <see cref="SimCommand.UnitType"/> at <see cref="SimCommand.Position"/>.</summary>
        SpawnUnit,
        /// <summary>
        /// Give <see cref="SimCommand.Ids"/> (or the owner's units inside the box) the order
        /// <see cref="SimCommand.Order"/>; Move and AttackMove go to <see cref="SimCommand.Tile"/>.
        /// </summary>
        OrderUnits,
        /// <summary>Create the building in <see cref="SimCommand.Building"/>.</summary>
        CreateBuilding,
        /// <summary>Kill every unit of <see cref="SimCommand.OwnerId"/>.</summary>
        KillOwner,
        /// <summary>Kill every unit (match end).</summary>
        DestroyAll,
    }

    /// <summary>A building the command system creates; placement, payment and footprint are done already.</summary>
    public struct BuildingSpec
    {
        public BuildingData Data;
        public float Rotation;
        public BuildingConfig Config;
    }

    /// <summary>One request from main-thread code to the simulation, applied at the next tick boundary.</summary>
    public struct SimCommand
    {
        public SimCommandKind Kind;
        public int OwnerId;
        /// <summary>The order of an <see cref="SimCommandKind.OrderUnits"/> command.</summary>
        public OrderKind Order;
        /// <summary>Goal tile of a move.</summary>
        public int2 Tile;
        /// <summary>Spawn position.</summary>
        public float2 Position;
        public UnitType UnitType;
        /// <summary>Unit ids a move applies to; null to use the box.</summary>
        public int[] Ids;
        /// <summary>Inclusive box corners of a box move.</summary>
        public int2 BoxMin, BoxMax;
        public BuildingSpec Building;
        /// <summary>Called with the new unit's id after a spawn is applied (or -1 when refused).</summary>
        public System.Action<int> OnSpawned;
    }

    /// <summary>
    /// Main-thread code never touches unit entities between ticks: it queues commands here, and
    /// <see cref="SimCommandSystem"/> applies them right after the next boundary.
    /// </summary>
    public sealed class SimCommandQueue
    {
        private List<SimCommand> pending = new List<SimCommand>();
        private List<SimCommand> draining = new List<SimCommand>();

        /// <summary>The live match's queue, or null when no simulation exists.</summary>
        public static SimCommandQueue Instance => SimContext.Current?.Commands;

        /// <summary>Commands waiting for the next boundary.</summary>
        public int Count => pending.Count;

        /// <summary>Queues a command. Main thread only.</summary>
        public void Enqueue(in SimCommand command) => pending.Add(command);

        /// <summary>Takes every queued command; the returned list is reused by the next drain.</summary>
        internal List<SimCommand> Drain()
        {
            (pending, draining) = (draining, pending);
            pending.Clear();
            return draining;
        }

        /// <summary>Puts commands that could not be applied yet back at the front of the queue.</summary>
        internal void Requeue(List<SimCommand> commands) => pending.InsertRange(0, commands);
    }
}

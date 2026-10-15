namespace WAR2D.Sim
{
    /// <summary>What a unit does about enemies and its order. Stored on <see cref="Unit"/> and gathered to <see cref="SimData.Stance"/>.</summary>
    public static class Stances
    {
        /// <summary>No order: fights whatever comes in range and is only moved by separation.</summary>
        public const byte Idle = 0;
        /// <summary>Follows its order and ignores enemies until it arrives.</summary>
        public const byte Move = 1;
        /// <summary>Follows its order but stops to fight enemies in range.</summary>
        public const byte AttackMove = 2;
        /// <summary>Never moves (not even by separation); fights enemies in range.</summary>
        public const byte Hold = 3;
    }

    /// <summary>A queued order assignment, applied by the next gather.</summary>
    public struct PendingOrder
    {
        /// <summary>Order slot to follow, or -1 for no order (Stop and Hold).</summary>
        public int Slot;
        public byte Stance;
        /// <summary>Tile index of the work target (valid when <see cref="WorkKind"/> is not None).</summary>
        public int WorkTile;
        /// <summary>One of <see cref="WorkKinds"/>; every non-work order carries None, which clears work.</summary>
        public byte WorkKind;
    }

    /// <summary>What a Builder or Digger is working on (<see cref="Unit.WorkKind"/>).</summary>
    public static class WorkKinds
    {
        public const byte None = 0;
        public const byte Build = 1;
        public const byte Repair = 2;
        public const byte Dig = 3;
    }
}

/// <summary>The order a client gives its units (the wire value of <c>CmdOrderChunk</c> and <c>CmdOrderSquad</c>).</summary>
public enum OrderKind : byte
{
    Move = 0,
    AttackMove = 1,
    Hold = 2,
    Stop = 3,
    /// <summary>Builders finish a blueprint or wall segment at the tile.</summary>
    Build = 4,
    /// <summary>Builders repair a damaged building at the tile.</summary>
    Repair = 5,
    /// <summary>Diggers tunnel into the rock, gem or wall at the tile.</summary>
    Dig = 6,
}

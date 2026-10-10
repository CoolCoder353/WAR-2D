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
    }
}

/// <summary>The order a client gives its units (the wire value of <c>CmdOrderChunk</c> and <c>CmdOrderSquad</c>).</summary>
public enum OrderKind : byte
{
    Move = 0,
    AttackMove = 1,
    Hold = 2,
    Stop = 3,
}

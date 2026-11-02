namespace WAR2D.Net.Replication
{
    /// <summary>
    /// How an attack event names its target: a unit or building id (positive), unnamed (0), or, only in the attacker
    /// owner's own events (v0.16), a wall tile as <c>-(tile index + 1)</c>.
    /// </summary>
    public static class AttackTargetIds
    {
        /// <summary>The target id for a wall tile (row-major index).</summary>
        public static int OfWall(int tileIndex) => -(tileIndex + 1);

        /// <summary>True when the target names a wall tile.</summary>
        public static bool IsWall(int targetId) => targetId < 0;

        /// <summary>The wall tile a negative target names.</summary>
        public static int WallTile(int targetId) => -targetId - 1;
    }
}

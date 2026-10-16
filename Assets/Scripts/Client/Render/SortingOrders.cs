namespace WAR2D.Client.Render
{
    /// <summary>
    /// Sorting orders of everything drawn in the world, low to high. Buildings take a band below zero by
    /// their y (nearer ones in front, for the 3/4 view), so every other layer sits outside that band.
    /// </summary>
    public static class SortingOrders
    {
        public const int Terrain = short.MinValue;
        /// <summary>The HQ clearing ring: over the map, under every building.</summary>
        public const int ClearingRing = -20000;
        public const int Effects = 10000;
        public const int Fog = 20000;
        /// <summary>Dev overlays: above fog, under selection brackets.</summary>
        public const int DebugOverlay = 25000;
        public const int BuildingSelection = 30000;
        public const int PlacementPreview = short.MaxValue;

        /// <summary>A building's order: 0 at y = 0 down to -4096 at y = 1024 (the largest map), farther ones behind.</summary>
        public static int Building(float y) => -(int)System.Math.Round(y * 4f);
    }
}

using WAR2D.Art.Render;

namespace WAR2D.Art
{
    public static partial class ArtManifest
    {
        /// <summary>The buildings, each with the generator that draws it (raised by <see cref="RenderGen.Lift"/> where it stands upright).</summary>
        private static ArtEntry[] Buildings() => new[]
        {
            // Buildings: footprint from GameConfig.xml; drawn upright. Only the Miner's rotation shows (4 rendered facings).
            new ArtEntry(nameof(BuildingType.Base), ArtKind.Building, 5, 5, 1, 1, true).Lit(c => RenderGen.Base(), RenderGen.Lift),
            new ArtEntry(nameof(BuildingType.Barracks), ArtKind.Building, 2, 2, 2, 1, true).Lit(c => RenderGen.Spawner(c.Frame), RenderGen.Lift),
            new ArtEntry(nameof(BuildingType.Miner), ArtKind.Building, 1, 1, 4, BuildingDirections, true).Lit(c => RenderGen.Miner(c.Frame, c.Direction), RenderGen.Lift),
            new ArtEntry("Mine", ArtKind.Building, 1, 1, 2, 1, true).Lit(c => RenderGen.Mine(c.Frame)),
            new ArtEntry("Radar", ArtKind.Building, 2, 2, 2, 1, true).Lit(c => RenderGen.Radar(c.Frame), RenderGen.Lift),
            new ArtEntry(nameof(BuildingType.HeavyFactory), ArtKind.Building, 3, 3, 2, 1, true).Lit(c => RenderGen.HeavyFactory(c.Frame), RenderGen.Lift),
            new ArtEntry(nameof(BuildingType.SiegeWorkshop), ArtKind.Building, 2, 2, 2, 1, true).Lit(c => RenderGen.SiegeWorkshop(c.Frame), RenderGen.Lift),
            // v0.13: the Gun turret's base (frame 1 is the damaged plinth, chosen by health, not looped) and its head (drawn over the
            // base in 8 facings). v0.20: both 2×2; the head's frame 1 fires its left barrel, frame 2 its right. And the Jammer.
            new ArtEntry(nameof(BuildingType.GunTurret), ArtKind.Building, 2, 2, 2, 1, true).Lit(c => RenderGen.GunTurret2x2Base(c.Frame), RenderGen.Lift),
            new ArtEntry(TurretHead, ArtKind.Building, 2, 2, 3, UnitDirections, true).Lit(c => RenderGen.GunTurret2x2Head(c.Angle, c.Frame), RenderGen.Lift),
            new ArtEntry(nameof(BuildingType.Jammer), ArtKind.Building, 2, 2, 2, 1, true).Lit(c => RenderGen.Jammer(c.Frame), RenderGen.Lift),
        };
    }
}

using WAR2D.Art.Render;

namespace WAR2D.Art
{
    public static partial class ArtManifest
    {
        /// <summary>The units and their layers, each with the generator that draws it.</summary>
        private static ArtEntry[] Units() => new[]
        {
            // Units: 1×1 tile, 8 facings; frames are move (2) then the unit's action (2).
            new ArtEntry(nameof(UnitType.Tank), ArtKind.Unit, 1, 1, 4, UnitDirections, true).Lit(c => RenderGen.Tank(c.Angle, c.Frame)),
            new ArtEntry("Builder", ArtKind.Unit, 1, 1, 4, UnitDirections, true).Lit(c => RenderGen.Builder(c.Angle, c.Frame)),
            new ArtEntry("Digger", ArtKind.Unit, 1, 1, 4, UnitDirections, true).Lit(c => RenderGen.Digger(c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Scout), ArtKind.Unit, 1, 1, 4, UnitDirections, true).Lit(c => RenderGen.Scout(c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Flamethrower), ArtKind.Unit, 1, 1, 4, UnitDirections, true).Lit(c => RenderGen.Flamethrower(c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Bomber), ArtKind.Unit, 1, 1, 4, UnitDirections, true).Lit(c => RenderGen.Bomber(c.Angle, c.Frame)),
            // Large units draw on 2×2-tile canvases (v0.20: the Sniper is one).
            new ArtEntry(nameof(UnitType.Sniper), ArtKind.Unit, 2, 2, 4, UnitDirections, true).Lit(c => RenderGen.Sniper2x2(c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Heavy), ArtKind.Unit, 2, 2, 4, UnitDirections, true).Lit(c => RenderGen.Heavy(c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Artillery), ArtKind.Unit, 2, 2, 4, UnitDirections, true).Lit(c => RenderGen.Artillery(c.Angle, c.Frame)),
            // Their layers: a turret's canvas is big enough for its barrel in every facing, centred on its pivot.
            new ArtEntry(nameof(UnitType.Tank) + HullSuffix, ArtKind.Unit, 1, 1, 2, UnitDirections, true).Lit(c => RenderGen.HullLayer(UnitType.Tank, c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Tank) + TurretSuffix, ArtKind.Unit, 1, 1, 2, TurretDirections, true).Lit(c => RenderGen.TurretLayer(UnitType.Tank, c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Scout) + HullSuffix, ArtKind.Unit, 1, 1, 2, UnitDirections, true).Lit(c => RenderGen.HullLayer(UnitType.Scout, c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Scout) + TurretSuffix, ArtKind.Unit, 1, 1, 2, TurretDirections, true).Lit(c => RenderGen.TurretLayer(UnitType.Scout, c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Sniper) + HullSuffix, ArtKind.Unit, 2, 2, 2, UnitDirections, true).Lit(c => RenderGen.HullLayer(UnitType.Sniper, c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Sniper) + TurretSuffix, ArtKind.Unit, 3, 3, 2, TurretDirections, true).Lit(c => RenderGen.TurretLayer(UnitType.Sniper, c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Heavy) + HullSuffix, ArtKind.Unit, 2, 2, 2, UnitDirections, true).Lit(c => RenderGen.HullLayer(UnitType.Heavy, c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Heavy) + TurretSuffix, ArtKind.Unit, 2, 2, 2, TurretDirections, true).Lit(c => RenderGen.TurretLayer(UnitType.Heavy, c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Artillery) + HullSuffix, ArtKind.Unit, 2, 2, 2, UnitDirections, true).Lit(c => RenderGen.HullLayer(UnitType.Artillery, c.Angle, c.Frame)),
            new ArtEntry(nameof(UnitType.Artillery) + TurretSuffix, ArtKind.Unit, 3, 3, 3, TurretDirections, true).Lit(c => RenderGen.TurretLayer(UnitType.Artillery, c.Angle, c.Frame)),
        };
    }
}

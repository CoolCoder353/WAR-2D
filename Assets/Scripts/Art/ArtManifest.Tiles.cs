using WAR2D.Art.Render;

namespace WAR2D.Art
{
    public static partial class ArtManifest
    {
        private static int Shapes => RenderGen.RockShapes.Length;

        /// <summary>The tiles and terrain features, each with the generator that draws it. The border shares the cache key of rock frame 15.</summary>
        private static ArtEntry[] Tiles() => new[]
        {
            // Tiles: frames are variants (random for floor and gems, autotile masks for walls).
            new ArtEntry(nameof(TileType.Ground), ArtKind.Tile, 1, 1, RenderGen.FloorVariants, 1, false).Lit(c => RenderGen.Floor(c.Seed, c.Frame)),
            new ArtEntry(nameof(TileType.Wall), ArtKind.Tile, 1, 1, RenderGen.RockShapes.Length, 1, false).Lit(c => RenderGen.Rock(c.Seed, RenderGen.RockShapes[c.Frame].mask, RenderGen.RockShapes[c.Frame].corners), key: c => $"Rock {c.Frame}"), // one per rock shape
            new ArtEntry(nameof(TileType.Gem), ArtKind.Tile, 1, 1, 4 * RenderGen.RockShapes.Length, 1, false).Lit(c => RenderGen.Gem(c.Seed, c.Frame / Shapes, RenderGen.RockShapes[c.Frame % Shapes].mask, RenderGen.RockShapes[c.Frame % Shapes].corners)), // 4 variants × rock shapes, variant-major
            new ArtEntry(nameof(TileType.RichGem), ArtKind.Tile, 1, 1, 4 * RenderGen.RockShapes.Length, 1, false).Lit(c => RenderGen.RichGem(c.Seed, c.Frame / Shapes, RenderGen.RockShapes[c.Frame % Shapes].mask, RenderGen.RockShapes[c.Frame % Shapes].corners)), // v0.17: as Gem, variant-major
            new ArtEntry(nameof(TileType.Border), ArtKind.Tile, 1, 1, 1, 1, false).Lit(c => RenderGen.Rock(c.Seed, 15), key: c => "Rock 15"),
            new ArtEntry("PlayerWall", ArtKind.Tile, 1, 1, AutotileVariants * WallDamageStates, 1, true).Lit(c => RenderGen.PlayerWall(c.Seed, c.Frame % AutotileVariants, c.Frame / AutotileVariants)),
            new ArtEntry("WallBlueprint", ArtKind.Tile, 1, 1, AutotileVariants, 1, true).Lit(c => RenderGen.WallBlueprint(c.Frame)),
            // Terrain features (2026-10-31): stamps the Terrain shader scatters over open floor and deep rock, kind-major,
            // each kind in FeatureFrames rotations. Transparent where the feature leaves the ground as it is.
            new ArtEntry(FloorFeature, ArtKind.Tile, RenderGen.FeatureTiles, RenderGen.FeatureTiles, RenderGen.FloorFeatureKinds * RenderGen.FeatureFrames, 1, false).Lit(c => RenderGen.FloorFeature(c.Seed, c.Frame / RenderGen.FeatureFrames, c.Frame % RenderGen.FeatureFrames)),
            new ArtEntry(RockFeature, ArtKind.Tile, RenderGen.FeatureTiles, RenderGen.FeatureTiles, RenderGen.RockFeatureKinds * RenderGen.FeatureFrames, 1, false).Lit(c => RenderGen.RockFeature(c.Seed, c.Frame / RenderGen.FeatureFrames, c.Frame % RenderGen.FeatureFrames)),
        };
    }
}

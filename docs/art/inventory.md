# Art and audio inventory (v0.8)

Every placeholder the game uses today and every asset v0.7 Siege will need. `Assets/Scripts/Art/ArtManifest.cs` is the code version of the art tables: tests fail when a `UnitType`, `BuildingType` or `TileType` value has no entry. Sizes are in tiles (one tile is one world unit). A sprite has `frames × directions` cells in its atlas.

The [style guide](style-guide.md) sets the values: 64 px per tile (PPU 64), 8 unit facings, 10 fps. `WAR-2D/Art/Build all` (`Editor/ArtBuild.cs`) renders every unit, building and tile entry into `Assets/Resources/Art/{Units,Buildings,Tiles}` (`.png`, `_mask.png` for the team colour, `.json` frame table, loaded with `ArtAtlas`). Entries with no generator yet (the v0.7 kit: Builder, Digger, Bomb, PlayerWall, WallBlueprint) get a placeholder block. Effects, icons and UI get their atlases with plan Tasks 11 and 12.

## Units

| Id | Size | Frames | Directions | Team mask | Source today |
|---|---|---|---|---|---|
| `Tank` | 1×1 | 4 (move 2, fire 2) | 8 | yes | `Resources/tank.png` (32 px, one image, rotated quad) |
| `Builder` | 1×1 | 4 (move 2, build 2) | 8 | yes | none (v0.7) |
| `Digger` | 1×1 | 4 (move 2, dig 2) | 8 | yes | none (v0.7) |

Units draw with pre-drawn facings, not a rotated quad (rotation breaks the pixel grid under Pixel Perfect): `UnitFrames` picks the cell from the facing and state, and `InstancedUnitRenderer` tints the team mask with the owner's colour. A type with no frames in the atlas draws as the Tank.

## Buildings

| Id | Size | Frames | Team mask | Source today |
|---|---|---|---|---|
| `Base` (HQ) | 5×5 (3×3 before v0.8) | 1 | yes | `Resources/Base.png` (64 px, PPU 22) |
| `SmallUnitSpawner` | 2×2 | 2 (idle: door lamp, each held 5 ticks) | yes | `Resources/SmallUnitSpawner.png` (64 px) |
| `Miner` | 1×1 | 4 (drill loop) | yes | `Resources/miner.png` (32 px) |
| `Bomb` | 1×1 | 2 (armed blink) | yes | none (v0.7; never sent to enemy clients, so only its owner's team draws it) |

Footprints match `GameConfig.xml` (`ArtManifestTests.BuildingFootprintsMatchTheConfig`). The plan's Task 2 note of a 2×2 Miner is out of date: the Miner is 1×1. Buildings draw from the `Buildings` atlas with the `TeamSprite` shader (`TeamSpriteMaterial`, one material per owner colour; ghosts are grey and 60 % opaque), always upright, and loop their idle frames with `SpriteFrameAnimator`.

## Tiles

| Id | Frames | Team mask | Source today |
|---|---|---|---|
| `Ground` | 4 random variants | no | `Resources/Sprites/Tiles/Floor.asset`; generated maps draw one flat texel per tile (`MapView`) |
| `Wall` (rock) | 16 autotile masks (N, E, S, W) | no | `Wall.asset`, `WallSIB.asset` (sibling rule tile) |
| `Gem` | 4 random variants × 16 autotile masks (variant-major) | no | `Gems.asset`, `GemsSIB.asset` |
| `Border` | 1 (solid rock) | no | drawn as rock |
| `PlayerWall` | 16 masks × 3 damage states | yes | none (v0.7) |
| `WallBlueprint` | 16 masks | yes | none (v0.7) |

Unused today: `Spawner.asset`, `Test.asset` and the `Pallets` tile palette (removed in Task 16 if nothing references them).

## Effects and overlays

| Id | Size | Frames | Source today |
|---|---|---|---|
| `Explosion` | 2×2 | 6 | `Effects.Explosion`: an orange `ProceduralSprites.Circle` scaled and faded with DOTween |
| `BombBlast` | 3×3 | 8 | none (v0.7) |
| `MuzzleFlash` | 1×1 | 2 | none |
| `Tracer` | 1×1 | 1 | `Effects.Tracer`: a yellow `ProceduralSprites.Pixel` |
| `DigDebris` | 1×1 | 4 | none (v0.7) |
| `RepairSparks` | 1×1 | 4 | none (v0.7) |
| `ConstructionOverlay` | 1×1 (tiled over the footprint) | 4 | none (v0.7: blueprint and under-construction) |
| `SelectionRingSmall` / `SelectionRingLarge` | 1×1 / 2×2 | 1 | selection is a highlight colour in `InstancedUnit.shader` |
| `WaypointFlag` | 1×1 | 2 | none (queued waypoints aren't drawn yet) |
| `RallyFlag` | 1×1 | 2 (team mask) | none |
| `FogEdge` | 1×1 | 16 dither masks | `FogView`: a flat fog texture, bilinear edges |
| `HealthBar` | 1×1 | 2 (back, fill) | units: `InstancedUnitRenderer.BuildBarAtlas`; buildings: uGUI `Resources/Ui/HealthBarUI` (goes with instanced buildings in v0.7) |

## Icons and cursors

| Id | Source today |
|---|---|
| `IconMove`, `IconAttackMove`, `IconStop`, `IconHold`, `IconMiner`, `IconSpawner`, `IconWall` | vector paths in `UI/Controllers/CommandIcon.cs` (approved v0.6 Figma icons) |
| `IconBuilder`, `IconDigger`, `IconBomb`, `IconRepair` | none (v0.7 command card) |
| `CursorDefault`, `CursorMove`, `CursorAttack`, `CursorInvalid` | the OS cursor |

## UI

| Id | Frames | Source today |
|---|---|---|
| `PanelFrame` | 1 (9-slice) | flat `Theme.uss` panels |
| `ButtonFrame` | 3 (default, hover, disabled) | flat `Theme.uss` buttons |

The font (an OFL pixel font or a code-generated bitmap font) and the token values come with the UI skin (plan Task 12, Figma first). The main menu's background is the live battle (owner request, v0.6), so there is no menu background image; the lobby keeps its flat surface unless the Task 12 design adds one.

## Audio

None exists today (`Main.mixer` has Master, Music and SFX groups wired to the settings sliders, but nothing plays).

**Sound effects** (plan Task 13, sfxr-style, one JSON parameter set each): `ShotTank`, `ShotBuilder`, `ShotDigger` (stubs until v0.7), `ExplosionSmall`, `ExplosionLarge`, `BuildingPlaced`, `BuildingDestroyed`, `UnitProduced`, `GemMined`, `UiClick`, `UiHover`, `UiError`, `Alert`, `VictoryStinger`, `DefeatStinger`; v0.7 kit: `ConstructLoop`, `Dig`, `BombBlast`, `WallHit`.

**Music** (plan Task 14, code-generated chiptune): `Menu`, `MatchCalm`, `MatchBattle`, `Victory`, `Defeat`.

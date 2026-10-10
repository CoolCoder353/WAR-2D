# Art and audio inventory (v0.8)

Every placeholder the game uses today and every asset v0.7 Siege will need. `Assets/Scripts/Art/ArtManifest.cs` is the code version of the art tables: tests fail when a `UnitType`, `BuildingType` or `TileType` value has no entry. Sizes are in tiles (one tile is one world unit). A sprite has `frames × directions` cells in its atlas.

Provisional values (16 px per tile, 8 unit facings, frame counts) are confirmed or changed by the style guide (plan Task 5).

## Units

| Id | Size | Frames | Directions | Team mask | Source today |
|---|---|---|---|---|---|
| `Tank` | 1×1 | 4 (move 2, fire 2) | 8 | yes | `Resources/tank.png` (32 px, one image, rotated quad) |
| `Builder` | 1×1 | 4 (move 2, build 2) | 8 | yes | none (v0.7) |
| `Digger` | 1×1 | 4 (move 2, dig 2) | 8 | yes | none (v0.7) |

Units draw with pre-drawn facings, not a rotated quad (rotation breaks the pixel grid under Pixel Perfect). Today every unit type draws with the Tank texture (`known-issues.md`, fixed in plan Task 7).

## Buildings

| Id | Size | Frames | Team mask | Source today |
|---|---|---|---|---|
| `Base` (HQ) | 3×3 | 1 | yes | `Resources/Base.png` (64 px, PPU 22) |
| `SmallUnitSpawner` | 2×2 | 2 (idle) | yes | `Resources/SmallUnitSpawner.png` (64 px) |
| `Miner` | 1×1 | 4 (drill loop) | yes | `Resources/miner.png` (32 px) |
| `Bomb` | 1×1 | 2 (armed blink) | yes | none (v0.7; never sent to enemy clients, so only its owner's team draws it) |

Footprints match `GameConfig.xml` (`ArtManifestTests.BuildingFootprintsMatchTheConfig`). The plan's Task 2 note of a 2×2 Miner is out of date: the Miner is 1×1. Buildings rotate in 90° steps as whole sprites, which keeps the pixel grid.

## Tiles

| Id | Frames | Team mask | Source today |
|---|---|---|---|
| `Ground` | 4 random variants | no | `Resources/Sprites/Tiles/Floor.asset`; generated maps draw one flat texel per tile (`MapView`) |
| `Wall` (rock) | 16 autotile masks (N, E, S, W) | no | `Wall.asset`, `WallSIB.asset` (sibling rule tile) |
| `Gem` | 4 random variants | no | `Gems.asset`, `GemsSIB.asset` |
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

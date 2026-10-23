# Art and audio inventory (v0.8)

Every placeholder the game uses today and every asset v0.7 Siege will need. `Assets/Scripts/Art/ArtManifest.cs` is the code version of the art tables: tests fail when a `UnitType`, `BuildingType` or `TileType` value has no entry. Sizes are in tiles (one tile is one world unit). A sprite has `frames × directions` cells in its atlas.

The [style guide](style-guide.md) sets the values: 64 px per tile (PPU 64), 8 unit facings, 10 fps. `WAR-2D/Art/Build all` (`Editor/ArtBuild.cs`) renders every unit, building and tile entry into `Assets/Resources/Art/{Units,Buildings,Tiles}` (`.png`, `_mask.png` for the team colour, `.json` frame table, loaded with `ArtAtlas`). Entries with no generator yet (the v0.7 kit: Builder, Digger, Bomb, PlayerWall, WallBlueprint) get a placeholder block. Effects, icons and UI get their atlases with plan Tasks 11 and 12.

## Units

| Id | Size | Frames | Directions | Team mask | Source today |
|---|---|---|---|---|---|
| `Tank` | 1×1 | 4 (move 2, fire 2) | 8 | yes | `Resources/tank.png` (32 px, one image, rotated quad) |
| `Builder` | 1×1 | 4 (move 2, build 2) | 8 | yes | none (v0.7) |
| `Digger` | 1×1 | 4 (move 2, dig 2) | 8 | yes | none (v0.7) |
| `Scout`, `Flamethrower`, `Sniper`, `SuicideBomber` | 1×1 | 4 (move 2, fire 2) | 8 | yes | `RenderGen.Arsenal.cs` (v0.12) |
| `Heavy`, `Artillery` | 2×2 (drawn at twice the size; large selection ring) | 4 (move 2, fire 2) | 8 | yes | `RenderGen.Arsenal.cs` (v0.12, 128 px canvases) |

Units draw with pre-drawn facings, not a rotated quad (rotation breaks the pixel grid under Pixel Perfect): `UnitFrames` picks the cell from the facing and state, and `InstancedUnitRenderer` tints the team mask with the owner's colour. A type with no frames in the atlas draws as the Tank.

## Buildings

| Id | Size | Frames | Team mask | Source today |
|---|---|---|---|---|
| `Base` (HQ) | 5×5 (3×3 before v0.8) | 1 | yes | `Resources/Base.png` (64 px, PPU 22) |
| `Barracks` (was `SmallUnitSpawner`) | 2×2 | 2 (idle: door lamp, each held 5 ticks) | yes | `RenderGen.Spawner` (renamed in v0.12, same art) |
| `HeavyFactory` | 3×3 | 2 | yes | `RenderGen.HeavyFactory` (v0.12) |
| `SiegeWorkshop` | 2×2 | 2 | yes | `RenderGen.SiegeWorkshop` (v0.12) |
| `GunTurret` (base) | 1×1 | 2 (whole, damaged: chosen by health below 50 %, not looped) | yes | `RenderGen.GunTurretBase` (v0.13) |
| `GunTurretHead` | 1×1 | 2 (rest, recoil just after a shot) × 8 facings | yes | `RenderGen.GunTurretHead` (v0.13), drawn over the base in the facing `ClientBuildings.Heads` keeps |
| `Jammer` | 2×2 | 2 (aerial lamps, each held half a second) | yes | `RenderGen.Jammer` (v0.13) |
| `Miner` | 1×1 | 4 (drill loop) × 4 facings | yes | `Resources/miner.png` (32 px) |
| `Bomb` | 1×1 | 2 (armed blink) | yes | none (v0.7; never sent to enemy clients, so only its owner's team draws it) |

Footprints match `GameConfig.xml` (`ArtManifestTests.BuildingFootprintsMatchTheConfig`). The plan's Task 2 note of a 2×2 Miner is out of date: the Miner is 1×1. Buildings draw from the `Buildings` atlas with the `TeamSprite` shader (`TeamSpriteMaterial`, one material per owner colour; ghosts are grey and 60 % opaque), always upright, and loop their idle frames with `SpriteFrameAnimator`. The Miner's 90° rotation picks one of its 4 facings (`TeamSpriteMaterial.Facing`); the other buildings have one.

## Tiles

| Id | Frames | Team mask | Source today |
|---|---|---|---|
| `Ground` | 4 random variants | no | `Tiles` atlas, drawn by the `Terrain` shader (`MapView`, index texture from `TileAutotile`) |
| `Wall` (rock) | 16 autotile masks (N, E, S, W) | no | `Wall.asset`, `WallSIB.asset` (sibling rule tile) |
| `Gem` | 4 random variants × 16 autotile masks (variant-major) | no | `Gems.asset`, `GemsSIB.asset` |
| `Border` | 1 (solid rock) | no | drawn as rock |
| `PlayerWall` | 16 masks × 3 damage states | yes | none (v0.7) |
| `WallBlueprint` | 16 masks | yes | none (v0.7) |

Unused today: `Spawner.asset`, `Test.asset` and the `Pallets` tile palette (removed in Task 16 if nothing references them).

## Effects and overlays

| Id | Size | Frames | Source today |
|---|---|---|---|
| `Explosion` | 2×2 | 6 | `Effects` atlas (`EffectGen.Blast`), pooled in `Effects` (128) |
| `BombBlast` | 3×3 | 8 | `Effects` atlas (`EffectGen.Blast`); unused until v0.7 |
| `MuzzleFlash` | 1×1 | 2 | `Effects` atlas, pooled (128), at the shooter's barrel with each tracer |
| `Tracer` | 1×1 | 1 | `Effects` atlas, pooled (256), turned to its heading |
| `SniperTracer` | 1×1 | 1 | `Effects` atlas, pooled (64): the Sniper's shot (v0.12) |
| `ArtilleryShell` / `ShellImpact` | 1×1 / 4×4 | 2 / 6 | `Effects` atlas, pooled (64 each): the shell flies 0.5 s, then the impact plays (v0.12) |
| `FlameStream` | 3×1 | 4 | `Effects` atlas, pooled (128), stretched from the nozzle to the target (v0.12) |
| `BomberBlast` | 4×4 | 8 | `Effects` atlas (v0.12); built but unused: bomber blasts reach clients as `BombBlast` events and draw the bomb's effect |
| `TurretMuzzle` | 1×1 | 3 | `Effects` atlas, pooled (64): at the turret's muzzle (`RenderGen.TurretMuzzleReach`/`Height`), turned to the head's facing, with a tracer to the target (v0.13) |
| `JamPulse` | 3×3 | 6 | `Effects` atlas, pooled (32): every 2.5 s over each finished Jammer the client sees (its own only while paid) (v0.13) |
| `DigDebris` | 1×1 | 4 | `Effects` atlas; unused until v0.7 |
| `RepairSparks` | 1×1 | 4 | `Effects` atlas; unused until v0.7 |
| `ConstructionOverlay` | 1×1 (tiled over the footprint) | 4 | `Effects` atlas; unused until v0.7 |
| `SelectionRingSmall` / `SelectionRingLarge` | 1×1 / 2×2 | 1 | `Effects` atlas: the small ring is drawn under selected units (instanced, `InstancedUnitRenderer.DrawRings`); the large one is corner brackets, 9-sliced over a selected building's footprint (`BuildingSelectionMarker`) |
| `WaypointFlag` | 1×1 | 2 | `Effects` atlas; queued waypoints aren't drawn yet |
| `RallyFlag` | 1×1 | 2 (team mask) | `Effects` atlas; no rally points yet |
| (fog edge) | — | — | dithered in the `Fog` shader (4×4 ordered dither on 2-art-pixel blocks), so no sprite |
| `HealthBar` | 1×1 | 2 (back, fill) | `Effects` atlas (reference); units draw `InstancedUnitRenderer.BuildBarAtlas` (48×4, palette outline); buildings: uGUI `Resources/Ui/HealthBarUI` (goes with instanced buildings in v0.7) |

## Icons and cursors

| Id | Source today |
|---|---|
| `IconMove`, `IconAttackMove`, `IconStop`, `IconHold`, `IconMiner`, `IconSpawner`, `IconWall` | vector paths in `UI/Controllers/CommandIcon.cs` (approved v0.6 Figma icons) |
| `IconBuilder`, `IconDigger`, `IconBomb`, `IconRepair` | none (v0.7 command card) |
| `IconGunTurret`, `IconJammer` | `Art/PixelIcons.cs` bitmaps (v0.13, Figma `Icons – v0.13`) |
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

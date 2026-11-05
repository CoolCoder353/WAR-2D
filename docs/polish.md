# Polish backlog

Small presentation improvements: client-only, built from the existing effect pools, generators and sounds, with no HUD or menu change (so no Figma round). Each one follows only what the client already knows and draws, so nothing here changes what a client learns (`LeakTests` stay as they are).

Started 2026-10-30 from a survey of the client code. Items move to **Built** when the owner approves and they are done.

## Built (branch `polish/presentation`, 2026-10-30)

The owner approved 1, 2, 3, B, C, D, F, G, H and I. All are built, with EditMode tests (`PolishEffectsTests`, `PolishEffectArtTests`) and the Effects atlas rebuilt (`WAR-2D/Art/Build all`). Both suites pass.

Checked in an editor match on 2026-10-30, through the control API, screenshots and the effects' counters: the red hit flash and the bar trail on units, smoke and fire on a damaged HQ, the glints, tread marks, fog easing, building collapses with their ripple, rubble and scorch. No exceptions were logged. The owner's playtest signed them off (2026-11-01).

### 1. Play the sounds that were never played

| Sound | Where it plays now |
| --- | --- |
| `explosion_large`, `building_destroyed` | A finished building 2 tiles or wider destroyed in sight (`Effects.BuildingCollapse`, item B) |
| `unit_produced` | A new own unit within 2.5 tiles of one of the player's finished producers (`ClientUnitStore.Appeared`), quiet for 2 s after the first batch so a join or rejoin doesn't play one per unit |
| `construct_loop` | One loop (`AudioService.Loop`) while one of the player's blueprints on screen got a progress record in the last 3 s, at the one nearest the view's centre; it fades in and out over 0.3 s at half volume |
| `gem_mined` | With item I's glint, at most every 0.4 s however many Miners are on screen |
| `wall_hit` | A seen wall tile whose health dropped in a terrain update (`ClientTerrain.Changed`). Wall shots carry no target position, so this follows the damage, not the shot |
| `ui_hover` | The pointer moving onto an enabled button in the HUD or the menus (`Widgets.HoverSounds`), at most every 0.06 s |
| `ui_error` | A card hotkey refused ("Radar: need 250 (you have 90)", "Queue full"), and a placement click on a spot the preview showed as invalid (instead of `building_placed`) |
| `victory`, `defeat` | Played once as the end screen appears, as a short hit while the music crossfades (2 s) into the Victory or Defeat stinger. Kept rather than deleted: the stinger alone starts late |

### 2. Units flash when hit

A unit whose replicated health drops flashes red for 0.15 s, as buildings do. `ClientWorld.TrackHit` compares each unit's health with last frame's (by id index, reset when the index gets a new id) and sets bit 2 of the instance's `pad`; the `InstancedUnit` shader tints it. The constants are in `HitFeedback`.

### 3. Wrecks and scorch marks

New ground-mark layer, `Decals` (pooled sprites at `SortingOrders.Decals`, under the clearing ring and everything else). A known unit that dies (`ClientUnitStore.Died`) leaves a `Scorch` (`ScorchLarge` under Heavies and Artillery) that holds 10 minutes (60 s before 2026-11-02) and dithers out over 20 s, and (playtest 2026-10-30) its wreck: its own sprite at its last facing, tinted charred, holding 10 minutes and fading over 15 s (not a Suicide bomber). A destroyed finished building leaves rubble the size of its footprint (`Rubble1/2/3/5`, 4 tiles uses the 3-tile one), which stays until a building or blueprint enters over it (`Decals.ClearRubbleUnder`). (2026-11-02, owner: longer holds rather than bigger pools; a pool that is full still reuses its oldest mark.)

### B. Building deaths scaled to size

`ClientPlayer.TargetPlayExplosions` now goes through `WorldEffects.Explosions`. An explosion on the anchor tile of a finished building the client saw (still known, or removed in the last 2 s, since the Gone record can arrive first: `ClientBuildings.TryTakeDying`) is that building's. It plays `BombBlast` (2–3 tiles) or `BomberBlast` (4 and wider), then a ripple of half the footprint's area in small explosions and debris over 0.5 s (capped at 128 waiting), and the rubble. Blueprints, bombs and 1-tile buildings keep the unit explosion. Units never stand on a building's anchor, so a miss is a unit.

### C. Damaged buildings smoke

Finished buildings on screen under half health give off rising `Smoke` puffs every 0.45 s, and under a quarter `Fire` every 0.3 s too, from 1 to 3 fixed spots on the roof (by size). Each building's timer is staggered by its id. Bombs and ghosts don't smoke.

### D. Stereo panning

`SfxVoicePool.Pan`: each positional sound pans by its screen x, up to 0.7 at the view's edges (never fully into one ear). The construction loop pans too.

### F. Health bars show the hit

A unit's or building's bar keeps the lost part as a pale segment that holds 0.25 s after the last hit, then drains at a whole bar per 0.4 s (`HitFeedback.Trail`). The trail travels in the bar instance's `rotation` (quads never rotate), and the shader draws it on bar materials (`_Trail`). A ghost coming back into sight with less health gets no flash or trail.

### G. Track marks

Tanks, Heavies and Artillery on screen lay a `Tracks` mark every half tile moved, turned to the exact heading (playtest 2026-10-30; it was the nearest of 4 directions). Marks hold 120 s (12 s before 2026-11-02) and fade over 10 s; in a big fight the 2048 pool turns over within seconds, so the oldest go first. A jump of more than 2 tiles (a correction) lays none. The pool is 2048.

### H. Fog fades in and out

`FogView` eases each changed cell's alpha to its new level over 0.25 s (unexplored to seen; smaller changes take less), and the `Fog` shader's dither turns that into a dissolve. A new grid (the first snapshot, a rejoin) is drawn at once. The fog texture is uploaded every frame while any cell is easing, instead of once per fog update; signed off by the owner (2026-11-01).

### I. Mining sparkle

Once a second (a Miner's payout interval), each finished Miner on screen whose faced tile the client knows is a gem glints on that tile (`GemSparkle`), with `gem_mined` (item 1).

## Built (2026-11-01)

The owner approved the three candidates A, E and J. EditMode tests in `PolishEffectsTests` and `PolishEffectArtTests`; the Effects atlas is rebuilt. Not yet looked at in a running match.

### A. Bullet impacts

Every tracer that reaches its target (plain, Sniper and Gun turret tracers; not Artillery shells, which keep `ShellImpact`, or flame streams) ends in a `HitSpark` (`EffectGen.HitSpark`, 3 frames: a white-hot point and short cooling sparks) from its own pool (`Effects.HitSparkPool` 128). Silent: the shot already played its sound. A shot drawn without its tracer line (the Tracers setting) has no spark either.

### E. Units appear with a puff

A new own unit beside one of the player's finished producers (the `unit_produced` rule in `WorldEffects.UnitAppeared`, quiet after a join or rejoin) kicks up a `DustPuff` (`EffectGen.DustPuff`, 5 frames, a low tan ball that spreads and thins) where it appears.

### J. Construction-finished flourish

A building whose progress record reaches 100 % while the client sees it (`ClientBuildings.ProgressChanged`) gets a ring of dust puffs round its footprint's edge (`Effects.BuildingFinished`: 4, or 3 per footprint tile, pushed out to the square edge; `Effects.DustPool` 96) and blinks bright twice over 0.36 s (`HitFeedback.Finishing`; `BuildingPacking.FinishFlag`, bit 4 of the instance's `pad`, lerps the sprite 45 % toward warm white in `InstancedBuilding.shader`). A building first seen already finished, a ghost and a bomb do neither. Silent: the `construction_finished` alert has its sound.

## Not pursued

Owner, 2026-10-30: screen shake, order-acknowledgement chirps, map pings (asked for after all on 2026-11-04, for players sharing vision), chat, floating resource numbers, an idle-producer alert, end-screen graphs, a Quick skirmish button, first-match hints, colourblind team colours.

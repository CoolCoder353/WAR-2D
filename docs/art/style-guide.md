# Art style guide (v0.8)

**Status: approved by the owner on 2026-10-11 (plan Task 5), with every recommendation below as written.** The method, finish, palette and building view were approved earlier the same day at the method gate ([method-prototype.md](method-prototype.md)). The numbered items were the open decisions.

`Assets/Scripts/Art/ArtStyle.cs` holds these values in code, and `Assets/Art/palette.png` is the shipped palette at 1 px per colour. Written by `WAR-2D/Art/Write palette`, `ArtStyleTests` keeps the code and the file in step.

## Approved

| Item | Value |
|---|---|
| Method | Code-generated lit models (`Assets/Scripts/Art/Render/`), finished as pixel art by `PixelFinish`. No AI art ships. |
| Tile and PPU | **64 px per tile, 1 tile = 1 world unit, PPU 64** for every sprite and atlas. Import settings: point filter, uncompressed, no mipmaps, except that the unit, building and terrain atlases use mipmaps with trilinear filtering for smooth far zoom (item 4). |
| Units | Top-down, in a 64×64 frame, with **8 rendered facings** 45° apart. Quads never rotate. The light stays fixed at the top left in every facing. |
| Buildings | **3/4 view** (`RenderGen.Lift`) with their own silhouettes inside square footprints and headroom above them (`RenderGen.Headroom`, scaled per building). The pivot sits at the footprint's centre. The HQ is 5×5, the Small Unit Spawner 2×2, the Miner 1×1. Buildings rotate in 90° steps as whole sprites. |
| Terrain | Stone floor, raised craggy rock with 16 autotile masks, faceted gems. All opaque. |
| Finish | Majority-vote downsample onto the palette (no blending) and a 1 px outline in the palette's outline colour. Unit and building frames carry a baked hard shadow: 45 % black, 4 art px right and down (`ArtStyle.ShadowAlpha`, `ShadowOffset`). Terrain has neither. |
| Atlases | `WAR-2D/Art/Build all` writes `Assets/Resources/Art/{Units,Buildings,Tiles}`: the colour atlas (team parts as `ArtStyle.TeamGrey`), the team mask (white on team pixels) and a JSON frame table with pivots. A unit's cell for direction `d` (0 = east, anticlockwise) and frame `f` is `first + d × frames + f`. Frames are 2 px apart so mipmaps bleed less. |
| Palette | **Charcoal** (`StylePalettes.Charcoal`): its 18 role colours plus the renderer's 4 extra colours (sandbags, drums, crates, white), each in 5 shades (`PixelFinish.Shades`). |

## Decided (recommendations approved)

**1. Palette clearance from the player colours: leave out the clashing shades.**
Units and buildings carry the owner's colour, so no environment or machine colour should be mistaken for a player. The rule: drop any palette colour within **ΔE 10** (CIE76) of any player colour in any of the three palette modes (standard, colourblind, high contrast). After that and removing duplicates (bright shades clamp to the same colour), **90 colours** ship (plus transparent). Six shades are dropped:

| Dropped shade | Too close to |
|---|---|
| Gem 2, base (`#F0524A`) | standard red `#E5484D` (ΔE 7.0), high-contrast red `#FF5A5A` (5.9) |
| Gem 2, light (shade 4) | high-contrast red `#FF5A5A` (5.3) |
| Warn, light (shade 4) | colourblind yellow `#F0E442` (8.5), high-contrast yellow `#FFE14D` (8.9) |
| White, dark (shade 2), Lamp, dark (shade 2), Body 3, lightest (shade 5) | colourblind grey `#BBBBBB` (1.5 to 7.9) |

The finish maps those pixels to the next-nearest colour, so gems lose one of their red steps and the light greys lose a step. The bigger readability risk is colourblind player 8, which is grey: any grey on a charcoal map is close to it. **Recommendation:** accept the dropped shades, and replace colourblind player 8 in the Figma `Theme` tokens with a non-grey colour during the UI skin task (Task 12). That change would bring back the three grey shades.

**2. Team colour: a magenta key, shaded by step.**
Team parts render in a 5-step magenta key ramp (`ArtStyle.TeamKeyRamp`, one magenta in the 5 shades) that is not in the palette. The importer (Task 6) turns the key into a mask channel (step 0 to 4), and the unit and building shaders multiply the owner's colour by the step's shade, so every palette mode and colour works with one atlas. **Recommendation:** approve.

**3. Frames: 10 fps for everything.**
Unit move 2 frames and action (fire, build, dig) 2; building idle 1 to 4 (Miner drill loop 4, spawner 2); effects up to 8. **Recommendation:** approve.

**4. Zoom and Pixel Perfect (sets Task 10).**
A 64 px tile is drawn 1:1 at an orthographic size of `screenHeight / 128`: **8.4 at 1080p**, 11.25 at 1440p, 16.9 at 4K. Today's default of 10 draws a 1080p tile at 54 screen px, slightly below 1:1. The zoom range is 3 to 30.

- **Recommendation:** zooming in snaps to whole-number scales (1:1, 2:1, …) with the Pixel Perfect Camera on. The default zoom becomes 1:1 (size 8.4 at 1080p, which shows about 15 % less of the map than today). Zooming out past 1:1 turns Pixel Perfect off and moves in smooth steps up to size 30. Smooth steps need **mipmaps with trilinear filtering on the unit, building and terrain atlases** (an exception to the import rule above), because point-sampled pixel art shimmers when it is shrunk. At the farthest zoom, terrain falls back to flat colour as planned.
- Alternative: keep the default at size 10 and accept slightly downscaled art at the normal view, with Pixel Perfect only when zoomed in.

**5. Effects.**
Same pipeline and finish as the sprites. Explosions and muzzle flashes need a fire ramp (orange), which the palette lacks, and orange sits next to standard player 6 (`#F07F2E`). **Recommendation:** add a fire ramp in Task 11, kept clear of the player colours by the same ΔE rule (pushed toward yellow-white cores and dark-red smoke), and allow additive blending for the flash cores only.

**6. Icons and cursors.**
**Recommendation:** keep the approved v0.6 Figma vector icons for the command card (they're crisp at every UI scale), and draw cursors and any new icons on a 16×16 grid in the same style, through Figma (Task 12).

**7. UI font.**
**Recommendation:** an OFL or CC0 pixel font with its licence committed, picked in the Task 12 Figma design. A code-generated bitmap font stays the fallback.

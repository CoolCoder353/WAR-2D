# Playtest fixes, 2026-10-30 (Figma brief)

The HUD side of the 2026-10-30 playtest fixes (known-issues.md, "Owner playtest fixes (2026-10-30, polish branch)"), plus UI proposals found while drawing them. Same look as v0.6 to v0.15.1 (Jersey 10, square corners, the `Theme` and `Scale` variables, the `Command button (pixel, 56)`, `Key chip`, `Callout` and `Icon/px/*` components).

- **File:** [WAR-2D v0.6 UX](https://www.figma.com/design/JrMColnJiQbFfODqyPjBYf), page **Playtest fixes (2026-10-30)** (node 152:1477)
- **Status:** drawn 2026-10-30. Items 1, 2, 4 and 5 were built first (the owner asked for them in the playtest notes). The owner **approved item 3's button and proposals 6–11 on 2026-10-30** ("Build all 3, 6–11"); all are built on `polish/presentation`.
- **New local component:** `Icon/px/Roam` (152:1610), 16×16 in the `PixelIcons.cs` format (white body, 1 px dark outline): a winding trail ending in an arrowhead.

## Frame index

| Frame | Node | What it shows |
| ----- | ---- | ------------- |
| Title | 153:2021 | Status and reading order |
| P1 · HUD after the playtest fixes | 152:1616 | The everyday HUD (from v0.15 frame A) with every change, numbered callouts (hide layer `Callouts P1` for the clean view) |
| Legend P1 | 153:2025 | Items 1–11 |
| P2 · Proposals close-up | 153:2161 | Items 8, 9 and 10 at 1:1 |

## Built (sign-off)

1. **Top bar fits eight players.** Chips shrink; a long name ends in "…"; an AI's tag is its robot icon only (the full "AI · Hard" is in the hover card, item 8). `Theme.uss` `.top-bar__player*`, `TopBarController.ShowPlayers`.
2. **All Builders chip shows, not selects.** A click moves the camera to the next Builder without changing the selection; Ctrl + click (or Ctrl + `.`) selects them all. Its hover card is drawn in P1. `UnitCommander.ShowNextBuilder`, `HoverText.AllBuilders`.
3. **Roam (`J`).** A fifth order button after Hold, `cmd-roam`, with the `Icon/px/Roam` icon and key `J`, and the hover card "Roam · Scout on their own: each unit drives to ground you haven't seen lately. Any other order ends it. Builders don't roam." The Commands panel grows by one button (330 px). `CommandCardController.RoamClicked`, `HoverText.Roam`, `IconRoam` in `PixelIcons`.
4. **Calmer alert feed.** A repeat (same kind and text) joins its row with "×N"; rows leave after `Alerts/ShowSeconds` (12 s), held while the pointer is over the feed; a right click dismisses; five rows at most.
5. **HQ flags on the minimap.** A flag (dark pole, cloth in the owner's colour) over every HQ the player has found, their own included; dimmed for a last-seen ghost. `MinimapTexture.PlotFlag`.

## Approved 2026-10-30 and built

6. **Roam state:** the Roam button shows its Active state while every selected unit that can roam does, and the selection title adds "· 4 roaming" (`SelectionModel.Roaming`, `AllRoaming`). Greyed out for a Builders-only selection.
7. **Alert timer:** a 2 px bar in the row's stripe colour drains along its bottom edge until the row leaves; it stops while the feed is held (`.alert__timer`, `AlertFeedModel.Left`, redrawn every 0.1 s).
8. **Player hover card:** hovering a top-bar chip opens the shared hover card with the full name, the AI tag (or "Player"), the team in a team match, and the status (eliminated, or away with its countdown) (`TopBarController.PlayerCard`). The colour is the chip's own swatch.
9. **Builder position:** after a click on the All Builders chip its count reads "4 / 12" in gold for 2 s, so the player knows where they are in the cycle (`TopBarController.ShowBuilderPosition`).
10. **HQs minimap layer:** a fifth filter row, "HQs (flags)", on by default. `MinimapLayers.Hqs` (16) stays out of `All` and the saved filter bits: `GameSettings.MinimapHqs` holds it, so files saved before it existed still show the flags.
11. **Roaming marker:** a dashed gold ring (`RoamRing` in the Effects atlas, `EffectGen.RoamRing`) under each roaming unit the player owns, drawn with the selection rings (bit 2 of `ClientWorld`'s selected flags).

## Checked in an editor match (2026-10-30)

Eight players (seven AIs) on the 1024 map, through the control API and screenshots: the top bar fits all eight with "…" names and icon-only AI tags; the Roam button shows (greyed with only a Builder selected); "Construction finished: Miner ×2" merged its repeat and its timer bar drained. The HQ flag was too small to see there: a 1024 map's minimap texture is 512 cells shown in 280 px, so the flag now scales with the texture (`MinimapTexture.FlagScale`, 2× at 512; EditMode test). Not seen live: the flag after that fix, the roaming ring and Active Roam button (Roam needs the J key or a click), the player hover card and the "4 / 12" read-out.

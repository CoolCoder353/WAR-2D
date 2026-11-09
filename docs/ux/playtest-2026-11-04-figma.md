# Playtest fixes (2026-11-04): infinite resources, rich gem markers, pings (Figma brief)

The items from the 2026-11-04 playtest that are new UI, drawn before anything is built (the HUD/menu rule). Same look as the earlier playtest pages: Jersey 10, square corners, the `Theme` and `Scale` variables.

- **File:** [WAR-2D v0.6 UX](https://www.figma.com/design/JrMColnJiQbFfODqyPjBYf), the sections starting **Playtest fixes 2026-11-04** on the page **HUD (in match)** (the page **Playtest fixes (2026-11-04)**, node 202:2, until the file was consolidated by area on 2026-11-09).
- **Status:** drawn 2026-11-04, **approved by the owner on 2026-11-04**, K1 and K3 built. K2 superseded the same day (below).

## Frames

| Frame | Node | What it shows |
| ----- | ---- | ------------- |
| K1 · Infinite resources | 202:4 | An **Infinite** option in the lobby's Resources row, the joined player's read-only line, and the top bar's balance reading Infinite. |
| K2 · Rich gem markers | 202:28 | **Superseded (owner, 2026-11-04): no diamonds.** Rich gems use the same indicator as normal gems, in gold: the flat tile colour at far zoom and on the minimap. The far-zoom colour had never worked: its slots stopped one kind short, so rich gems drew as rock (`MapView.FlatType`). |
| K3 · Pings | 202:61 | Middle click: rings in the pinger's colour with their name, a minimap ring, an alert row ("IronFox pinged"). Heard by the pinger and everyone they share vision with. |

## Spectating

The owner, 2026-11-04: the spectator screen follows the replay viewer (v0.18 **R1**, 192:37) instead of `Panel / Spectating – v0.8` (34:206): the player bar on top, and the match time, View selector (Everything or one player's sight) and Leave at the bottom. A live match has no seek bar or speed, and Graphs wait for the end screen. Not drawn in R1, kept from the match HUD: the minimap and the alert feed.

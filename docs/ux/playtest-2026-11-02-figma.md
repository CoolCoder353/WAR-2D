# Playtest fixes (2026-11-02): host tools in the lobby (Figma brief)

The lobby items from the 2026-11-02 playtest that are new UI, drawn before anything is built (the HUD/menu rule). Same look as v0.16 M2 ([v0.16-figma.md](v0.16-figma.md)): Jersey 10, square corners, the `Theme` and `Scale` variables, the `Button` component.

- **File:** [WAR-2D v0.6 UX](https://www.figma.com/design/JrMColnJiQbFfODqyPjBYf), page **Playtest fixes (2026-11-02)** (node 180:368).
- **Status:** drawn 2026-11-02, **approved by the owner on 2026-11-02**, built the same day. Not built: hiding the server from a banned player's join list (see known-issues).

## Frames

| Frame | Node | What it shows |
| ----- | ---- | ------------- |
| K1 · Host tools in the lobby | 180:372 | The M2 host lobby with: a **server name** field beside the title (host only; the subtitle names the server); a **⋯ button** on every other human's card, open on one card with **Force ready** (marks them ready; they can still cancel), **Kick** (removed, may rejoin) and **Ban** (removed, can't rejoin this server); a **hover card** on a match setting (HQ pick time: what it does and what each value means). Players grouped by team and the AI gamer tags, as built. |
| K2 · Kick and Ban, and the server name elsewhere | 180:777 | The **Ban** confirmation (Kick and Force ready don't ask); the Play screen's **Friends' games** list led by each server's name; the Play screen line a **removed player** sees ("You were removed from Oscar's server by the host", or "You are banned from…"), with Dismiss. |

## Notes for building (after approval)

- Every match setting gets a hover card: Map size, Seed, Resources, Options per player, HQ pick time, Visibility, Mode, Diplomacy. Text from `gameplay.md`'s lobby rules.
- Kick, ban, force ready and the server name are host-only `[Command]`s through `CommandGate` with server-side checks (sender is the server owner, the target is a human in the lobby, never the host); a ban lasts while the server is open (by Steam id, or connection address on Direct). The server name is a validated nickname-style string (`CommandValidator`), carried in the Steam lobby data for the join list.
- A kicked or banned connection is told why (a refusal message like the rejoin refusals) before it is disconnected.

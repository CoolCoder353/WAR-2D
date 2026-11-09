# Playtest fixes (2026-11-05): lobby team board, unit cap (Figma brief)

The UI items from the 2026-11-05 playtest, drawn before anything is built (the HUD/menu rule). Same look as the earlier playtest pages: Jersey 10, square corners, the `Theme` and `Scale` variables.

- **File:** [WAR-2D v0.6 UX](https://www.figma.com/design/JrMColnJiQbFfODqyPjBYf), the sections starting **Playtest fixes 2026-11-05** on the pages **Menus & lobby**, **HUD (in match)** and **Components & icons** (the page **Playtest fixes (2026-11-05)**, node 212:2, until the file was consolidated by area on 2026-11-09).
- **Status:** drawn 2026-11-05 and revised the same day after the owner's notes (team rows horizontal, not columns; pick a team by drag or dropdown, no scrolling; a smaller players area in Free-for-all). **Approved 2026-11-05**, except L4's army chip count / cap, which the owner dropped (removed from the frame).

## The problem

"It is hard to assign teams to the bots and stuff." Today a team is a `Solo ▸` pill on each row that the host clicks to cycle Team 1 → 2 → 3 → 4 → Solo, one step per click; the rows are only sorted by team, so who is with whom is read off a list; the local player's own team is a separate picker under the list; and Mode (Free-for-all or Teams), which decides whether teams exist at all, sits at the bottom of the scrolling settings column.

## Frames

| Frame | Node | What it shows |
| ----- | ---- | ------------- |
| L1 · Lobby – team board (host, Teams) | 216:7 | The redo. **Players** on the left as a **team board**: one horizontal **row** per team (Team 1–4), then **Solo** ("everyone is an enemy"), each headed by its name and player count, with its cards side by side and **+ Add AI** at the end of the row (a Normal / Random AI straight into that team). Two ways to change a player's team, both always available: **drag** the card by ⠿ onto another row, or pick the team in the card's **Team ▾ dropdown** (drawn open on Zephyr's card). An empty team shows a dashed drop zone. Eight players at most, so the five rows always fit without scrolling. **Mode** (Free-for-all / Teams) moves up beside the Players title. **Quick setup** buttons: Me vs the AIs, Humans vs AIs, Split evenly, Shuffle, Everyone solo. AI cards keep their Difficulty ▾ and Style ▾ and ×; joined players keep the ⋯ (kick / ban) menu; your own card has the colour ▾ (the separate colour and team pickers under the list go). **Right column:** the map preview (gems red, rich gems gold), caption and Reroll, then every setting without scrolling (size and seed, resources, start options, HQ pick time, visibility), Reset beside the Map heading. **Footer:** Leave, status, Invite friends, Ready, Start match. |
| L2 · Lobby – joined player (Teams) | 216:297 | The same rows for a joined player: only their own card's dropdown (and drag) is live; everyone else's dropdown shows their team greyed out. No Add AI, no quick setup, no Reroll/Reset; Mode reads "Teams (the host's choice)"; settings are read-only (the chosen option lit, the rest disabled). The host can still move them. |
| L3 · Lobby – free-for-all (host) | 223:307 | Free-for-all: no rows and no team dropdowns. The players area shrinks to what it needs (two cards per row, eight at most, plus one **+ Add AI**), and the space goes to a larger map (520 px) beside the settings. Switching back to Teams keeps the teams last set (new players start Solo). |
| L4 · Unit cap reached | 216:892 | A producer at the 10,000-unit cap refunds and keeps its queue silently today. New: an **alert row** "Unit cap reached (10,000): producers wait" (warning, once, again only after dropping below the cap); the producer's production line reads **"Waiting: unit cap (10,000 units)"**. (A count / cap on the top bar's army chip was drawn and dropped by the owner.) |

Components (the section **Playtest fixes 2026-11-05** on the page **Components & icons**): `Lobby / Button` (Kind = default, primary, ghost, disabled), `Lobby / Dropdown` (State = default, open, disabled; Value), `Lobby / Player card` (Name, Detail, Ready, Show remove, and Show team chips, which shows its Team dropdown). (The first draft's team chips were replaced by the dropdown.)

## Open decisions (defaults drawn)

1. **Quick setup**: the five buttons drawn, or fewer? Default: all five. "Split evenly" balances humans and AIs across as many teams as there are humans (at least 2); "Shuffle" keeps team sizes and deals players at random.
2. **Teams beyond 4**: four team rows as today (`TeamRules`), or up to 8? Default: 4.
3. **Drag and dropdown**: both always on, as asked. (A setting that hides one of them is possible, if "the user can choose" meant that.) Default: both, no setting.
4. **Unit cap alert**: also sound? Default: no sound (it's not urgent).

## Code notes (for after approval)

- `LobbyController.ShowRows` builds the board (one row per team, Solo last) instead of the sorted list; `HostTeamClicked(owner, team)` → `GameCore.Cmd_SetTeam` already sets any team, so the dropdown and a drop on a row need no new command. The local player's dropdown and drag use `TeamClicked` (`Cmd_SetOwnTeam`). Add AI per row: `AddAiClicked` gains a team argument and `Cmd_AddAi` takes the team instead of picking the emptiest.
- Quick setup is host-side only: it sends the same `Cmd_SetTeam` per seat that a dropdown pick does (AIs included).
- Unit cap: `SpawnerSystem`'s refund path raises an owner-only notice (as Upkeep unpaid does) and the producer's queue message carries a "capped" flag for the production line.

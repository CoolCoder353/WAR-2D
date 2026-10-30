# Store text (for approval)

The final text for the Steam store page of **War: Weaponry and Resources** (app 1975300), following the [store page plan](../superpowers/specs/2026-10-19-store-page-design.md). **Status: being uploaded to Steamworks (2026-10-22) after the first store review; the owner submits it for review.** A mockup of the page is in [mockup/index.html](mockup/index.html), built by `tools/store/mockup.py`.

Each block is ready to paste into the Steamworks field named in its heading. The long description is in Steam's markup (`[h2]`, `[b]`, `[list]`). GIFs can be added with `[img]` once they're made.

---

## Basic Info

| Field | Value |
| --- | --- |
| Game name | War: Weaponry and Resources |
| Developer | CoolCoder64 |
| Publisher | CoolCoder64 |
| Franchise | (none) |
| Platforms | Windows; Linux + SteamOS |
| Primary genre | Strategy |
| Genres | Strategy, Indie (Steam adds Free to Play and Early Access from the release settings) |
| Players | Multi-player; PvP: Online |
| Languages | English: Interface |
| Supported features | None |
| Support email | weaponryandresourcesgame@gmail.com |
| Legal lines | © 2026 CoolCoder64. All rights reserved. |

## Short description

257 of 300 characters.

```text
A top-down RTS for you and your friends, with armies in the thousands. Dig through rock, wall off chokepoints and hide bombs, then throw everything at your rivals' HQ. Every match is a new cave map. Online multiplayer only for now; AI opponents are planned.
```

## About This Game (long description)

```text
[h2]Your HQ is all you have[/h2]
Every player starts with one Headquarters. Lose it and you're out. Fight over a cave map nobody has seen before, alone or in teams, until one side's HQ is the last one standing.

[h2]Play with friends[/h2]
Right now WAR is played against other people: you need at least one friend online. AI opponents are planned. Host a lobby and invite friends straight from Steam; nobody has to open a port. Colourblind and high-contrast palettes and fully rebindable keys come as standard.

[h2]Send everything[/h2]
There's no unit cap. Box-select the whole army and send it, split it into squads and hit every lane at once, or leave a wall of tanks on a chokepoint while the rest go the long way round. Battles run to thousands of units, and the game keeps up.

[h2]The map is a weapon[/h2]
Caves are generated fresh every match: open clearings, rich expansions and narrow chokepoints between them. Wall a lane shut. Send Diggers through the rock to come up behind someone's defences. Hide bombs where their army will walk, and watch your Radar for the moment it arrives.

[h2]Nobody knows where you started[/h2]
Every player is dealt secret start sites and picks one before the countdown ends. Only your teammates know which. Scouting isn't optional.

[h2]Build for the fight you're in[/h2]
Each factory builds one kind of unit at a time, so every switch is a decision. Fast scouts to find them, heavy armour to break the line, artillery and snipers to pick it apart from range, flamethrowers for whatever gets through.

[h2]Every unit costs you twice[/h2]
Mining gems pays for your army, and upkeep keeps it alive. Build past what your economy can carry and your units start to decay, so a bigger army isn't always a stronger one.

[h2]Alliances that can break[/h2]
Turn diplomacy on and choose who you fight and who you share vision with, then send resources to the friend who's losing. Fog of war hides everything your side can't see, including what your allies are planning.

[h2]Watch it back[/h2]
Every match is recorded. Replay it with the fog lifted, at up to 8× speed, and find out exactly where that army came from.

[h2]Early Access[/h2]
The game is in active development and changes with every update. We plan to add AI opponents, static defences and a tutorial next, and what comes after that is shaped by what players tell us.
```

Notes for the reviewer:

- Single-player and Co-op (against the AI) are added to Players, and Online Co-Op to the tags, when v0.14 (AI opponents) ships. Co-op is left out until then so players don't expect PvE.
- Screenshot 03 isn't uploaded (it repeats 01); the order is 01, 02, 05, 04, 06, 07.
- No counts of units, buildings or players anywhere in the text, since those will change (owner, 2026-10-19). The Players field in Basic Info still says multiplayer.
- Price: the Early Access answer says the game is free and stays free at full release (owner, 2026-10-22, after Valve asked for a pricing direction). The long description still doesn't mention price.
- The "AI opponents" line moves from "Early Access" to its own section once v0.14 ships.
- The Gun turret and the Jammer (v0.13) aren't mentioned until they're released.
- Unit names in the text are lower-case descriptions ("heavy armour", "flamethrowers"), so a renamed or added unit doesn't make the page wrong.

## Early Access questions

Rewritten on 2026-10-22 after Valve's first store review failed the Early Access answers (it read "Fully playable online" and "The core game is done" as finished development, and asked for a length estimate, a pricing direction and a fuller list of what's coming).

**Why Early Access?**

```text
WAR is still in active development. You can mine, build and fight online now, but the unit roster, the map generator and the balance will change a lot before the full release, and we want players to shape those changes. Real matches show us which units are unfair, which maps play well and which features matter most, which internal testing can't.
```

**Approximately how long will this game be in Early Access?**

```text
Roughly 18 to 24 months. The game leaves Early Access once the features listed below are in and the balance has settled; if plans change, we'll say so on the store page and in the community hub.
```

**How is the full version planned to differ from the Early Access version?**

```text
The full version is planned to add:

- AI opponents with difficulty settings, for single-player skirmishes and co-op against the AI
- Static defences: a gun turret to hold a position and a jammer that blinds enemy scouting and radar around your base
- A tutorial that teaches the basics: mining, building, ordering units and digging
- More unit types and more kinds of terrain
- More variety from the map generator: new map sizes and layouts
- Balance and quality-of-life changes driven by player feedback and match replays

We also plan to look at ranked matchmaking. The list will grow and change with what players ask for.
```

**What is the current state of the Early Access version?**

```text
An early but playable build. You can host or join online free-for-all and team matches with friends through Steam, on generated cave maps with secret start points. Diplomacy, walls, bombs, radar, digging and match replays are in. There are no AI opponents or tutorial yet, so you need at least one other player, and you should expect rough balance, bugs and frequent changes between updates.
```

**Will the game be priced differently during and after Early Access?**

```text
No. The game is free to play during Early Access and will stay free to play at full release.
```

**How are you planning on involving the Community in your development process?**

```text
Through the Steam discussions and regular playtests. Balance changes come from what players report and from match replays, and every update's notes say what changed and why.
```

Notes for the reviewer:

- When AI opponents ship (v0.14), the current-state answer drops "There are no AI opponents" and the full-version list keeps only what the AI still lacks (more difficulty settings, co-op tuning).
- The Gun turret and the Jammer are named generically ("a gun turret", "a jammer") so a rename doesn't make the page wrong.

## System requirements

**Windows, minimum**

| Field | Value |
| --- | --- |
| OS | Windows 10 64-bit |
| Processor | Quad-core, 3.0 GHz |
| Memory | 8 GB RAM |
| Graphics | DirectX 11 card with 2 GB VRAM (GeForce GTX 960 / Radeon R9 380 class) |
| DirectX | Version 11 |
| Network | Broadband internet connection |
| Storage | 1 GB available space |
| Additional notes | Mouse and keyboard required. Hosting a large match uses more CPU than joining one. |

**Windows, recommended**

| Field | Value |
| --- | --- |
| OS | Windows 11 64-bit |
| Processor | 6 cores, Ryzen 5 5600 / Core i5-12400 class |
| Memory | 16 GB RAM |
| Graphics | Radeon RX 6600 / GeForce RTX 3060 class |
| DirectX | Version 11 |
| Network | Broadband internet connection |
| Storage | 1 GB available space |

**Linux + SteamOS, minimum**

| Field | Value |
| --- | --- |
| OS | Ubuntu 22.04 64-bit, SteamOS 3 or equivalent |
| Processor | Quad-core, 3.0 GHz |
| Memory | 8 GB RAM |
| Graphics | Vulkan-capable card with 2 GB VRAM |
| Network | Broadband internet connection |
| Storage | 1 GB available space |
| Additional notes | Mouse and keyboard required. |

**Linux + SteamOS, recommended**

| Field | Value |
| --- | --- |
| OS | A current 64-bit distribution |
| Processor | 6 cores, Ryzen 5 5600 / Core i5-12400 class |
| Memory | 16 GB RAM |
| Graphics | Radeon RX 6600 / GeForce RTX 3060 class, Vulkan |
| Network | Broadband internet connection |
| Storage | 1 GB available space |

These are best guesses (owner, 2026-10-19). The recommended rows match the perf gate's reference machine; the minimum rows haven't been tested.

## Controller support

```text
Mouse and keyboard only. No controller support.
```

## Accessibility

| Feature | Note |
| --- | --- |
| Colourblind mode | Okabe–Ito palette for every player colour, unit, building, the minimap and the UI |
| High-contrast mode | Every player colour at least 4.5:1 against the map floor |
| Rebindable controls | Every order key, the queue and squad modifiers, and the menu key |
| Adjustable audio | Master, music and sound-effect volume |
| Camera options | Edge scrolling can be turned off; arrow keys and middle-drag pan |
| No speech | No dialogue, so no subtitles are needed |

## Tags

As applied in Steamworks on 2026-10-22 (the first ones weigh most). Online Co-Op is left off until AI opponents ship:

Resource Management, PvP, Base Building, War, Top-Down, Wargame, RTS, Military, Competitive, Multiplayer, Strategy, 2D, Pixel Graphics, Destruction, Diplomacy, Tanks, Procedural Generation, Team-Based, Early Access, Indie.

"Free to Play" is left out of the tags; Steam shows it from the price anyway while the game is free.

## Content survey: draft answers

The survey's exact questions change; these are the facts to answer it with.

- **Violence:** fantasy military violence. Vehicles and soldiers shoot and explode; there's no blood, gore or human suffering shown. Units are small top-down sprites.
- **Sexual content, nudity, strong language, drugs, gambling:** none.
- **User-generated content:** the player nickname is shown to other players in the lobby and the match. There's no text or voice chat.
- **AI-generated content:** none in the game or on the store page. All art and audio are generated by code written for the game (procedural rendering and synthesis); no AI image, audio or text model made any shipped content (see `docs/art/ai-content.md`). AI coding assistants were used to write code; check whether the survey's current wording asks about that.
- **Online:** online multiplayer through Steam; the host's Steam ID stays on the server for the match only.

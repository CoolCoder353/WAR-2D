# WAR-2D

A networked, top-down 2D real-time strategy game built in Unity, made for armies of up to 10,000 units per player. Two to eight players join a lobby (free-for-all or teams), each places a Headquarters (HQ) in their own clearing of a generated cave map, then they mine gems, build Builders, walls, spawners, hidden bombs and Radars, dig through rock with Diggers and send tanks to destroy each other's HQ. The last side with an HQ standing wins.

- **Engine:** Unity `6000.4.1f1`
- **Networking:** [Mirror](https://mirror-networking.com/) (server-authoritative, KCP transport)
- **Simulation:** Unity DOTS / Entities `6.4.0` (units and buildings are ECS entities, not GameObjects)
- **Target platforms:** Linux and Windows standalone. Build profiles for both are in `Assets/Settings/Build Profiles/`; Linux is the platform the tests and perf gate run on
- **Online play:** Steam (relay, lobbies, invites) through FizzySteamworks, with Direct (KCP) for tests, LAN and perf runs

## Documentation

| Document | What's in it |
|---|---|
| [docs/architecture.md](docs/architecture.md) | How the code fits together: scenes, singletons, ECS systems, networking, visibility, config |
| [docs/gameplay.md](docs/gameplay.md) | Rules of the game, controls, buildings, units, economy, combat, win/loss |
| [docs/known-issues.md](docs/known-issues.md) | Open issues and design requests, plus a log of what past reviews fixed |
| [docs/bug-review.md](docs/bug-review.md) | Code-review findings, how each was triaged, and the low-priority ones still open |
| [docs/debugging.md](docs/debugging.md) | The control API and `tools/war2d` CLI, overlays, `WarLog`, replays, the AI bench |
| [docs/tuning.md](docs/tuning.md) | How to tune balance and the AI: where the numbers are, the balance sheet, the bench, the unattended tuner |
| [docs/playtesting.md](docs/playtesting.md) | How to run a playtest: Steam, and Direct play through a router or tunnel |
| [docs/roadmap.md](docs/roadmap.md) | The current roadmap (v0.10–v0.14: debug tools and replays, new maps, new units, defences, AI opponents; v0.15 quality of life and v0.15.1 followed), with a design spec per update |
| [docs/archive/](docs/archive/roadmap-v0.2-v0.9.md) | The completed v0.2–v0.9 roadmap, its specs, plans, spike and performance results |
| [steam/README.md](steam/README.md) | Uploading builds to Steam with `tools/steam-upload.sh` |
| [CLAUDE.md](CLAUDE.md) | Guidance for AI coding assistants working in this repo |

## Getting started

### Requirements

- Unity Hub with editor **6000.4.1f1** (the version pinned in `ProjectSettings/ProjectVersion.txt`)
- The Linux and/or Windows build support modules, if you want to make builds

All other dependencies are either Unity packages (resolved from `Packages/manifest.json` on first open, Steamworks.NET included) or vendored under `Assets/` (Mirror, FizzySteamworks, DOTween, NaughtyAttributes, TIM Console).

### Open and run

1. Clone the repo and open the folder in Unity Hub.
2. Let the first import finish (Entities and Burst compilation takes a while).
3. Open `Assets/Main_Menu.unity` and press **Play**.

### Play a multiplayer match

Mirror needs two running instances: one host and one or more clients.

1. Make a standalone build (see below), or run a second editor instance on a copy of the project.
2. In the first instance press **Play**, then **Host**. This starts a server and a local client at the same time.
3. **With Steam**, friends join from the Play screen's *Friends' games* list or an invite (Steam relay, no port forwarding). **Without Steam** (editor, `-direct` or `-noSteam` runs, tests), open **Join** and enter the host's IP (`localhost` on the same machine). Direct connections use UDP port **7778** (KCP transport); a release build shows the IP field only when started with `-direct`. For play across networks without Steam, see [docs/playtesting.md](docs/playtesting.md).
4. Players can rename themselves in the lobby. Only the **server owner** (the host, or the next player if the host leaves) sees the **Start Game** button.
5. Starting the game loads the configured map (`Map_2`, generated from a seed), and every player places their HQ at one of the start options they are dealt (AI players place theirs at once; when the host's pick time runs out, the rest are placed at random). Once every HQ is down, a 5-second countdown runs and the match begins.

See [docs/gameplay.md](docs/gameplay.md#controls) for in-game controls.

### Build

The scenes in the build are `Assets/Main_Menu.unity` (index 0) and `Assets/Maps/Map_2.unity` (index 1).

- **Linux** and **Windows:** build profiles are checked in at `Assets/Settings/Build Profiles/` (`Linux.asset`, `Windows.asset`). Open one under *File → Build Profiles* and build.
- **Steam:** `tools/steam-upload.sh <branch>` builds fresh Linux and Windows depots headless (close the editor first) and uploads them with `steamcmd`. Setup is in [steam/README.md](steam/README.md).

There is no dedicated server: a batch-mode player is a test or perf client, and `GameManager` sets `headlessStartMode` to `DoNothing` so none of them starts hosting on its own. The host is always a player (Mirror host), and when the host leaves the match ends; host migration and a dedicated build are not built (see [roadmap](docs/roadmap.md) section 5). A match takes 2 to 8 players.

## Tests

The game code lives in its own `WAR2D` assembly with EditMode and PlayMode test assemblies. Run them through the open Unity editor (there is no CI):

```bash
tools/unity-test.sh EditMode
tools/unity-test.sh PlayMode
```

`tools/unity-compile.sh` runs a compile check first. `tools/qa-smoke.sh` runs a two-process host and join check, and `tools/perf-run.sh` runs the performance gate against a Linux release build.

## Balancing

Gameplay numbers are set in `Assets/Resources/GameConfig.xml`, which is read at runtime via `Config.ConfigLoader`. It is the single source of every balance value, AI behaviour included, and is validated strictly at startup: an invalid config refuses to host.

To change them, see [docs/tuning.md](docs/tuning.md):
- `node tools/balance-sheet.js` works out unit numbers and equal-cost fights from the config alone, in under a second.
- `tools/ai-bench.sh --brief` plays AI-vs-AI matches against a Linux build and prints a short summary.
- `node tools/ai-tune.js <spec.json>` tries listed values unattended and confirms the best.

## Project layout

```
Assets/
├── Main_Menu.unity              Lobby scene (GameManager, UI documents, console)
├── Maps/Map_2.unity             The playable map (WorldStateManager, UnitCommander, HUD)
├── Player.prefab                Networked player object (ClientPlayer)
├── Resources/                   Runtime-loaded assets: GameConfig.xml, generated art and audio, GameCore.prefab
├── Scripts/                     Game code (WAR2D assembly)
│   ├── GameManager.cs           Mirror NetworkManager: connect/host/join/leave, scene hooks
│   ├── GameCore.cs (+ Net/*Commands.cs)  Server game state machine, lobby, players, win/loss
│   ├── ClientPlayer.cs          Per-player NetworkBehaviour: SyncVars, RPCs
│   ├── ServerPlayer.cs          Server-only player data (resources, state)
│   ├── Ai/                      The AI brain and managers (own WAR2D.Ai assembly)
│   ├── AiView/                  The AI's team view (what its side sees) and `AiRunner`, its link to the match
│   ├── Unit/                    WorldStateManager: map, ids, buildings, order commands
│   ├── Sim/                     The 20 Hz unit simulation: tick stages, SimData, orders, vision
│   ├── Pathing/                 Sector graph and flow fields
│   ├── World/                   Map grid, store and generator
│   ├── Building/                Mining component and building placement helpers
│   ├── Components/              ECS component structs (buildings, health, upkeep)
│   ├── Systems/                 ECS systems for buildings: resources, spawning, destruction, win/loss
│   ├── Net/                     Command gate, rate limiter, validator, lobby/match/order/siege commands, UPnP
│   ├── Net/Steam/               Connection routes, Steam lobbies, auth tickets, behind ISteamBackend
│   ├── Steam/                   The real Steamworks backend (own WAR2D.Steam assembly, WAR2D_STEAM define)
│   ├── Net/Replication/         Per-client interest, encoder, fog and building messages
│   ├── Client/                  Presentation: UnitCommander, input, camera, instanced rendering, effects
│   ├── UI/                      UI Toolkit controllers and models (menu, lobby, HUD, settings)
│   ├── Art/, Audio/             Procedural art and sound generators
│   ├── Config/                  GameConfig data classes + strict XML parser/loader
│   ├── Rules/                   Pure rule helpers (placement, combat, upkeep, win/loss, ...)
│   ├── Editor/                  Editor menus: art and audio builds, build commands, Steam setup
│   └── Dev/                     Perf harness, AI bench and QA helpers (need `-perf`)
├── Tests/                       EditMode and PlayMode test assemblies
├── 3rd Party/                   TIM Console (+ custom commands), FizzySteamworks, NaughtyAttributes
├── Mirror/                      Vendored Mirror networking
└── Plugins/Demigiant/DOTween/   Vendored DOTween (client tweening)
```

## Licensing

The project has no licence file yet. The vendored components (Mirror, DOTween, NaughtyAttributes, TIM Console) keep their own licences in their folders.

## Contributors

See the git history.

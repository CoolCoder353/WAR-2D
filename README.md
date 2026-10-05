# WAR-2D

A networked, top-down 2D real-time strategy game built in Unity. Any number of players join a lobby, each places a Headquarters (HQ) on the map, then they mine gems, build unit spawners and send tanks to destroy each other's HQ. The last player with an HQ standing wins.

- **Engine:** Unity `6000.4.1f1`
- **Networking:** [Mirror](https://mirror-networking.com/) (server-authoritative, KCP transport)
- **Simulation:** Unity DOTS / Entities `6.4.0` (units and buildings are ECS entities, not GameObjects)
- **Target platforms:** Linux standalone for now; a Windows build profile is deferred to a later release (see [known issues](docs/known-issues.md#project-and-config-hygiene))

## Documentation

| Document | What's in it |
|---|---|
| [docs/architecture.md](docs/architecture.md) | How the code fits together: scenes, singletons, ECS systems, networking, visibility, config |
| [docs/gameplay.md](docs/gameplay.md) | Rules of the game, controls, buildings, units, economy, combat, win/loss |
| [docs/known-issues.md](docs/known-issues.md) | Deferred issues and hygiene leftovers, each with a target version |
| [docs/roadmap.md](docs/roadmap.md) | Development plan for v0.2–v0.9: what each update delivers, why, and when it's done |
| [CLAUDE.md](CLAUDE.md) | Guidance for AI coding assistants working in this repo |

## Getting started

### Requirements

- Unity Hub with editor **6000.4.1f1** (the version pinned in `ProjectSettings/ProjectVersion.txt`)
- The Linux and/or Windows build support modules, if you want to make builds

All other dependencies are either Unity packages (resolved from `Packages/manifest.json` on first open) or vendored under `Assets/` (Mirror, DOTween, NaughtyAttributes, TIM Console).

### Open and run

1. Clone the repo and open the folder in Unity Hub.
2. Let the first import finish (Entities and Burst compilation takes a while).
3. Open `Assets/Main_Menu.unity` and press **Play**.

### Play a local multiplayer match

Mirror needs two running instances: one host and one or more clients.

1. Make a standalone build (see below), or run a second editor instance on a copy of the project.
2. In the first instance press **Host**. This starts a server and a local client at the same time.
3. In the other instance(s) open **Join**, enter the host's IP (`localhost` on the same machine) and connect. Players connect to UDP port **7778** (KCP transport).
4. Players can rename themselves in the lobby. Only the **server owner** (the host, or the next player if the host leaves) sees the **Start Game** button.
5. Starting the game loads the configured map (`Map_2`), and every player places their HQ. Once everyone has placed one, a 5-second countdown runs and the match begins.

See [docs/gameplay.md](docs/gameplay.md#controls) for in-game controls.

### Build

The scenes in the build are `Assets/Main_Menu.unity` (index 0) and `Assets/Maps/Map_2.unity` (index 1).

- **Linux:** a build profile is checked in at `Assets/Settings/Build Profiles/Linux.asset`. Open it under *File → Build Profiles* and build.
- **Windows:** no profile is checked in — the owner deferred Windows to a later release. To build one yourself, create a Windows build profile with the same two scenes.

Headless/dedicated server hosting is not configured yet; it is planned in the [roadmap](docs/roadmap.md).

## Tests

The game code lives in its own `WAR2D` assembly with EditMode and PlayMode test assemblies. Run them through the open Unity editor (there is no CI):

```bash
tools/unity-test.sh EditMode
tools/unity-test.sh PlayMode
```

`tools/unity-compile.sh` runs a compile check first.

## Balancing

Gameplay numbers are set in `Assets/Resources/GameConfig.xml`, which is read at runtime via `Config.ConfigLoader`. It is the single source of every balance value and is validated strictly at startup — an invalid config refuses to host.

## Project layout

```
Assets/
├── Main_Menu.unity              Lobby scene (GameManager, GameCore, LobbySystem, console)
├── Maps/Map_2.unity             The playable map (WorldStateManager, UnitCommander, game UI)
├── Player.prefab                Networked player object (ClientPlayer)
├── Resources/                   Runtime-loaded assets: GameConfig.xml, sprites, UI prefabs, tiles
├── Scripts/                     Game code (WAR2D assembly)
│   ├── GameManager.cs           Mirror NetworkManager: connect/host/join/leave, scene hooks
│   ├── GameCore.cs              Server game state machine, players, win/loss
│   ├── ClientPlayer.cs          Per-player NetworkBehaviour: SyncLists, RPCs
│   ├── ServerPlayer.cs          Server-only player data (resources, state)
│   ├── LobbySystem.cs           Lobby UI
│   ├── Unit/                    WorldStateManager + pathfinding
│   ├── Building/                Building components, placement UI (BuildingButtonManager)
│   ├── Components/              ECS component structs (units, buildings, health, upkeep)
│   ├── Systems/                 ECS systems: movement, combat, resources, spawning, win/loss, destruction
│   ├── Client/                  Client presentation: UnitCommander, input, camera, effects, UI
│   ├── Config/                  GameConfig data classes + strict XML parser/loader
│   ├── Rules/                   Pure rule helpers (placement, combat, upkeep, win/loss, …)
│   ├── Net/                     Command gate, rate limiter, validator, tile occupancy, id allocator
│   └── UI/                      HQ placement screen, menu wiring, exit/return buttons
├── Tests/                       EditMode and PlayMode test assemblies
├── 3rd Party/                   TIM Console (+ custom commands), NaughtyAttributes
├── Mirror/                      Vendored Mirror networking
└── Plugins/Demigiant/DOTween/   Vendored DOTween (client tweening)
```

## Contributors

See the git history.

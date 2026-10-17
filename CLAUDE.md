# CLAUDE.md

Guidance for AI coding assistants working in this repository.

## What this is

WAR-2D is a Unity `6000.4.1f1` multiplayer 2D RTS (free-for-all or teams, 2 to 8 players, Linux and Windows builds; the tests and perf gate run on Linux). It uses **Mirror** for networking (server-authoritative; Steam relay through FizzySteamworks for online play, KCP on port 7778 as "Direct" for tests, perf and LAN) and **Unity Entities (DOTS)** for the simulation. The current plan is [docs/roadmap.md](docs/roadmap.md) (v0.10 to v0.13), with a design spec per update in `docs/superpowers/specs/`; the completed v0.2 to v0.9 roadmap and its specs and plans are in `docs/archive/`. Read these before making non-trivial changes:

- [docs/architecture.md](docs/architecture.md): how the systems connect, the networking reference, the ECS data model
- [docs/gameplay.md](docs/gameplay.md): intended rules and numbers
- [docs/known-issues.md](docs/known-issues.md): known bugs and mismatches. **Check here before "fixing" odd behaviour.** It may already be listed, or be load-bearing.
- [docs/bug-review.md](docs/bug-review.md): open code-review findings (2026-10-14 general scan plus unit-system re-scan). Check here for unfixed bugs before changing Sim, Net, replication, or unit code.

## Git commits

- **Never add co-author attribution to commits.** No `Co-Authored-By:` trailers, no "Generated with" lines, and no other AI attribution in commit messages or PR descriptions. This applies to every commit, with no exceptions.
- Only commit when asked. Don't commit from `main`; create a branch first.

## Working in this repo

- Game code is in `Assets/Scripts/`. **Don't edit vendored code** in `Assets/Mirror/`, `Assets/Plugins/Demigiant/`, `Assets/3rd Party/NaughtyAttributes-*/` or `Assets/3rd Party/Console/Scripts/` unless asked. Custom console commands go in `Assets/3rd Party/Console/Custom Commands/Custom_Commands.cs`.
- There's no CI. Compile and test through the **live Unity editor**: `tools/unity-compile.sh` for a compile check, `tools/unity-test.sh EditMode` and `tools/unity-test.sh PlayMode` to run the test suites (both must pass before a change lands). Unity owns the `.csproj`/`.slnx` files; don't hand-edit them.
- Scene, prefab and `.asset` files are YAML. Avoid hand-editing them. If you must, keep `.meta` GUIDs intact. Every new asset needs its `.meta` (Unity creates it on import).
- To check behaviour in a running game, use the control API (`tools/war2d`, [docs/debugging.md](docs/debugging.md)) rather than asking the owner to click through it.
- Gameplay numbers belong in `Assets/Resources/GameConfig.xml`, read through `Config.ConfigLoader.LoadConfig()`. It is the single source of balance values, parsed strictly; don't add new hard-coded balance values, and check `ConfigLoader.IsValid` before hosting.

## Architecture rules of thumb

- **The server owns the world.** Units and buildings are ECS entities created only on the server, and only by `SimCommandSystem` at a tick boundary (units from `SpawnUnit`, buildings from `CreateBuilding`, which `WorldStateManager.TryAddBuilding` queues after validating). Clients never touch the ECS world.
- **The tick is asynchronous.** `SimTickGroup` (20 Hz) schedules Burst jobs that run between ticks; `SimBoundarySystem` settles them at the next tick. **Main-thread code never touches `Unit` entities or the `SimData` arrays between ticks** (it would stall on, or race with, the jobs): it queues a `SimCommand` on `SimCommandQueue` instead. That includes the map's `Used` footprint flags: use `MapStore.QueueUsed` (applied at the boundary), never `SetUsed`, from match code. Code that must read the settled world subscribes to `SimContext.Settled`. New tick stages declare write access to `Unit` (see `SimData`) and never complete their own jobs. Tick order: boundary, commands, gather, hash, bombs, combat, movement, construction, dig, vision, economy, lifecycle.
- **Clients only see what their team sees.** Units reach clients through `ReplicationService` (routes + corrections; interest = own team's units + units on fog cells the team sees now, from `SimVisionSystem`) and are predicted and drawn by `ClientWorld`. The camera box only tunes correction fidelity. Buildings reach clients as per-client `BuildingBatch` records (`BuildingInterest`, with last-seen ghosts) into `ClientBuildings`, drawn instanced by `InstancedBuildingRenderer` and picked by tile with `BuildingPicker` (no GameObject per building); fog reaches them as `FogBatch` (own team's grid only). Bombs go only to their owner and the share-vision set (never as ghosts), and terrain and wall changes only for cells the team sees. Anything new sent to clients must respect the same rule, and `LeakTests` must keep passing. To show new unit data, add it to the replication messages (`Net/Replication/Messages.cs`, both the encoder and `ClientUnitStore`). Don't add networked GameObjects per entity.
- **All client actions are Commands** on `WorldStateManager` or `GameCore` with `requiresAuthority = false` and a `NetworkConnectionToClient sender = null` parameter. **Every `[Command]` must call `CommandGate.Allow(sender, nameof(...))` first and validate its arguments server-side** — ownership (`ownerId == netId` cast with `BuildingData.UIntToInt`), game state, and value/box ranges (`CommandValidator`). Never trust a client argument.
- **Private per-player data** (resources) goes in `ServerPlayer` and reaches the owning client via TargetRpc, never a SyncVar.
- **Singletons:** `GameManager` (`DontDestroyOnLoad`, in `Main_Menu`), `GameCore` (spawned by the server from `Resources/Network/GameCore.prefab`, then `DontDestroyOnLoad`; it exists only while hosting or joined), `WorldStateManager` and `UnitCommander` (in `Map_2`). Null-check `.Instance` in code that can run during scene transitions. Don't add networked scene objects that move themselves to `DontDestroyOnLoad`: the editor's first scene never gets Mirror's scene ids (see `known-issues.md`).
- **IDs:** entity `id` comes from `WorldStateManager.Ids` (`NetIdAllocator`: `(generation << 20) | index`, indices are reused with a new generation), and `ownerId` is the player's `netId` as int. Per-unit arrays are indexed by `NetIdAllocator.IndexOf(id)`. Don't confuse either with ECS `Entity.Index`.
- **Steam** code sits behind `ISteamBackend` (`Net/Steam/`); the real backend is the separate `WAR2D.Steam` assembly (define `WAR2D_STEAM`), and tests use `FakeSteamBackend`, so nothing but a release build needs the Steam client. Direct (KCP) connections need the editor, `-perf` or `-direct`.
- **Dev-only APIs** (bots, free units and buildings for the perf harness) check `DevApi.Allowed` and refuse without `-perf`.
- **Teams and diplomacy:** `GameCore.Teams` (owner → team, fixed at match start) is only the *starting* state. Who attacks and shares vision with whom lives in `SimContext.Diplomacy` (per owner slot, one-way), copied to `SimData.AttackMask` / `ShareVisionMask` at the boundary; change it only through the `SetAttack` / `SetShareVision` `SimCommand`s. "Enemy" means an owner on my attack mask, never just a different owner or team. A player learns only their own row (`ClientPlayer.TargetDiplomacy`).
- **Game state** is `GameCore.CurrentState`. Resource and win/loss systems only run in `GameState.Playing`.

## Adding content

- **New unit type:** add it to the `UnitType` enum (`Components/UnitComponents.cs`), add a `<Unit type="...">` to `GameConfig.xml` (including `Radius` and `SizeClass`) and its `<DamageTable>` entries. Then say which building makes it in `SpawnerRules.Produces` (the HQ makes Builders, the Spawner Tanks and Diggers), and give the command card a production row if needed. Its art comes from a generator in `RenderGen` registered in `Editor/ArtBuild.cs` (until then it draws as a placeholder); rebuild the atlases with `WAR-2D/Art/Build all`.
- **New building type:** add it to the `BuildingType` enum (`Components/BuildingComponents.cs`), add a `<Building type="...">` to `GameConfig.xml` with its `Width`/`Height` footprint, add an `ArtManifest` entry and a generator in `RenderGen` registered in `Editor/ArtBuild.cs` (then `WAR-2D/Art/Build all`), add a `case` in `SimCommandSystem.CreateBuilding` for its components, and note it starts as a blueprint (`Construction`) built by Builders, add a build button in `Assets/UI/Hud/CommandCard.uxml` (with an icon in `CommandIcon`, drawn from the Figma icon) wired in `CommandCardController`; HUD changes go through Figma first. For client click behaviour, map it to a component in `UnitCommander.BuildingListInsert`.
- **New tile type:** add it to `TileType` and give every `TileRules` query (floor, solid, opaque) a case, plus `TileAutotile`/`MapView` art.
- **New ECS system:** for building logic, add a `partial struct : ISystem` in `Assets/Scripts/Systems/` gated on `GameCore.Instance.CurrentState`. For unit logic, add a tick stage in `Assets/Scripts/Sim/` (`[UpdateInGroup(typeof(SimTickGroup))]`, return when `SimClock.Running` is false, schedule Burst jobs over `SimData` on `state.Dependency`); structural changes to units go through `SimCommand`s.
- **Art and sound:** nothing under `Assets/Resources` is hand-made (`NoPlaceholderArtTests`). Art is an `ArtManifest` entry plus a generator (`RenderGen`, `EffectGen`), built into the atlases by `WAR-2D/Art/Build all`; UI icons and cursors are the bitmaps in `Art/PixelIcons.cs`. A sound effect is a JSON parameter set in `Assets/Art/Audio/sfx/` built by `WAR-2D/Audio/Build SFX`; music is a JSON song spec in `Assets/Art/Audio/music/` built by `WAR-2D/Audio/Build music`. Gameplay sounds go through `AudioService` (it only follows events the client already draws).
- **Performance:** `tools/perf-run.sh` runs the §4.1 gate against a Linux release build (`Builds/Linux/WAR-2D.x86_64`). Re-run it after changes to the tick, pathing or replication.

## Style

- Match the surrounding code: XML `/// <summary>` comments on public members, PascalCase methods, Mirror attributes (`[Server]`, `[Client]`, `[ServerCallback]`, `[ClientCallback]`) on every networked method.
- Logging uses `WarLog` with a category (`WarLog.Info(LogCategory.Net, ...)`); never `Debug.Log` in game code.
- Keep the docs in `docs/` up to date when you change behaviour, especially `known-issues.md` when you fix or find an issue.

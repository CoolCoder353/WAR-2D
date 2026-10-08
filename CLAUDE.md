# CLAUDE.md

Guidance for AI coding assistants working in this repository.

## What this is

WAR-2D is a Unity `6000.4.1f1` multiplayer 2D RTS (free-for-all, any player count, Linux + Windows). It uses **Mirror** for networking (server-authoritative, KCP on port 7778) and **Unity Entities (DOTS)** for the simulation. Read these before making non-trivial changes:

- [docs/architecture.md](docs/architecture.md): how the systems connect, the networking reference, the ECS data model
- [docs/gameplay.md](docs/gameplay.md): intended rules and numbers
- [docs/known-issues.md](docs/known-issues.md): known bugs and mismatches. **Check here before "fixing" odd behaviour.** It may already be listed, or be load-bearing.

## Git commits

- **Never add co-author attribution to commits.** No `Co-Authored-By:` trailers, no "Generated with" lines, and no other AI attribution in commit messages or PR descriptions. This applies to every commit, with no exceptions.
- Only commit when asked. Don't commit from `main`; create a branch first.

## Working in this repo

- Game code is in `Assets/Scripts/`. **Don't edit vendored code** in `Assets/Mirror/`, `Assets/Plugins/Demigiant/`, `Assets/3rd Party/NaughtyAttributes-*/` or `Assets/3rd Party/Console/Scripts/` unless asked. Custom console commands go in `Assets/3rd Party/Console/Custom Commands/Custom_Commands.cs`.
- There's no CI. Compile and test through the **live Unity editor**: `tools/unity-compile.sh` for a compile check, `tools/unity-test.sh EditMode` and `tools/unity-test.sh PlayMode` to run the test suites (both must pass before a change lands). Unity owns the `.csproj`/`.slnx` files; don't hand-edit them.
- Scene, prefab and `.asset` files are YAML. Avoid hand-editing them. If you must, keep `.meta` GUIDs intact. Every new asset needs its `.meta` (Unity creates it on import).
- Gameplay numbers belong in `Assets/Resources/GameConfig.xml`, read through `Config.ConfigLoader.LoadConfig()`. It is the single source of balance values, parsed strictly; don't add new hard-coded balance values, and check `ConfigLoader.IsValid` before hosting.

## Architecture rules of thumb

- **The server owns the world.** Units and buildings are ECS entities created only on the server, and only by `SimCommandSystem` at a tick boundary (units from `SpawnUnit`, buildings from `CreateBuilding`, which `WorldStateManager.TryAddBuilding` queues after validating). Clients never touch the ECS world.
- **The tick is asynchronous.** `SimTickGroup` (20 Hz) schedules Burst jobs that run between ticks; `SimBoundarySystem` settles them at the next tick. **Main-thread code never touches `Unit` entities or the `SimData` arrays between ticks** (it would stall on, or race with, the jobs): it queues a `SimCommand` on `SimCommandQueue` instead. Code that must read the settled world subscribes to `SimContext.Settled`. New tick stages declare write access to `Unit` (see `SimData`) and never complete their own jobs.
- **Clients only see what's in their camera box.** Units reach clients through `ReplicationService` (routes + corrections, interest = own units + the camera box) and are predicted and drawn by `ClientWorld`. Buildings reach clients through the `ClientPlayer` SyncLists (`WorldStateManager.UpdatePlayerViews`) and are GameObjects. To show new unit data, add it to the replication messages (`Net/Replication/Messages.cs`, both the encoder and `ClientUnitStore`). Don't add networked GameObjects per entity.
- **All client actions are Commands** on `WorldStateManager` or `GameCore` with `requiresAuthority = false` and a `NetworkConnectionToClient sender = null` parameter. **Every `[Command]` must call `CommandGate.Allow(sender, nameof(...))` first and validate its arguments server-side** — ownership (`ownerId == netId` cast with `BuildingData.UIntToInt`), game state, and value/box ranges (`CommandValidator`). Never trust a client argument.
- **Private per-player data** (resources) goes in `ServerPlayer` and reaches the owning client via TargetRpc, never a SyncVar.
- **Singletons:** `GameManager`, `GameCore` (both `DontDestroyOnLoad`, in `Main_Menu`), `WorldStateManager` and `UnitCommander` (in `Map_2`). Null-check `.Instance` in code that can run during scene transitions.
- **IDs:** entity `id` comes from `WorldStateManager.Ids` (`NetIdAllocator`: `(generation << 20) | index`, indices are reused with a new generation), and `ownerId` is the player's `netId` as int. Per-unit arrays are indexed by `NetIdAllocator.IndexOf(id)`. Don't confuse either with ECS `Entity.Index`.
- **Dev-only APIs** (bots, free units and buildings for the perf harness) check `DevApi.Allowed` and refuse without `-perf`.
- **Game state** is `GameCore.CurrentState`. Resource and win/loss systems only run in `GameState.Playing`.

## Adding content

- **New unit type:** add it to the `UnitType` enum (`Components/UnitComponents.cs`), add a `<Unit type="...">` to `GameConfig.xml` (including `Radius` and `SizeClass`) and its `<DamageTable>` entries, and put a texture named exactly like the enum value at the root of `Assets/Resources/`. Then hook a spawner up to it (`SpawnerData.unitType` in `SimCommandSystem.CreateBuilding`). The instanced renderer currently draws every unit with the Tank texture; per-type textures come with the art pass.
- **New building type:** add it to the `BuildingType` enum (`Components/BuildingComponents.cs`), add a `<Building type="...">` to `GameConfig.xml` with its `Width`/`Height` footprint, add a sprite with that name in `Assets/Resources/`, add a `case` in `SimCommandSystem.CreateBuilding` for its components, and add a HUD button and entry in the `BuildingButtonManager` lists in `Map_2`. For client click behaviour, map it to a component in `UnitCommander.BuildingListInsert`.
- **New ECS system:** for building logic, add a `partial struct : ISystem` in `Assets/Scripts/Systems/` gated on `GameCore.Instance.CurrentState`. For unit logic, add a tick stage in `Assets/Scripts/Sim/` (`[UpdateInGroup(typeof(SimTickGroup))]`, return when `SimClock.Running` is false, schedule Burst jobs over `SimData` on `state.Dependency`); structural changes to units go through `SimCommand`s.
- **Performance:** `tools/perf-run.sh` runs the §4.1 gate against a Linux release build (`Builds/Linux/WAR-2D.x86_64`). Re-run it after changes to the tick, pathing or replication.

## Style

- Match the surrounding code: XML `/// <summary>` comments on public members, PascalCase methods, Mirror attributes (`[Server]`, `[Client]`, `[ServerCallback]`, `[ClientCallback]`) on every networked method.
- Logging uses `Debug.Log`. Network-related logs sometimes use `TIM.Console.Log(..., TIM.MessageType.Network)`.
- Keep the docs in `docs/` up to date when you change behaviour, especially `known-issues.md` when you fix or find an issue.

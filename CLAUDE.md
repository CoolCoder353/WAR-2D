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

- **The server owns the world.** Units and buildings are ECS entities created only on the server: buildings in `WorldStateManager.TryAddBuilding`, units in `SpawnerSystem.CreateUnit`. Clients never touch the ECS world.
- **Clients only see what's in their camera box.** `WorldStateManager.UpdatePlayerViews` copies visible `ClientUnit` / `BuildingData` / `HealthComponent` structs into the `ClientPlayer` SyncLists. `UnitCommander` turns SyncList hooks into GameObjects. To show new data on clients, add it to one of these structs or lists. Don't add new networked GameObjects per entity.
- **All client actions are Commands** on `WorldStateManager` or `GameCore` with `requiresAuthority = false` and a `NetworkConnectionToClient sender = null` parameter. **Every `[Command]` must call `CommandGate.Allow(sender, nameof(...))` first and validate its arguments server-side** — ownership (`ownerId == netId` cast with `BuildingData.UIntToInt`), game state, and value/box ranges (`CommandValidator`). Never trust a client argument.
- **Private per-player data** (resources) goes in `ServerPlayer` and reaches the owning client via TargetRpc, never a SyncVar.
- **Singletons:** `GameManager`, `GameCore` (both `DontDestroyOnLoad`, in `Main_Menu`), `WorldStateManager` and `UnitCommander` (in `Map_2`). Null-check `.Instance` in code that can run during scene transitions.
- **IDs:** entity `id` comes from `WorldStateManager.Ids` (`NetIdAllocator` — unique per match), and `ownerId` is the player's `netId` as int. Don't confuse either with ECS `Entity.Index`.
- **Game state** is `GameCore.CurrentState`. Resource and win/loss systems only run in `GameState.Playing`.

## Adding content

- **New unit type:** add it to the `UnitType` enum (`Components/UnitComponents.cs`), add a `<Unit type="...">` to `GameConfig.xml`, and put a sprite named exactly like the enum value at the root of `Assets/Resources/`. Then hook a spawner up to it (`SpawnerData.unitType` in `TryAddBuilding`).
- **New building type:** add it to the `BuildingType` enum (`Components/BuildingComponents.cs`), add a `<Building type="...">` to `GameConfig.xml` with its `Width`/`Height` footprint, add a sprite with that name in `Assets/Resources/`, add a `case` in `WorldStateManager.TryAddBuilding` for its components, and add a HUD button and entry in the `BuildingButtonManager` lists in `Map_2`. For client click behaviour, map it to a component in `UnitCommander.BuildingListInsert`.
- **New ECS system:** add a `partial struct : ISystem` in `Assets/Scripts/Systems/`. Gate it on `GameCore.Instance.CurrentState` if it should only run during play, and use an `EntityCommandBuffer` for structural changes inside queries.

## Style

- Match the surrounding code: XML `/// <summary>` comments on public members, PascalCase methods, Mirror attributes (`[Server]`, `[Client]`, `[ServerCallback]`, `[ClientCallback]`) on every networked method.
- Logging uses `Debug.Log`. Network-related logs sometimes use `TIM.Console.Log(..., TIM.MessageType.Network)`.
- Keep the docs in `docs/` up to date when you change behaviour, especially `known-issues.md` when you fix or find an issue.

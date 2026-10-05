# Known Issues and Tech Debt

These were found by reading the code (October 2026). Most haven't been reproduced in play. File and line references point at the code as it was when this was written.

## Config and balance mismatches

| Issue | Where | Effect |
|---|---|---|
| Building costs are read from `Resources/Data/ResourceConfig.xml`, which **doesn't exist**. `ResourceConfigLoader` logs an error and uses its defaults. | `Scripts/Data/ResourceConfigLoader.cs`, used by `WorldStateManager.TryAddBuilding` | Every building costs **100** upfront and **5/s** upkeep (Base: 0/s), whatever `GameConfig.xml` says. The HQ costs 100 instead of 0. |
| `GameManagerSettings.initalResources` / `coreBaseName` aren't used. | `Scripts/Data/GameManagerSettings.cs` | Changing `Game_Manager_Settings.asset` does nothing. Starting resources come from `GameConfig.xml`. |
| `SpawnRate` and per-building `MiningRate` in `GameConfig.xml` aren't read by any system. | `Config/GameConfig.cs`, `Systems/` | Spawners spawn as fast as the queue and resources allow. All miners use the global `Resources/MiningRate`. |
| Unit range (5), attack interval (1 s), acceleration and rotation values are hard-coded. | `Systems/SpawnerSystem.cs` `CreateUnit` | Can't be tuned from config. |

## Gameplay and logic bugs

| Issue | Where | Effect |
|---|---|---|
| `IsAvaliable` ignores the real result and always sets `isAvaliable = true`. | `Systems/MovementSystem.cs` | Units never wait for an occupied tile, so they can overlap. |
| `ClientUnit.targetId` is set to the target's **ECS `Entity.Index`**, but the client looks it up in `unitGameObjects`, which is keyed by unit **`id`**. | `CombatSystem.cs` vs `UnitCommander.VisualizeAttackingUnits` | Attack tracers rarely or never render, and never for building targets. |
| Entities destroyed by combat aren't removed from `WorldStateManager.Units/Buildings`, and a destroyed building's tiles stay `used`. | `CombatSystem`, `DestructionSystem`, `WorldStateManager` | Stale registry entries. You can't build again where a building died. |
| Disconnected players are marked `Eliminated` but **never removed** from `GameCore.ServerPlayers`. | `GameCore.OnPlayerLeave` | `UpdateClientsPrivateData` and others call `GetComponent` on the destroyed player object. Player counts used for win checks include players who have left. |
| The server's Countdown lasts 3 s, but the client UI counts down 5 s and then sends `Cmd_ReadyToStartGame`. | `GameCore.CheckHQPlacementProgress`, `UI/HQPlacementUI.cs` | The game becomes *Playing* about 2 s before the UI says so. `Cmd_ReadyToStartGame` is effectively redundant. |
| `playersReadyToStart` is never reset. | `GameCore` | A second match in the same session may skip its ready gate. |
| A draw is `DeclareWinner(-1)`. `RpcGameDraw` is never called. | `GameCore.DeclareDraw` | A draw shows every player the Lose screen. |
| Passive income still goes to eliminated players. | `ResourceSystem.HandlePassiveGeneration` | Harmless today, but wrong if spectating is added. |
| Upkeep is silently skipped when a player can't afford it. | `ResourceSystem.ProcessDeduction` | No penalty for running out. Confirm whether this is intended. |
| `TryAddBuilding` doesn't check `GameCore.CurrentState`. | `WorldStateManager.TryAddBuilding` | Buildings other than the HQ could be placed during *PlacingHQ* if the UI allowed it. |

## Networking and hosting

| Issue | Where | Effect |
|---|---|---|
| The **Start Game** button only appears for a player who is both server and local player (`ClientIsServerOwner`). | `ClientPlayer.ClientIsServerOwner` | A **headless/dedicated server** (auto-started via `headlessStartMode`) can't start a match. |
| Clients send `UpdateClientView` (a reliable Command) every rendered frame. The server sends `SetServerPlayer` JSON to every player every `LateUpdate`, on top of `TargetUpdateResources`. | `UnitCommander.Update`, `GameCore.LateUpdate`, `ResourceSystem` | Unnecessary bandwidth. Resources are sent twice. |
| `UpdatePlayerViews` uses LINQ `Any`/`FindIndex` over SyncLists for every visible entity, every `FixedUpdate`. | `WorldStateManager.UpdatePlayerViews` | O(players × visible²). It will get slow with large armies. |
| Unit and building IDs are `Random.Range(0, int.MaxValue)` with no collision check. | `SpawnerSystem`, `WorldStateManager.TryAddBuilding` | Collisions are very unlikely but possible. |
| `"Map_2"` is hard-coded as the match scene. | `GameCore.Cmd_StartGame` | No map selection. |
| ECS systems rely on Mirror `[Server]`/`[ServerCallback]` attributes on `ISystem` struct methods. Whether Mirror's weaver enforces them there hasn't been checked. | `Scripts/Systems/*` | They are server-only in practice because entities only exist on the server. |

## Project and config hygiene

- **HDRP is the active render pipeline** (`Assets/Settings/HDRP*.asset`, `GraphicsSettings`) for a 2D sprite game. URP 2D is the usual choice. Worth confirming this is deliberate.
- **Input System only** (`activeInputHandler: 1`) since v0.2. Gameplay input comes from the code-defined `GameInput` action map (`Assets/Scripts/Client/GameInput.cs`), and the legacy `Input` API is no longer used by game code. The `InputSystem_Actions.inputactions` asset still isn't referenced by game code (rebinding UI is planned).
- **Packages that appear unused:** `com.unity.ai.navigation`, `com.unity.cinemachine`, `com.unity.timeline`, `com.unity.visualscripting` (only an unused `using` in `UnitCommander.cs`).
- **No Windows build profile** is checked in. Only `Assets/Settings/Build Profiles/Linux.asset` is.
- **No automated tests.** `com.unity.test-framework` is installed but there are no test assemblies for game code.
- **Main-menu wiring by tag.** `GameManager.OnSceneLoaded` finds buttons with `GameObject.FindWithTag`. Renaming or removing a tag breaks the menu silently.

## Work-in-progress and legacy content

| Item | Status |
|---|---|
| `UnitType.Soldier` | In the enum. No config entry, sprite or spawner. Spawning it would fail with "Unit type Soldier not found in config". |
| `Assets/Maps/Map_Unused.unity` | Old map, not in the build. |
| `Assets/Maps/Map_Common/Unit Subscene.unity` | Empty ECS subscene referenced by `Map_2`. Placeholder for baked entities. |
| `Assets/TestingScenes/` (`DOTS_TEST`, `DOTS_TEST/UnitTestScene`, `New Pathfinding`, `Unit.prefab`) | Experimental scenes for ECS and pathfinding. Not in the build. |
| `Scripts/Resources/ResourceMap.cs` | Earlier distance-based resource-field idea (tag `Resource`). Not used by the current gem-tile mining. |
| `Scripts/LobbyPlayerPrefab (2).prefab` | Duplicate prefab with a stray name. Probably safe to delete after checking references. |
| `*Authoring` components + Bakers (`UnitIdAuthoring`, `HealthAuthoring`, `DamageAuthoring`, `MovementAuthoring`, `SpawnerAuthoring`, `BuildingAuthoring`) | Only useful for subscene baking, which isn't used yet. The structs they define are used everywhere. |
| `Pathfinding.FindPath` (managed) and `BurstFindPath(doJob: true)` | Old path. The `doJob: true` branch starts a task and returns an empty path straight away. |
| `BuildingButtonManager.CheckIfBuildingInWall` / `GetBuildingSizeInUnits` and the commented-out client-side validation | Replaced by the server round-trip `CanBuildBuildingCommand`. |
| `GameManager.OnSceneLoaded` spawn-spot block (tag `SpawnSpot`) | Picks a spawn spot but does nothing with it. Left over from before HQ placement. |
| `ClientPlayer.OnSceneChangedEvent` (commented out) | Replaced by `UnitCommander.Awake` calling `SetUnitHandles`/`SetBuildingHandles`. |

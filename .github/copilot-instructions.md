# WAR-2D Copilot Instructions

## Project Overview

WAR-2D is a networked 2D RTS/Strategy game built with **Unity**, **Mirror** (networking), and **Unity DOTS/ECS** (gameplay simulation). The game progresses through distinct states: Lobby → PlacingHQ → Countdown → Playing → GameOver.

### Core Architecture
- **Server-Authoritative**: Game logic runs on server; clients receive authoritative updates
- **Hybrid Approach**: Mirror for networking/player management + DOTS/ECS for game simulation (units, buildings, combat)
- **Singleton Pattern**: `GameCore`, `GameManager`, `WorldStateManager` manage global state

---

## Key Systems & Data Flow

### 1. **Player & Connection Management**
- **GameManager** (extends NetworkManager): Handles connections, scene loading, singleton persistence
- **GameCore** (NetworkBehaviour): Manages game state (`CurrentState: GameState`), player collections, game events
- **ServerPlayer**: Server-side player data (resources, state); synchronized to clients via `SetServerPlayer()` RPC
- **ClientPlayer** (NetworkBehaviour): Client-side representation with `SyncList<ClientUnit>` and `SyncList<BuildingData>` for visibility
- **Critical Flow**: Player connects → `OnServerAddPlayer()` → Player added to `GameCore.ServerPlayers` → Private data sent via TargetRpc

### 2. **Game State Machine**
```csharp
enum GameState { Lobby, PlacingHQ, Countdown, Playing, GameOver }
```
- **Lobby**: Players join, view other players
- **PlacingHQ**: Players place headquarters (checked via `WorldStateManager`)
- **Countdown**: Short timer before gameplay
- **Playing**: Active gameplay with resource/unit management
- **GameOver**: Winner determined (see `WinLossSystem`)

### 3. **DOTS/ECS Game Simulation**
Units and buildings are **Entity Component System** entities, not GameObjects. Key components:
- **ClientUnit**: `id, ownerId, position, spriteName (UnitType), targetId`
- **BuildingData**: `id, ownerId, position, buildingType`
- **HealthComponent**, **DamageComponent**: Combat stats
- **MovementComponent**, **ResourceCostComponent**: Unit properties
- **MiningComponent**, **BuildingResourceComponent**: Resource generation/consumption

**Key Systems** (in `Systems/`):
- **CombatSystem**: Enemy targeting and damage application (server-only via `[Server]`)
- **MovementSystem**: Unit pathfinding and motion using `PathPoint` buffer
- **ResourceSystem**: Passive generation, mining, consumption (runs every 0.1s to reduce network load)
- **SpawnerSystem**: Unit/building creation via `EntityCommandBuffer`
- **WinLossSystem**: Elimination checking
- **DestructionSystem**: Entity cleanup

**Critical Pattern**: Systems use `SystemAPI.Query<>()` with `[ServerCallback]` or `[Server]` attributes to ensure server-only execution.

### 4. **Visibility & Network Synchronization**
- **WorldStateManager**: Maintains tilemap (`TilemapStruct` with `NativeHashMap<int2, TileNode>`), tracks player "view area"
- **UpdatePlayerViews()**: Calculates which units/buildings each player can see
- Only visible entities are added to `ClientPlayer.visuableUnits` and `visuableBuildings` SyncLists
- Clients listen to SyncList events → update `UnitCommander` for rendering

### 5. **Resource Economy**
- **Passive Generation**: Base rate from config (default ~0.1 per frame)
- **Mining**: `MiningComponent` checks for gems on adjacent tiles, generates resources
- **Consumption**: Units/buildings have `ResourceCostComponent` (upfrontCost + runningCostPerSecond)
- **Transaction Model**: `ServerPlayer.AddResources()` / `RemoveResources()` update `data.resources`, synced via TargetRpc each frame

---

## Configuration System

All game balance/balance lives in `Config/GameConfig.cs`:
```csharp
GameConfigData {
  ResourceConfig: PassiveGenerationRate, StartingResources, MiningRate
  Dictionary<string, UnitConfig>: Health, Damage, MoveSpeed, UpfrontCost, RunningCost
  Dictionary<string, BuildingConfig>: Health, UpfrontCost, RunningCost, MiningRate, SpawnRate
}
```
Load via `ConfigLoader.LoadConfig()` (supports XML/JSON). Always access server-side; never hardcode values.

---

## Critical Conventions & Patterns

### Network Attributes & Execution Context
- **[Server]**: Method runs only on server; called by any client
- **[ServerCallback]**: Runs on server during normal update (respects `isServer` check internally)
- **[Client]**: Runs only on local client
- **[TargetRpc]**: Send data to specific client (use for private player data)
- **[Command]**: Client → Server RPC; use `requiresAuthority=false` for UI actions

### Entity Lifecycle
1. **Creation**: `EntityCommandBuffer.CreateEntity()` + add components (not direct `Instantiate()`)
2. **Lookup**: `Dictionary<int, Entity>` (Units/Buildings keyed by ID)
3. **Destruction**: `EntityCommandBuffer.DestroyEntity()` or component removal

### SyncVar & SyncList Hooks
- Use `hook = nameof(CallbackMethod)` for `[SyncVar]` to trigger local updates
- `SyncList<T>` events: `OnAdd`, `OnChange`, `OnRemove`, `OnClear` (wired in `ClientPlayer.OnStartClient()`)

### Pathfinding
- Units have `PathPoint` dynamic buffer (linked list of waypoints)
- `MovementSystem` processes these; populated by pathfinding algorithm (see `Unit/Pathfinding/`)

---

## File Structure & Key References

```
Assets/Scripts/
├── GameCore.cs                    # Server game state, player collections, win/loss events
├── GameManager.cs                 # Networking, connection lifecycle, singleton
├── ClientPlayer.cs                # Client representation, sync lists
├── ServerPlayer.cs                # Server player data (resources, state)
├── Unit/
│   ├── WorldStateManager.cs       # Tilemap, visibility, pathfinding
│   ├── *Authoring.cs              # Baker pattern (MonoBehaviour → ECS components)
│   └── Pathfinding/               # A* pathfinding implementation
├── Systems/
│   ├── CombatSystem.cs            # Targeting, damage
│   ├── MovementSystem.cs          # Pathfinding execution
│   ├── ResourceSystem.cs          # Economy
│   ├── SpawnerSystem.cs           # Unit/building creation
│   ├── WinLossSystem.cs           # Victory conditions
│   └── DestructionSystem.cs       # Cleanup
├── Building/
│   ├── BuildingResourceComponent.cs
│   ├── MiningComponent.cs
│   └── Building Spawning/
│       ├── BuildingAuthoring.cs   # BuildingData component
│       └── BuildingButtonManager.cs
├── Config/
│   ├── GameConfig.cs              # Data structures
│   └── ConfigLoader.cs            # Load from assets
├── UI/                            # HQPlacementUI, WinScreenUI, LossScreenUI
├── Client/                        # UnitCommander (rendering/selection)
└── Components/                    # HQComponent
```

---

## Common Tasks

### Add a New Unit Type
1. Add to `UnitType` enum (Unit/UnitIdAuthoring.cs)
2. Add config entry: `config.Units["NewType"] = new UnitConfig { Health=10, ... }`
3. Create prefab with `UnitIdAuthoring`, link in spawner
4. Add sprite/rendering logic in `UnitCommander`

### Modify Combat Balance
- Edit `DamageComponent` spawn values in `SpawnerSystem.CreateUnit()` or move to config
- Adjust `CombatSystem` targeting/attack speed logic
- **Never** hardcode; read from `GameConfigData`

### Debug Server State
- Use `[ServerCallback]` + `Debug.Log()` (only logs on server)
- Check `GameCore.ServerPlayers` to inspect resources/state
- Inspect `WorldStateManager.Units/Buildings` dictionaries

### Add Resource Sink (Building/Unit Upkeep)
- Define `runningCostPerSecond` in config or `ResourceCostComponent`
- In `ResourceSystem.HandleConsumption()`, iterate entities with `ResourceCostComponent`, subtract from `ServerPlayer.RemoveResources()`
- If resources < 0, destroy the entity

---

## Networking Best Practices

1. **Authority**: Server is authoritative; clients are presentational
2. **Data Serialization**: Use `ServerData.Serialize()` (JSON via Newtonsoft) for TargetRpc payloads
3. **Update Frequency**: Batch updates (ResourceSystem runs at 0.1s intervals to reduce traffic)
4. **Prefab Registration**: Register all networked prefabs in `NetworkManager.spawnPrefabs`

---

## Known Architectural Decisions

- **Hybrid DOTS**: MonoBehaviour singletons (`GameCore`, `GameManager`, `WorldStateManager`) manage DOTS/ECS world; reduces complexity of pure DOTS migration
- **Visibility SyncLists**: Rather than replicating all entities, only visible ones sync; reduces bandwidth
- **Batch Command Buffers**: Systems use `EntityCommandBuffer` to defer structural changes (creation/destruction) until end of frame
- **Tilemap Persistence**: Uses `NativeHashMap` for O(1) tile lookups during pathfinding

---

## Testing & Debugging

- **Test Scene**: `Assets/TestingScenes/` contains isolated tests
- **Local Multiplayer**: Launch as Host + Client in same editor for quick iteration
- **Log Filtering**: Search `TIM.Console.Log()` calls (custom logging); check `Editor.log` for full output
- **Gizmos**: Enable `WorldStateManager.showTileMapweights` to visualize pathfinding weights

---

**Last Updated**: December 2025 | Branch: `Buildings-Expansion`

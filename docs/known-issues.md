# Known Issues and Tech Debt

The October 2026 code review found roughly 30 bugs, mismatches and hygiene problems. **v0.2 fixed all of them except the items below**, which are deliberately deferred to a later version. Each entry names the version that should clear it. See [roadmap.md](roadmap.md) for what each version delivers.

## Performance and scale

| Issue | Where | Target | Effect |
|---|---|---|---|
| `UpdatePlayerViews` uses LINQ `Any`/`FindIndex` over the `ClientPlayer` SyncLists for every visible entity, every `FixedUpdate`. | `WorldStateManager.UpdatePlayerViews` | **v0.5** | O(players × visible²). Fine at current army sizes, slow with large armies. v0.5's hybrid replication (send a unit's path once, then corrections and compact events) replaces the per-player visible lists entirely. |

## Tracked generated files

Found in the v0.3 project review. They were committed before `.gitignore` covered them, so ignoring them has no effect until they're untracked. v0.3 Task 1 clears them.

- **`Editor.log` (1.8 MB) and two `mono_crash.mem.*.blob` crash dumps (10 MB each)** are tracked at the repo root, even though `.gitignore` lists them. **v0.3**
- **19 files under `obj/`** (MSBuild `AssemblyReference.cache` output) are tracked. **v0.3**
- **`Assets/SceneDependencyCache/`** (5 `.sceneWithBuildSettings` files plus metas) is tracked. It looks like generated editor cache. Confirm in the editor before untracking. **v0.3**

## Project and config hygiene

Inert leftovers disclosed in v0.2 and scheduled to be cleaned up together.

- **No Windows build profile.** Only `Assets/Settings/Build Profiles/Linux.asset` is checked in — the owner deferred Windows to a later release. **v0.4**
- **Dangling HDRP entry** in `ProjectSettings/GraphicsSettings.asset`'s `m_RenderPipelineGlobalSettingsMap`: it points at a deleted HDRP global-settings asset (the project runs URP 2D). **v0.4**
- **Inert `ProjectSettings/HDRPProjectSettings.asset`** left behind by the move to URP 2D. **v0.4**
- **Unreferenced `Assets/Resources/Materials/TankRTS-5.png.mat`** — the Ground tilemap used it before the URP conversion. **v0.4**
- **Empty `Assets/Resources/Prefabs/` folder** (its only asset, `Bullet.prefab`, was deleted in v0.2). **v0.4**
- **Vendored Mirror components still call the legacy `Input` API** — `Components/GUIConsole.cs`, `Components/RemoteStatistics.cs` and `Components/Profiling/ToggleHotkey.cs` (the last via its own `GraphCanvas.prefab`). None are used in the shipped scenes, and the project is Input System only (`activeInputHandler: 1`), so they would throw if ever attached. **v0.4**
- **Vendored Console `Demo/Scripts/DemoPlayer.cs` uses the legacy `Input.GetAxis`.** Demo only; not used by any shipped scene. **v0.4**
- **`WorldStateManager.GenerateTileMap` leaks the tilemap's `NativeHashMap`.** It allocates `world.tiles` with `Allocator.Persistent`, and nothing disposes it — one map-sized leak per `Map_2` load (v0.2 removed the other persistent allocations). **v0.4**
- **No server-side visibility model.** The view box is client-supplied; v0.2 clamps it to the map, but a modified client can still request the whole map. Replaced by the v0.5 replication + fog of war. **v0.5**

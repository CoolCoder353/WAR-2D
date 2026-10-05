# Known Issues and Tech Debt

The October 2026 code review found roughly 30 bugs, mismatches and hygiene problems. **v0.2 fixed all of them except the items below**, which are deliberately deferred to a later version. Each entry names the version that should clear it. See [roadmap.md](roadmap.md) for what each version delivers.

## Performance and scale

| Issue | Where | Target | Effect |
|---|---|---|---|
| `UpdatePlayerViews` uses LINQ `Any`/`FindIndex` over the `ClientPlayer` SyncLists for every visible entity, every `FixedUpdate`. | `WorldStateManager.UpdatePlayerViews` | **v0.5** | O(players × visible²). Fine at current army sizes, slow with large armies. v0.5's hybrid replication (send a unit's path once, then corrections and compact events) replaces the per-player visible lists entirely. |

## Project and config hygiene

Inert leftovers disclosed in v0.2 and scheduled to be cleaned up together.

- **No Windows build profile.** Only `Assets/Settings/Build Profiles/Linux.asset` is checked in — the owner deferred Windows to a later release. **v0.4**
- **Dangling HDRP entry** in `ProjectSettings/GraphicsSettings.asset`'s `m_RenderPipelineGlobalSettingsMap`: it points at a deleted HDRP global-settings asset (the project runs URP 2D). **v0.4**
- **Inert `ProjectSettings/HDRPProjectSettings.asset`** left behind by the move to URP 2D. **v0.4**
- **Unreferenced `Assets/Resources/Materials/TankRTS-5.png.mat`** — the Ground tilemap used it before the URP conversion. **v0.4**
- **Empty `Assets/Resources/Prefabs/` folder** (its only asset, `Bullet.prefab`, was deleted in v0.2). **v0.4**
- **Vendored Mirror components still call the legacy `Input` API** — `Components/GUIConsole.cs`, `Components/RemoteStatistics.cs` and `Components/Profiling/ToggleHotkey.cs` (the last via its own `GraphCanvas.prefab`). None are used in the shipped scenes, and the project is Input System only (`activeInputHandler: 1`), so they would throw if ever attached. **v0.4**
- **Vendored Console `Demo/Scripts/DemoPlayer.cs` uses the legacy `Input.GetAxis`.** Demo only; not used by any shipped scene. **v0.4**

# Known Issues and Tech Debt

The October 2026 code review found roughly 30 bugs, mismatches and hygiene problems. **v0.2 fixed all of them except the items below**, which are deliberately deferred to a later version. Each entry names the version that should clear it. See [roadmap.md](roadmap.md) for what each version delivers.

## Performance and scale

| Issue | Where | Target | Effect |
|---|---|---|---|
| Buildings are still one GameObject each on clients, fed by `ClientPlayer` SyncLists. | `UnitCommander`, `WorldStateManager.UpdatePlayerViews` | **v0.7** | Fine for a few hundred buildings; walls make the counts large, so v0.7 instances buildings and walls like units. |
| The perf gate counts the bots' bandwidth at virtual clients (encoded payload plus an estimated KCP/UDP overhead per batch), not at raw KCP clients on loopback as the v0.4 plan asked. | `Dev/PerfMatch.cs`, `ReplicationService.AddVirtualClient` | **v0.5** | The numbers leave out KCP retransmits and the host's KCP send cost. v0.5 tunes the transport and should measure real loopback clients. |
| The gate's 3 runs were taken with the Unity editor open (the protocol asks for it closed). | `docs/perf/v0.4/` | **v0.5** | Results are conservative; re-run with the editor closed when the gate is next run. |
| Corrections dominate bandwidth in dense melees (crowd jostle from separation and units stopping to fight). At ~80k units the fronts battle averages ~100 KB/s per client, but an artificially dense battle (reinforcements spawned into the fight) reached ~450 KB/s. | `Net/Replication/ReplicationEncoder.cs` | **v0.5** | Within budget for the gate scenario. v0.5's transport work should consider velocity-carrying corrections or hold-on-target hints. |

## Project and config hygiene

Inert leftovers disclosed in v0.2 and scheduled to be cleaned up together.

- **No Windows build profile.** Only `Assets/Settings/Build Profiles/Linux.asset` is checked in — the owner deferred Windows to a later release. **v0.4**
- **Inert `ProjectSettings/HDRPProjectSettings.asset`** left behind by the move to URP 2D. **v0.4**
- **Unreferenced `Assets/Resources/Materials/TankRTS-5.png.mat`** — the Ground tilemap used it before the URP conversion. **v0.4**
- **Empty `Assets/Resources/Prefabs/` folder** (its only asset, `Bullet.prefab`, was deleted in v0.2). **v0.4**
- **Vendored Mirror components still call the legacy `Input` API** — `Components/GUIConsole.cs`, `Components/RemoteStatistics.cs` and `Components/Profiling/ToggleHotkey.cs` (the last via its own `GraphCanvas.prefab`). None are used in the shipped scenes, and the project is Input System only (`activeInputHandler: 1`), so they would throw if ever attached. **v0.4**
- **Vendored Console `Demo/Scripts/DemoPlayer.cs` uses the legacy `Input.GetAxis`.** Demo only; not used by any shipped scene. **v0.4**
- **No server-side visibility model.** The view box is client-supplied; it is clamped to the map and its span checked, but a modified client can still move its view anywhere to see units there. Replaced by v0.5 fog of war. **v0.5**
- **Every unit type draws with the Tank texture.** `InstancedUnitRenderer` binds one texture; per-type frames come with the sprite atlas in the art pass. **v0.8**

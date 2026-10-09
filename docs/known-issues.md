# Known Issues and Tech Debt

The October 2026 code review found roughly 30 bugs, mismatches and hygiene problems. **v0.2 fixed all of them except the items below**, which are deliberately deferred to a later version. Each entry names the version that should clear it. See [roadmap.md](roadmap.md) for what each version delivers.

## Performance and scale

| Issue | Where | Target | Effect |
|---|---|---|---|
| Buildings are still one GameObject each on clients (fed by `BuildingBatch` records since v0.5). | `UnitCommander`, `BuildingReplication.cs` | **v0.7** | Fine for a few hundred buildings; walls make the counts large, so v0.7 instances buildings and walls like units. |
| KCP settings (windows, MTU, interval) are still Mirror's defaults on the `Main_Menu` NetworkManager. v0.5 added real loopback measurement (`tools/perf-run.sh` starts 7 headless `-perfClient` players counting raw UDP bytes through `MeteredKcpTransport`) but no gate has been run with it yet. | `Main_Menu` `KcpTransport`, `Dev/PerfClient.cs` | **v0.6** | Tune from the first real-client gate results; the 8 player processes share the host's CPU, so host tick timings in those runs are conservative. |
| The gate's 3 runs were taken with the Unity editor open (the protocol asks for it closed). | `docs/perf/v0.4/` | **v0.6** | Results are conservative; re-run with the editor closed when the gate is next run. |
| Corrections dominate bandwidth in dense melees (crowd jostle from separation and units stopping to fight). At ~80k units the fronts battle averages ~100 KB/s per client, but an artificially dense battle (reinforcements spawned into the fight) reached ~450 KB/s. | `Net/Replication/ReplicationEncoder.cs` | **v0.6** | Within budget for the gate scenario. Fog interest (v0.5) sends each client every enemy its team sees, not just the camera box, so re-measure with the real-client gate before choosing velocity-carrying corrections or hold-on-target hints. |
| **v0.5's performance gate was never run.** Its exit criterion "8 clients × 10,000 units stay within the bandwidth budget" is unverified with fog interest and building records. Needs a Linux release build, the editor closed, then `tools/perf-run.sh` (3 runs, 7 real clients). | `tools/perf-run.sh`, `Dev/PerfMatch.cs`, `Dev/PerfClient.cs` | **v0.6** | The v0.4 numbers (camera-box interest, virtual clients) no longer describe the shipped networking. Results go to `docs/perf/v0.5/`. |
| `SimVisionSystem`'s cost at 80,000 units is unmeasured, including the v0.6 worst case where every player shares vision with everyone (each source is stamped on up to 8 grids). Source collection is a single-threaded job over every unit. | `Sim/SimVisionSystem.cs` | **v0.6** | Measured by the gate above (`perf.tick.main`, `perf.tick.wait`); parallelise the collect step if it shows. |

## Not yet play-tested (v0.5)

v0.5 passed the EditMode and PlayMode suites (including `LeakTests`), but nobody has played a match with it yet. Check these in the first manual Linux match. **v0.6**

- **Fog overlay** (`Client/FogView.cs`): draw order over the instanced units and buildings, and that its colours look right.
- **Lobby team label** (`LobbySystem`): it is created in code inside each lobby row, so its placement in the row's layout is unchecked; cycling teams as host, and seeing the change on other clients.
- **Allied play**: allies share vision and never fight; a team win shows the Win screen to every player on the team, including eliminated ones.
- **Building ghosts**: enemy buildings dim when out of sight and disappear only once their spot is seen again.
- **Editor crash after `SimData` gains fields.** The first EditMode run after `SimData`'s layout changes can segfault while scheduling a tick job (v0.5: `GatherJob`; v0.6 Task 4: `SimCombatSystem`, in `AtomicSafetyHandle` handle extraction). Restarting the editor fixes it every time, so it is stale Burst code compiled against the old struct layout. Restart the editor after changing `SimData`'s fields, before running the tests.

## Project and config hygiene

Inert leftovers disclosed in v0.2 and scheduled to be cleaned up together.

- **No Windows build profile.** Only `Assets/Settings/Build Profiles/Linux.asset` is checked in — the owner deferred Windows to a later release. **v0.9**
- **Vendored Mirror components still call the legacy `Input` API** — `Components/GUIConsole.cs`, `Components/RemoteStatistics.cs` and `Components/Profiling/ToggleHotkey.cs` (the last via its own `GraphCanvas.prefab`). None are used in the shipped scenes, and the project is Input System only (`activeInputHandler: 1`), so they would throw if ever attached. Not shipped; leave vendored code alone (no target).
- **Vendored Console `Demo/Scripts/DemoPlayer.cs` uses the legacy `Input.GetAxis`.** Demo only; not used by any shipped scene. Not shipped; leave vendored code alone (no target).
- **Every unit type draws with the Tank texture.** `InstancedUnitRenderer` binds one texture; per-type frames come with the sprite atlas in the art pass. **v0.8**

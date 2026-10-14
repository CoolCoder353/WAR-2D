# Bug Review

Code-review findings for WAR-2D, newest sections last. The first review (2026-10-14) audited `Assets/Scripts/` (Sim, Net, Systems, Rules, World, Client, UI, Config, Audio, Pathing) plus `GameCore` / `GameManager` / `WorldStateManager`, read-only. The second scan (same day) re-audited the unit system specifically: spawn, orders, movement, combat, upkeep, selection, replication/prediction and rendering. The third scan (same day) diagnosed playtest reports of stuck pathfinding. The fourth scan (same day) diagnosed playtest reports of snappy unit movement. The fifth scan (same day) diagnosed playtest reports of friend joins failing from the Play screen. Nothing here is fixed yet; each item names where to fix it. When an item is fixed, move it to [known-issues.md](known-issues.md) (fixed row) or delete it here.

## First review (2026-10-14): general

### High

| Finding | Where | Notes |
| --- | --- | --- |
| A command from a disconnecting connection throws on the server: the sender's owner id is read without a null guard. | `Net/OrderCommands.cs` (`TryActingOwner`) | `sender?.identity == null` is not checked (unlike `Cmd_Surrender`, `Cmd_GiftResources`). Guard and return false first. |
| Same crash in the diplomacy path. | `Net/DiplomacyCommands.cs` (`TryChangeDiplomacy:40`) | `sim == null` is checked, the sender is not. Same fix. |
| Snapshot pacing reads as unlimited at `0` but blocks every new unit: with the default `0`, `Entered.Length >= MaxEnters` is always true, so no `Enter` is ever produced. | `Net/Replication/InterestSets.cs:67-68,215` | Fix: only pace when `MaxEnters > 0`. Verify what `ReplicationService` sets from `SnapshotBytesPerSecond`; if never set, this is live. |
| Vision divides by zero when `VisionInterval` is `0`. | `Sim/SimVisionSystem.cs:37` | `SimEconomySystem` guards with `math.max(1, TickRate)`; vision does not. Enforce `VisionInterval >= 1` in `ConfigParser` or guard here. |
| Units past the global `Capacity` are silently dropped from the tick but never destroyed or refunded: they accumulate invisibly, cost ECS memory, and never tick, replicate or die. | `Sim/SimGatherSystem.cs:34-35` | Fix: refuse `SpawnUnit` at cap (like the per-player cap) and log. |

### Medium

| Finding | Where | Notes |
| --- | --- | --- |
| `NaN` / `Infinity` costs pass payment: `cost <= 0` is false for `NaN`, `balance < NaN` is false, so `balance -= NaN` poisons `Resources` forever. | `Rules/UpkeepRules.cs:5-11`, `ServerPlayer.cs:92-103` | Gifts guard this (`GiftRules.IsValid`); config/lobby paths do not. Fix: treat non-`cost > 0` as free/invalid explicitly. |
| A voice is claimed before the clip is checked, so a missing `Audio/Sfx/{id}` burns a pool voice until stolen. | `Audio/AudioService.cs:53-57` | Fix: load `Clip(id)` first, return early on null. |
| Placement uses exact float equality for right angles, so `89.9999` fails while `FacingOffset` would round it fine. | `Rules/PlacementRules.cs:63-67` | Fix: tolerance via `DeltaAngle` (e.g. `< 0.01`). |
| `PlotUnit` / `PlotBuilding` dereference a null fog array after `Begin(null)`. | `Client/MinimapTexture.cs:59-68,92-103` | Current caller guards, but any other caller throws. Fix: return early when fog is null. |
| A bare IPv6 address with a port (`::1:7778`) is silently taken as a host with the default port. | `Net/JoinAddress.cs:36-43` | Fix: reject multi-colon input with a trailing `:port` unless bracketed (`[ipv6]:port`). |
| `CommandGate` is static and only cleared via `Forget`; if the disconnect path does not call it, buckets leak and a reused `connectionId` inherits limits/violations (false kick). | `Net/CommandGate.cs:14,41` | Verify `GameManager.OnServerDisconnect` calls `CommandGate.Forget`. |
| `MapGenerator` throws on seed `0` (`new Random(0)`). | `World/MapGenerator.cs:46-52` | Lobby paths guard this; direct/test calls crash. Fix: map `0` to `1` or throw a clear `ArgumentException`. |
| A multi-spawner queue command fans out to an unbounded selection; verify the server clamps to `MaxSpawnersPerCommand` (64). | `Rules/SpawnerRules.cs:11`, `UI/Controllers/HudController.cs:350-357` | Server comment says lists over the limit are refused (`CmdChangeQueues`); confirm the same guard covers every queue path. |

### Low / hardening

- **`Client/Settings/SettingsStore.cs:92`** — `Path.GetDirectoryName` can return null for a bare filename; `CreateDirectory(null)` throws (caught, save silently skipped). Guard null.
- **`Net/LobbyCommands.cs:132`** — a full lobby falls back to colour `0`, duplicating it. Unreachable via `LobbyRules.MayJoin`; assert instead.
- **`Net/Upnp/UpnpProtocol.cs:138-150`** — `IsPublic` misses TEST-NET / benchmarking ranges. Minor (playtest stopgap).
- **`Net/JoinAddress.cs:50`** — `999.999.999.999` can pass host validation as DNS and fail later at connect. Acceptable, but the error misleads.
- **`Client/ClientWorld.cs` (`FireTracers`)** — rotation write is bounds-checked; verify `PredictOne` is safe for unknown indices.
- **`Client/GameInput.cs`** — `FastPan` / `AppendModifier` / `QueueModifier` share Shift; `QueueModifier` is rebindable while `AppendModifier` is not, so queue vs append diverge after a rebind. Share one action or exclude both.

## Second scan (2026-10-14): unit system

Unit-specific findings from the focused re-scan (spawn, orders, movement, combat, upkeep, selection, replication/prediction, rendering).

### High

| Finding | Where | Notes |
| --- | --- | --- |
| Spawn position is unvalidated: `Spawn` writes `command.Position` straight into the unit. | `Sim/SimCommandSystem.cs:115-153` | `SpawnerSystem` passes a searched tile (valid), but `DevSpawnUnit` passes an arbitrary `float2`. A unit spawned inside a wall, footprint or off-map fails `Blocked` on every axis and sits stuck forever. Clamp to map bounds and reject non-walkable spots (or snap like orders). |

### Medium

| Finding | Where | Notes |
| --- | --- | --- |
| `TryAddBuilding` also lacks a sender guard (`sender.identity.GetComponent`), same crash class as the general High items. | `Unit/WorldStateManager.cs:506-518` | Guard `sender?.identity == null` before use. |
| A unit pushed out of a new footprint with nowhere free stays inside the building (footprint search capped at 4096 tiles). | `Sim/SimCommandSystem.cs:177-199`, `Rules/TileSearch.cs` | Rare on open maps; on a full map the unit is permanently stuck. Fail the placement or destroy the unit instead of leaving it embedded. |
| Heal/repair path is one-way: `ClientBuildings.SetHealth` ignores unknown ids, and there is no cap check against max, so a stale or forged health record is silently kept or dropped. | `Net/Replication/BuildingReplication.cs` (`ClientBuildings.SetHealth`) | Clamp to `[0, max]` and log unknown ids. |
| Health byte quantisation hides near-death: a unit at 0.4% replicates as `0` while alive, so clients draw it dead-but-present. | `Net/Replication/Messages.cs` (`HealthUnit`) | Reserve `0` for dead or quantise `1..100` for the living. |
| Squad counts can go stale: server-side `Prune` drops dead units, but counts are only pushed when flagged dirty on assign. | `Net/OrderCommands.cs` (`SendSquadCounts`) | Push counts after prune-driven removals (e.g. on `CmdOrderSquad`) or on a slow timer. |
| Attack tracers from an unknown attacker predict from a default state (origin), drawing a streak from `(0,0)`. | `Client/ClientWorld.cs` (`FireTracers`) | Skip tracers whose attacker index is unknown, as is done for unknown targets. |

### Low / hardening

- **Per-tick spawn bursts bypass the cap check within one boundary except via the transient `UnitsBySlot++`.** The increment-then-recount works, but it is load-bearing and uncommented (`Sim/SimCommandSystem.cs:123-135`, `Sim/SimEconomySystem.cs:81-89`). Comment it or enforce the cap against pending spawns explicitly.
- **`KillUnits` decrements `UnitsBySlot` although the economy recounts it every tick.** Harmless, but the same double-bookkeeping as above; remove or comment.
- **`moversByClass` only has 2 size-class buckets (`Sim/SimCommandSystem.Move.cs:10`).** A `SizeClass >= 2` is masked with `& 1` and silently joins the wrong flow field. Every config type is `0` today; assert `SizeClass < 2` at spawn.
- **`OrderBook.GoalSnapRadius` (12) can move a rally or order goal far from what the player clicked** with no client feedback. Show the snapped goal (or reject rock goals for rallies).
- **`WaypointBook` silently drops waypoints past `MaxQueued` (4).** Intended, but the client shows no "queue full" state; surface it in the HUD.
- **Box-order edge exclusion (`Sim/SimCommandSystem.Move.cs:45-52`).** Units exactly on the max edge are excluded (`p >= hi`); consistent with inclusive tiles, but drag boxes drawn on tile edges can miss visibly-inside units by a pixel. Inset or round the box before testing.
- **Selection has no unit cap.** A 10k-unit box select sends ~5 order chunks under one token (`MaxOpenTokens` 4, token timeout 2 s); a slow connection can strand the pending order. Fine today, revisit with larger armies.
- **Ghost building selection may outlive the building.** Verify the `Gone` record path calls `Selection.RemoveBuilding`; otherwise the command card shows a queue for a dead spawner.

## Third scan (2026-10-14): stuck pathfinding (playtest reports)

Playtesters report pathfinding getting stuck randomly at small and large unit counts: some units wedge into walls, some stop moving at random. Tracing order issue, flow-field rebuild, steering (`DesiredVelocityJob`), integration (`IntegrateJob`), and arrival/waypoint advance shows four interacting defects, not one bug — which matches the randomness and the small-and-large spread.

### High

| Finding | Where | Notes |
| --- | --- | --- |
| Orders steer with no field for ~1s+ after issue: units beeline straight at the goal into walls. `DesiredVelocityJob` only follows a field when `OrderReady[order] != 0`, else steers straight at the goal. But `Issue()` only sets `Live`, never `Ready`; `Ready` is set in `AtBoundary` only if the cache has published, while `ScheduleRebuilds` runs later in `SimEndSystem` (OrderLast) capped at `MaxFieldRebuildsPerTick = 2`, and same-boundary rebuilds were removed (TODO comment). Even one order waits 1+ ticks; queued orders (size-class splits, waypoint legs) queue behind each other. Units grind along walls via axis-separated integration and end up wedged behind corners; if the goal sector rebuild is still queued on arrival, they sit in `AtGoal` until the arrival radius catches them. | `Sim/SimMovementSystem.cs:167-174`, `Sim/OrderBook.cs:84-105,166`, `Sim/SimTickGroup.cs:121` | Fix: restore a same-boundary fast path for at least the goal sector, or hold units (zero desired velocity) until `Ready`. Minimum: steer toward the route's first waypoint, not the raw goal, while unready. |
| Arrival radius scales with unit count: `goalRadius + (count-1)*0.5` means a 500-follower order "arrives" hundreds of tiles early, clears the order, and goes Idle. | `Sim/SimMovementSystem.cs:195-196` | Fix: cap the spread term (e.g. `min(count, N)*0.5` with small N), or spread goals per unit (goal-spread already exists at issue time — reuse per-unit offsets). |

### Medium

| Finding | Where | Notes |
| --- | --- | --- |
| Route-miss extension retargets where the unit is, not where it is going: an off-route unit steers straight at the goal and queues its own sector, `AddStartSector` rebuilds behind it, and with throttled rebuilds the unit ping-pongs (straight-line, miss, rebuild-behind). In woods/corridors this reads as vibrating against a wall. | `Sim/SimMovementSystem.cs:176-183`, `Sim/OrderBook.cs:160-164`, `Pathing/SectorFieldCache.cs:17-22` | Fix: extend toward the goal sector (or the straight-line lookahead sector) and keep last valid heading instead of beelining — the `SectorFieldCache` doc comment already prescribes this. |
| Possible diagonal corner-cutting mismatch between the whole-map and sector field builders can wedge units into wall pockets with no unstick logic anywhere in movement. | `Pathing/FlowField.cs` (both builders), `Sim/SimMovementSystem.cs:303-312` | Fix: verify both builders share the corner rule; add a stuck detector (position delta ~0 over N ticks with live order → re-issue route / perpendicular nudge / re-queue sector extension). |

### Low / hardening (pathfinding)

- **`OrderBook.GoalSnapRadius` (12) silently relocates rock/footprint goals up to 12 tiles.** Units "stop" somewhere unclicked. Show the snapped goal or reject rock goals for rallies (`Sim/OrderBook.cs:106-130`).
- **`MaxStartCells` (512) subsampling leaves armies over 512 units partly off-route from the start.** The rest begin in the route-miss path above (`Pathing/SectorFieldCache.cs:27`, `Sim/OrderBook.cs:83`).
- **`MaxSearchNodes` (200000) can silently give up on cross-map 1024^2 routes** → uncovered sectors → straight-line fallback (`Pathing/SectorGraph.cs:57`).
- **Arrival clears AttackMove stance to Idle** instead of holding/fighting (`Sim/SimMovementSystem.cs:373-378`).

### Suggested fix order

1. Do not beeline while `!Ready` (hold or route-first-waypoint) — kills wall-grinding.
2. Cap the arrival-spread term — kills random stops in large armies.
3. Extend routes toward the goal sector + keep last heading — kills oscillation.
4. Stuck detector + corner-rule audit — backstop.

## Fourth scan (2026-10-14): snappy unit movement (playtest reports)

Playtesters report unit movement looking snappy (teleport-jitter) at any unit count. The count-independence points at client-side presentation, not the server sim. Tracing batch, decode, predict, and draw shows four compounding defects.

### High

| Finding | Where | Notes |
| --- | --- | --- |
| The client predicts at the newest batch's tick with no render delay: every batch arrival snaps all units to the server's newest position, then the client extrapolates until the next snap. `serverTime = max(serverTime, tickTime)` on arrival, `PredictAll(serverTime + sinceBatch)` between. At 20 Hz that is a snap every ~50 ms on every unit, worse with network jitter since there is no dejitter buffer. | `Client/ClientWorld.cs:108-114,135,161-164` | Fix: render ~100 ms behind (`PredictAll` at `newestTickTime - 0.1s`) so batches usually arrive before their time is drawn and motion is continuous; only extrapolate past confirmed data. |

### Medium

| Finding | Where | Notes |
| --- | --- | --- |
| The 0.1 s correction blend is defeated by real frame timing: decay is `exp(-elapsed / 0.1)` on wall-clock time between `PredictAll` calls, so at 20 fps or after any hitch the blend vanishes in one frame, and any correction over `MaxBlendTiles` (2 tiles) skips blending entirely. Offscreen-threshold corrections (2.0 tiles) and resume changes routinely exceed it. Lower fps = snappier; weak machines report it most. | `Client/ClientUnitStore.cs:37-39,107-112,275-294` | Fix: decay by tick-time not wall time, raise `BlendSeconds` to ~0.2 s, blend (don't cut) jumps up to ~4 tiles. |
| Correction quantisation plus resume jumps: `DeltaScale` 8 snaps corrections to a 1/8-tile grid while the server skips below 0.25 tiles (visible random-walk steps); `ApplyCorrection` re-anchors then resumes toward a possibly different leg, flipping predicted direction instantly; `StartRoute` restarts at the route's first waypoint, teleporting the prediction base while the unit is mid-leg. | `Net/Replication/MovementPrediction.cs:112-134`, `Assets/Resources/GameConfig.xml` (`DeltaScale`, `CorrectionThreshold`) | Fix: raise `DeltaScale` 8→16 and lower the threshold once a render delay absorbs the traffic; anchor `StartRoute` at the current predicted position, not waypoint 0. |
| Facing snaps instantly: `rotation[]` is set directly from velocity/target bearing each frame with no angular smoothing, and `UnitFrames.Direction` rounds to 8 facings, so heading noise flips the sprite 45° in one frame. Combined with the positional jitter above, units flicker between facings. | `Client/ClientWorld.cs:248-266` | Fix: slew `rotation[]` toward the target bearing at a capped turn rate. |

### Suggested fix order

1. Render delay (~100 ms behind newest tick) — single biggest win.
2. Frame-rate-independent blend (tick-time decay, ~0.2 s, blend up to ~4 tiles).
3. Smooth facing (capped turn rate).
4. Smaller corrections (`DeltaScale` 16, lower threshold, anchor `StartRoute` at predicted position).

## Fifth scan (2026-10-14): friend join fails from Play screen (playtest reports)

Playtesters report joining a friend's tunnel address works from the main menu but fails on the Play screen (Main menu, Play button, host/join screen).

### High

| Finding | Where | Notes |
| --- | --- | --- |
| `ConnectToServer` silently drops joins while a client is already active: `if (!NetworkClient.active)` guards `StartClient` with no else path, so a Play-screen Join during a lingering connection does nothing but still shows Connecting. | `GameManager.cs:286-299`, `UI/Controllers/MenuController.cs:105-112` | Fix: if active, `StopClient` first then connect, or disable Join while `NetworkClient.active` with a hint. |
| Failed or cancelled joins leave stale `joiningAddress` so a second attempt misreports: cancel calls `StopClient` but `Update` only clears on inactive client, causing stuck Connecting or wrong Failed versus Refused text. | `UI/Controllers/MenuController.cs:114-151` | Fix: clear `joiningAddress` on cancel and on disconnect callback, and reset `wasConnected` per attempt. |


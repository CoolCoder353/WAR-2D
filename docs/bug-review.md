# Bug Review

Findings from code reviews and scans, from an outside review (2026-10-14, triaged against the code on 2026-10-15) to the eighth scan (2026-11-05: movement, server and client). The review was not taken as fact: each finding was traced in the source. Confirmed bugs are fixed (and logged in [known-issues.md](known-issues.md)); the rest are listed below with why they were not changed.

## Triage (2026-10-15)

### Confirmed and fixed

| Finding                                                                                                                        | Fix                                                                                           |
| ------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------- |
| Join from the Play screen silently did nothing while a client was already active, leaving the UI on "Connecting" (fifth scan). | `GameManager.Connect` stops a lingering client before connecting (and refuses while hosting). |
| `AudioService.Sfx` claimed a voice before checking the clip exists.                                                            | The clip is loaded first.                                                                     |
| `MinimapTexture.PlotUnit` / `PlotBuilding` dereferenced a null fog array after `Begin(null)`.                                  | Both return when there is no fog.                                                             |
| `MapGenerator.Generate` threw on seed `0` (`Unity.Mathematics.Random`).                                                        | Seed `0` maps to `1`.                                                                         |
| Tracers from an attacker the client does not know drew from the origin.                                                        | `FireTracers` skips unknown attackers.                                                        |
| A living unit under 0.5 % health replicated as `0` (dead).                                                                     | `HealthByte` reserves `0` for dead; the living send at least `1`.                             |

### Not a bug (checked, no change)

- **Null `sender.identity` in `TryActingOwner`, `TryChangeDiplomacy`, `TryAddBuilding`:** every `[Command]` calls `CommandGate.Allow` first, which returns false for a null connection or `conn.identity == null`.
- **Snapshot pacing blocks all new units at `MaxEntersPerBuild == 0`:** `InterestSets` maps `0` to `int.MaxValue`, and `ReplicationService` sets it to at least 64.
- **`VisionInterval` of 0:** the config parser enforces `min: 1` and `SimData` applies `math.max(1, ...)`.
- **Units past the global `Capacity` leak:** `Capacity` equals the id allocator's capacity, and `Allocate` throws at exhaustion, so `Spawn` refuses first.
- **`NaN` / `Infinity` costs:** `ConfigParser.Float` rejects both; gifts are validated separately.
- **`CommandGate` leak:** `Forget` is called on both disconnect paths (`GameManager`, `GameCore`).
- **Multi-spawner commands unbounded:** `CmdChangeQueues` and `CmdSetRally` both refuse over `MaxSpawnersPerCommand`.
- **`moversByClass` size class:** `SizeClass` is parsed with `max: 1`.
- **Spawn position unvalidated:** only `DevSpawnUnit` passes a free position, and it is gated by `DevApi.Allowed`.
- **`IsRightAngle` exact equality:** placement rotation is a canonical value from the client; rejecting `89.9999` is correct.
- **`::1:7778` taken as a host:** that string is a valid bare IPv6 address, so it is ambiguous by nature; bracket syntax is supported.
- **Arrival radius grows with unit count (third scan):** it now scales with the square root of the followers, not linearly (`SimMovementSystem`).
- **Stale `joiningAddress` after cancel (fifth scan):** the cancel handler clears it and `Join` resets `wasConnected`.
- **Snappy movement (fourth scan):** presentation tuning (render delay, blend time, facing slew), not a defect. The described "snap per batch" does not match the code (units are predicted along their routes and drawn through a blend). Revisit as a polish task if playtests still report it.
- **Steering straight at the goal until a flow field is ready (third scan):** real, but the wait is one to a few ticks, not a second; it needs a design decision (hold or route-first-waypoint), not a bug fix. A stuck detector is a feature, not a fix.

### Still open (valid, low priority)

`Shift` shared by `QueueModifier` and `AppendModifier` (kept on purpose, open until the owner confirms; see `known-issues.md`, rebinding), and the low / hardening bullets below that are not struck through. The original findings follow; fixes are marked in place. The 2026-10-18 pass fixed stale squad counts, the embedded large unit, the replay EOF hang, the v0.7 client leftovers, the null-identity loops and most hardening bullets.

---

## Ninth scan (2026-11-07): v0.19 and v0.20

A review of everything since v0.18 (`88a467e0..release/v0.20`, 184 script files), by reading the code; the eighth scan's open items were checked again on the way and the ones still there are fixed above. New findings, all fixed on `release/v0.20` and logged in [known-issues.md](known-issues.md):

| Severity | Where | Finding | Fix |
| --- | --- | --- | --- |
| Medium | `ApplyDamageJob` | The hit queue's order is the worker threads'. A hit after the killing one still set `LastHitBy`, the death's cause and the HQ's last hitter, and a flame lit the corpse, so who got a kill or an elimination was a race. | A unit or building already at 0 takes no more hits; the first killing hit counts (`ReviewV020Tests`). |
| Low | `ClientWorld.UnitAt`, `CombatVisuals` | A replay's per-player view hid units that player didn't see, but still drew their fire, burning marker, laser, leap shadow and sparks. | Hidden units are unknown to the combat drawing. |
| Low | `UpkeepJob`, `SimCommandSystem.KillUnits` | A unit killed by unpaid upkeep, or removed with its owner, left with the cause of whatever hurt it last: one lit once and dead of decay later was drawn burning to death. | Both set a plain death. |

Checked and not changed: the combat wire's codec bounds, the status delta and its fallback, shell bookkeeping, the lane client's held combat news, the route copy steps, waiting blueprints, the ping and spectator commands (gated and validated), the Demolish refund text (already fixed).

Still open from that pass: the eighth scan's correction item (Medium) and the reused order handle (Low); a Mine is set off by a Bomber leaping over it (the spec doesn't say; left as is); a client that meets a Sniper mid-aim or a Bomber mid-leap sees no laser or arc until the next one (the aim and leap news go out once).

---

## Eighth scan (2026-11-05): movement, server and client

Three read-only passes over `fix/routes-chokes-builders`, by tracing the code. The movement pass also ran a model of the movement jobs. What they found and this branch fixed is in [known-issues.md](known-issues.md) under "Route and choke fixes", each with a test that fails without the fix, except where it says otherwise. **Everything below is open**, comes from reading the code, and has not been reproduced in a running game.

### Movement

| Severity | Where | Finding | Smallest fix |
| --- | --- | --- | --- |
| ~~Medium~~ Fixed | `ReplicationEncoder.AppendRoute` | Fixed 2026-11-06: half tiles on the wire (`RouteWire`), routes snapped on the server. See known-issues.md, "Review fixes, round 3". | |
| Medium | `ReplicationEncoder` corrections | A correction is sent unreliably and applied to the server's copy at once. If it is lost, the server thinks the client knows, and the client shows the unit at its route's end until the next order. A correction can also pass the same tick's MoveOrder. | Skip the correction on a new MoveOrder's tick; send a reliable position when a unit stops. |
| Low | `RouteStore`, `SectorFieldCache` | A reused order handle hides a new route for up to 2 s: a lone unit's next queued leg can take the same handle in the same boundary. | Treat `Arrived[i] != 0` as no order in the trace and commit jobs. |
| ~~Low~~ Fixed | `SimCommandSystem.ClearFinishedWork` | Fixed 2026-11-07 (v0.19/v0.20 review): a queued Attack behind a job starts its chase as `AdvanceWaypoints` does. Was: a busy worker's queued Attack loses its target and stays in Attack stance, never firing. The UI doesn't send Attack to workers, so only a modified client can do this. | Handle the Attack stance as `AdvanceWaypoints` does. |
| ~~Low~~ Fixed | `SimCommandSystem.OnUpdate` | Fixed 2026-11-07: cleared only after a running tick's gather read them. Was: `PendingMoves` and `PendingOrders` set at a paused boundary are cleared at the next one, with no gather between. Only the dev flow of pause, place or order, then step can do this. | Clear them only after a boundary whose gather ran. |
| ~~Low~~ Fixed | `DesiredVelocityJob.NearestFieldCell` | Fixed 2026-11-07: a diagonal past two blocked straight cells is skipped. Was: it takes a diagonal neighbour even when both straight tiles beside it are solid, so a unit can stop in the corner without reporting a route miss. | Skip such a neighbour. |

### Server

| Severity | Where | Finding | Smallest fix |
| --- | --- | --- | --- |
| ~~Medium~~ Fixed | `ReplicationService` fog sends | Fixed 2026-11-06: snapshot parts and byte-cut deltas under the cap. | |
| ~~Medium~~ Fixed | `BuildingReplication`, `WriterSink.Full` | Fixed 2026-11-06: stops while a building's records still fit, Gone and Hide included. | |
| ~~Medium~~ Fixed | `MatchCommands.RpcMatchSummary`, `MaxPoints` 360 | Fixed 2026-11-06: `MaxPoints` 80, tested against the 16 KB cap. | |
| ~~Low~~ Fixed | `WorldStateManager.KillAllEntitiesOwnedBy` | Fixed 2026-11-06: `KillOwner` removes the owner's walls. | |
| ~~Low~~ Fixed | `WorldStateManager.KillAllEntitiesOwnedBy` | Fixed 2026-11-07: the `KillOwner` sim command also drops the owner's buildings, those created in the same drain included (`ReviewV020Tests`). Was: a building still queued at the kill is created afterwards, with a dead owner. | Record killed owners; set such a building's health to 0. |
| ~~Low~~ Fixed | `SimGatherSystem` | Fixed 2026-11-06: an unpaid Radar's sight is 0. | |

### Client

| Severity | Where | Finding | Smallest fix |
| --- | --- | --- | --- |
| ~~Medium~~ Fixed | `ClientPlayer.spawnerQueues`, `spawnerRallies` | Fixed on `unstable/pr30-routes-testing`: `ClientPlayer.ResetForNewMatch` (called on a Rematch from `MatchCommands`) clears them, and the attack rallies. | |
| ~~Medium~~ Fixed | `ClientPlayer.gameOverDeclared` | Fixed the same way: `ResetForNewMatch` resets it. | |
| ~~Medium~~ Fixed | `UnitCommander.Awake` | Fixed 2026-11-07: the local player is found lazily, and `Update` waits for it. Was: it assumes the local player spawned first, which slot reuse can break on a rejoin. It then throws, and `Update` throws every frame. | Null-check, and find the player lazily. |
| ~~Medium~~ Fixed | `tools/ai-bench.sh` | Fixed 2026-11-07: styles are seeded on the match seed (`seed0 + k`); baselines from before don't compare. The `"style"` objective's throw was already caught by the tuner's dry run. Was: the style generator seeds on size and k, not the seed, so the tuner replays the same pairs. On 512, k 0 to 7 never plays Balanced, and `"style": "Turtle"` throws after a full run. | Seed on `seed0 + k`. Old baselines then don't compare. |
| ~~Low~~ Fixed | `UnitCommander.FinishDigDrag` | Fixed 2026-11-07: the box stops the Diggers' roaming. Was: a dig box doesn't stop roaming, so roaming Diggers soon leave the job. | `Roaming.Stop(diggers)` after the chunk loop. |
| ~~Low~~ Fixed | `UnitCommander.Update` | Fixed 2026-11-07: an open dialog ends the drag. Was: a box, wall or dig drag stays live through a dialog. After F1 in a wall drag, the next release places and pays for walls. | End the drag in that early return. |
| ~~Low~~ Fixed | `Rebinding` | Fixed 2026-11-07: `Rebinding.SameControl` takes a plain modifier path as covering its left and right keys (`ReviewV020Tests`). Was: the conflict check compares path strings, and defaults are `/ctrl`, `/shift`, `/alt` where a rebind records `/leftCtrl`. Stop on Left Ctrl reports no conflict. | Map left and right to the plain name first. |
| ~~Low~~ Fixed | `CommandCardController` Demolish text | Found fixed in the 2026-11-07 review: `SelectionModel.RefundFor` sums each building at its own type's cost. Was: it multiplies the first building's cost by the count, and the server refunds by type and skips the HQ. | Sum per id in `SelectionModel.Pull`. |
| ~~Low~~ Fixed | `WallDrag` | Fixed 2026-11-07: a cut line is traced again to its cut end, as the server will. Was: over the line limit, the preview cuts the line but the server traces a new one to the cut end, which can differ. | Re-run `WallRules.Line` to the cut end. |
| Low | `UnitCommander` Rally all | It stays armed after a HUD click, and Esc opens the menu as well, against the comments. | Add it to the Esc chain; drop it on a press over UI. |
| Low | Several | The minimap uses `e.shiftKey`, not the rebindable Queue key. `rejoinArmed` stays true after a refused rejoin. A wall drag can send 31 order chunks of a budget of 40. `HudController` doesn't split lists over 64 buildings, and the server refuses those. | Each is a line or two. |

### Tests failing on `main` at `ae223db`

- From `cb8f0b6` (Balance), whose numbers the tests still hold: `MapGeneratorTests.RecordedHashPerSize` (3), `PlaytestFix2Tests.TheSimReportsTheUpkeepOwedNotJustWhatWasAfforded`, `PlaytestFix3Tests.ABombersBlastDoesATenthToFriends`, `SimArsenalTests.TheDetonateOrderBlowsUpEverySelectedBomber`, `SimLifecycleTests.UnpaidUnitsDecay`, `SimLifecycleTests.UpkeepChargesPerUnitUntilBudgetRunsOut`. `SimAutoWorkTests.ItRepairsAnOwnBuildingBelowTheShareAndNotAnEnemys` was another; this branch fixes it. `gameplay.md` still says decay is 5 %, where the config now has 100.
- From before it, on this Windows PC too: `ManualTests.TheCompiledWordsAreTheApprovedFile` (needs `node tools/manual-mock.js --write-text`), `ReplayCursorTests` (2, a file still open when deleted), `UpnpProtocolTests.UsesUrlBaseWhenGiven`, `DebugApiPlayModeTests.ScreenshotWritesAPng`, `LobbyTests.MenuBattleRunsAndIsGoneOnceHostIsPressed`. `PerformanceTests.MinimapRedrawAtTenThousandOwnUnitsIsUnderOneMillisecond` timed out once on `main` and passed on this branch. `ReplicationPlayModeTests.UnitDrawsWhereServerHasIt` is flaky on both (see Movement).

## Seventh scan (2026-11-03): whole project

A targeted pass over the command surface, economy, production, the sim's command drain, reconnect, pause and per-client events (not the client UI, art, AI or pathing internals). All fixed (see [known-issues.md](known-issues.md), "Review fixes (2026-11-03)").

| Finding | Fix |
| ------- | --- |
| A unit queued by a producer could be made inside a building placed over its tile before the boundary: the spawn did not recheck the tile, and the building's push only moves units already in the arrays. | `SimCommandSystem.Spawn` picks the nearest free tile again (`TryFindFreeTile`, shared with `PushUnitsOffTiles`). |
| `FlushDeathEvents` and `FlushBlasts` filtered by the viewer's own grid, so a defeated spectator (who sees everything through replication) got no explosions or blasts. | `WorldStateManager.ViewerGrid` gives a spectator `SimData.SeesAll`, as `ReplicationService.GridOf` does. |
| `bombTiles` kept one bomb per tile: a bomb laid over another owner's unknown, unseen bomb overwrote it. | `BombTiles` keeps every bomb on a tile; removal is by id. |
| `ServerPlayer.AddIncome` counted income while away, and Miners and gem digs counted `Stats.Mined`, though nothing was credited. | Nothing is counted while away. |
| `HumansConnected` / `IsConnectedHuman` counted an away seat (identity kept, no connection). | Only seats with a live connection count. |

### Checked, no issue

Every `[Command]` gates, takes the owner from the sender and validates; gifts reject NaN, Infinity and overdrafts; order chunks are capped and squads pruned to owned units before counts are sent; rejoin tickets are revoked on leave, kick and elimination; repairs re-read the budget per repair; refused spawns refund and requeue; lobby AI commands check the state.

## Sixth scan (2026-10-28): v0.15 reconnect and QoL

All fixed in v0.15.1 (see [known-issues.md](known-issues.md), "Reconnect review fixes"); UI states on the Figma page `v0.15.1 Hotkeys and polish`.

| Finding | Fix |
| ------- | --- |
| Leave match kept the seat 60 s (`gameplay.md` promised it never would): the server cannot tell a leave from a drop. | `LeaveMatch` message first; the server revokes the ticket and closes the connection. |
| A `CommandGate` kick went through `TryMarkAway`, so the kicked client was offered a working Rejoin. | `CommandGate.TakeKicked`; a kicked seat is never kept. |
| An away player eliminated by an HQ loss kept a valid ticket; `RpcOnPlayerLost` was skipped (no connection), so a rejoin landed with no HQ and no Defeat screen. | `EliminatePlayer` ends the away seat at once. |
| A rejoin before the server noticed the drop was refused (no away seat yet), until the server's own timeout. | `TryTakeOver`: a valid token takes the seat from the old connection. |
| While a seat was away, any connection was let in and could sit without a player forever. | `RejoinGrace` (15 s). |
| `ServerPlayer.TrySpend` returned true without charging an away player, so a purchase applied after the drop was free; `Refund` went through `Add`, which drops money while away. | `TrySpend` refuses while away; upkeep uses `TryPayUpkeep`; `Refund` bypasses the freeze. |
| Producer queues and rallies were not resent on rejoin. | `WorldStateManager.SendOwnProducers`. |
| A map-hash mismatch disconnect counted as a drop. | It clears the ticket. |

## First review (2026-10-14): general

### High

| Finding                                                                                                                                                                            | Where                                                | Notes                                                                                                                                   |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| A command from a disconnecting connection throws on the server: the sender's owner id is read without a null guard.                                                                | `Net/OrderCommands.cs` (`TryActingOwner`)            | `sender?.identity == null` is not checked (unlike `Cmd_Surrender`, `Cmd_GiftResources`). Guard and return false first.                  |
| Same crash in the diplomacy path.                                                                                                                                                  | `Net/DiplomacyCommands.cs` (`TryChangeDiplomacy:40`) | `sim == null` is checked, the sender is not. Same fix.                                                                                  |
| Snapshot pacing reads as unlimited at `0` but blocks every new unit: with the default `0`, `Entered.Length >= MaxEnters` is always true, so no `Enter` is ever produced.           | `Net/Replication/InterestSets.cs:67-68,215`          | Fix: only pace when `MaxEnters > 0`. Verify what `ReplicationService` sets from `SnapshotBytesPerSecond`; if never set, this is live.   |
| Vision divides by zero when `VisionInterval` is `0`.                                                                                                                               | `Sim/SimVisionSystem.cs:37`                          | `SimEconomySystem` guards with `math.max(1, TickRate)`; vision does not. Enforce `VisionInterval >= 1` in `ConfigParser` or guard here. |
| Units past the global `Capacity` are silently dropped from the tick but never destroyed or refunded: they accumulate invisibly, cost ECS memory, and never tick, replicate or die. | `Sim/SimGatherSystem.cs:34-35`                       | Fix: refuse `SpawnUnit` at cap (like the per-player cap) and log.                                                                       |

### Medium

| Finding                                                                                                                                                                           | Where                                                                 | Notes                                                                                                                     |
| --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| `NaN` / `Infinity` costs pass payment: `cost <= 0` is false for `NaN`, `balance < NaN` is false, so `balance -= NaN` poisons `Resources` forever.                                 | `Rules/UpkeepRules.cs:5-11`, `ServerPlayer.cs:92-103`                 | Gifts guard this (`GiftRules.IsValid`); config/lobby paths do not. Fix: treat non-`cost > 0` as free/invalid explicitly.  |
| A voice is claimed before the clip is checked, so a missing `Audio/Sfx/{id}` burns a pool voice until stolen.                                                                     | `Audio/AudioService.cs:53-57`                                         | Fix: load `Clip(id)` first, return early on null.                                                                         |
| Placement uses exact float equality for right angles, so `89.9999` fails while `FacingOffset` would round it fine.                                                                | `Rules/PlacementRules.cs:63-67`                                       | Fix: tolerance via `DeltaAngle` (e.g. `< 0.01`).                                                                          |
| `PlotUnit` / `PlotBuilding` dereference a null fog array after `Begin(null)`.                                                                                                     | `Client/MinimapTexture.cs:59-68,92-103`                               | Current caller guards, but any other caller throws. Fix: return early when fog is null.                                   |
| A bare IPv6 address with a port (`::1:7778`) is silently taken as a host with the default port.                                                                                   | `Net/JoinAddress.cs:36-43`                                            | Fix: reject multi-colon input with a trailing `:port` unless bracketed (`[ipv6]:port`).                                   |
| `CommandGate` is static and only cleared via `Forget`; if the disconnect path does not call it, buckets leak and a reused `connectionId` inherits limits/violations (false kick). | `Net/CommandGate.cs:14,41`                                            | Verify `GameManager.OnServerDisconnect` calls `CommandGate.Forget`.                                                       |
| `MapGenerator` throws on seed `0` (`new Random(0)`).                                                                                                                              | `World/MapGenerator.cs:46-52`                                         | Lobby paths guard this; direct/test calls crash. Fix: map `0` to `1` or throw a clear `ArgumentException`.                |
| A multi-spawner queue command fans out to an unbounded selection; verify the server clamps to `MaxSpawnersPerCommand` (64).                                                       | `Rules/SpawnerRules.cs:11`, `UI/Controllers/HudController.cs:350-357` | Server comment says lists over the limit are refused (`CmdChangeQueues`); confirm the same guard covers every queue path. |

### Low / hardening

- ~~`Client/Settings/SettingsStore.cs:92` — `CreateDirectory(null)` for a bare filename.~~ **Fixed (2026-10-18):** a path with no directory part is written in place.
- ~~`Net/LobbyCommands.cs:132` — a full lobby falls back to colour `0`.~~ **Fixed (2026-10-18):** still unreachable; it now logs an error (`LogCategory.Lobby`) if it ever happens.
- ~~`Net/Upnp/UpnpProtocol.cs:138-150` — `IsPublic` misses TEST-NET / benchmarking ranges.~~ **Fixed (2026-10-18):** TEST-NET-2 and -3 join TEST-NET-1 and benchmarking (`UpnpProtocolTests.ClassifiesPublicAddresses`).
- ~~`Net/JoinAddress.cs:50` — `999.999.999.999` passes as a DNS name.~~ **Fixed (2026-10-18):** an all-numeric host must be a valid IPv4 address (`JoinAddressTests.RejectsMalformed`).
- ~~`Client/ClientWorld.cs` (`FireTracers`) — verify `PredictOne` is safe for unknown indices.~~ **Checked (2026-10-18), no change:** both calls are behind `IsKnown` (attacker) and `IdOf(index) == target` (target), and `IdOf` is bounds-checked.
- **`Client/GameInput.cs`** — `FastPan` / `AppendModifier` / `QueueModifier` share Shift; `QueueModifier` is rebindable while `AppendModifier` is not, so queue vs append diverge after a rebind. Share one action or exclude both.

## Second scan (2026-10-14): unit system

Unit-specific findings from the focused re-scan (spawn, orders, movement, combat, upkeep, selection, replication/prediction, rendering).

### High

| Finding                                                                                  | Where                             | Notes                                                                                                                                                                                                                                                                              |
| ---------------------------------------------------------------------------------------- | --------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Spawn position is unvalidated: `Spawn` writes `command.Position` straight into the unit. | `Sim/SimCommandSystem.cs:115-153` | `SpawnerSystem` passes a searched tile (valid), but `DevSpawnUnit` passes an arbitrary `float2`. A unit spawned inside a wall, footprint or off-map fails `Blocked` on every axis and sits stuck forever. Clamp to map bounds and reject non-walkable spots (or snap like orders). |

### Medium

| Finding                                                                                                                                                                            | Where                                                                  | Notes                                                                                                                                  |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| `TryAddBuilding` also lacks a sender guard (`sender.identity.GetComponent`), same crash class as the general High items.                                                           | `Unit/WorldStateManager.cs:506-518`                                    | Guard `sender?.identity == null` before use.                                                                                           |
| A unit pushed out of a new footprint with nowhere free stays inside the building (footprint search capped at 4096 tiles). **Partly fixed (2026-10-18):** a large unit with no clear spot now takes the nearest free tile without the clearance instead of staying put, and a unit with no free tile at all is logged. Failing the placement is still open (rare: needs 4096 used tiles around it). | `Sim/SimCommandSystem.cs` (`PushUnitsOffTiles`), `Rules/TileSearch.cs` |                                                                                                                                        |
| ~~Heal/repair path is one-way: `ClientBuildings.SetHealth` has no cap check against max.~~ **Fixed (v0.12):** the health is clamped to `[0, max]` (unknown ids are still ignored). | `Net/Replication/BuildingReplication.cs` (`ClientBuildings.SetHealth`) |                                                                                                                                        |
| ~~Health byte quantisation hides near-death.~~ **Fixed (2026-10-15):** see the triage table.                                                                                         | `Net/Replication/Messages.cs` (`HealthUnit`)                           |                                                                                                                                        |
| ~~Squad counts can go stale between pushes.~~ **Fixed (2026-10-18):** a squad order that prunes dead members marks the counts dirty, so they go out on the next settled tick (the 2 s push still covers deaths without an order). | `Net/OrderCommands.cs` (`CmdOrderSquad`, `SendSquadCounts`)             |                                                                                                                                        |
| ~~Attack tracers from an unknown attacker draw from `(0,0)`.~~ **Fixed (2026-10-15):** see the triage table.                                                                         | `Client/ClientWorld.cs` (`FireTracers`)                                |                                                                                                                                        |

### Low / hardening

- ~~Per-tick spawn bursts rely on an uncommented `UnitsBySlot++`.~~ **Fixed (2026-10-18):** commented as load-bearing in `SimCommandSystem.Spawn`.
- ~~`KillUnits` decrements `UnitsBySlot` although the economy recounts it.~~ **Fixed (2026-10-18):** commented; it is not redundant (a spawn later in the same drain needs the room).
- ~~`moversByClass` only has 2 size-class buckets.~~ **Fixed (v0.12):** one bucket per `FlowSizeClass`, no `& 1`, and a spawn with a size class outside `FlowSizeClass.Count` is refused. Spawners and push-outs also respect large-unit clearance (`SimLargeClassTests`). **Fixed again (2026-10-18):** both searches had the clearance check in the walk instead of the goal, so a search that started inside a footprint, or beside a new wall, could never leave it: a Heavy Factory never spawned a Heavy or Artillery (`TryFindSpawnTile`), and a Heavy under a new wall was never pushed off (`PushUnitsOffTiles`). Covered by `ArsenalMatchTests.EveryProducerMakesEachOfItsUnits` and `SimLargeClassTests.AWallRaisedUnderALargeUnitPushesItToAClearSpot`.
- ~~**`OrderBook.GoalSnapRadius` (12) can move a rally or order goal far from what the player clicked** with no client feedback.~~ **Fixed (2026-10-18):** the client works out the moved goal and shows it (Figma `v0.12 Fixes`; `OrderFeedback`); a rally flag sits at the moved spot. Show the snapped goal (or reject rock goals for rallies).
- ~~**`WaypointBook` silently drops waypoints past `MaxQueued` (4).**~~ **Fixed (2026-10-18):** with Shift held the selection's goals show numbered, and past the limit the cursor says "Queue full" and the order is not sent (`OrderQueueTracker`). Intended, but the client shows no "queue full" state; surface it in the HUD.
- **Box-order edge exclusion (`Sim/SimCommandSystem.Move.cs:45-52`).** Units exactly on the max edge are excluded (`p >= hi`); consistent with inclusive tiles, but drag boxes drawn on tile edges can miss visibly-inside units by a pixel. Inset or round the box before testing. *(2026-10-18: player box selections are made on the client and sent as ids; only the menu battle and dev tools send box orders, so this is not visible in play.)*
- **Selection has no unit cap.** A 10k-unit box select sends ~5 order chunks under one token (`MaxOpenTokens` 4, token timeout 2 s); a slow connection can strand the pending order. Fine today, revisit with larger armies.
- ~~Ghost building selection may outlive the building.~~ **Checked (2026-10-18), no change:** a `Gone` record raises `ClientBuildings.Removed`, and `UnitCommander.OnBuildingRemoved` calls `Selection.RemoveBuilding`.

## Third scan (2026-10-14): stuck pathfinding (playtest reports)

Playtesters report pathfinding getting stuck randomly at small and large unit counts: some units wedge into walls, some stop moving at random. Tracing order issue, flow-field rebuild, steering (`DesiredVelocityJob`), integration (`IntegrateJob`), and arrival/waypoint advance shows four interacting defects, not one bug — which matches the randomness and the small-and-large spread.

### High

| Finding                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           | Where                                                                                        | Notes                                                                                                                                                                                                        |
| ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| ~~Orders steer with no field for ~1s+ after issue: units beeline straight at the goal into walls.~~ **Fixed (2026-11-02, owner's decision: wait):** a unit holds until its order's route is ready. `DesiredVelocityJob` only follows a field when `OrderReady[order] != 0`, else steers straight at the goal. But `Issue()` only sets `Live`, never `Ready`; `Ready` is set in `AtBoundary` only if the cache has published, while `ScheduleRebuilds` runs later in `SimEndSystem` (OrderLast) capped at `MaxFieldRebuildsPerTick = 2`, and same-boundary rebuilds were removed (TODO comment). Even one order waits 1+ ticks; queued orders (size-class splits, waypoint legs) queue behind each other. Units grind along walls via axis-separated integration and end up wedged behind corners; if the goal sector rebuild is still queued on arrival, they sit in `AtGoal` until the arrival radius catches them. | `Sim/SimMovementSystem.cs:167-174`, `Sim/OrderBook.cs:84-105,166`, `Sim/SimTickGroup.cs:121` | Fix: restore a same-boundary fast path for at least the goal sector, or hold units (zero desired velocity) until `Ready`. Minimum: steer toward the route's first waypoint, not the raw goal, while unready. |
| Arrival radius scales with unit count: `goalRadius + (count-1)*0.5` means a 500-follower order "arrives" hundreds of tiles early, clears the order, and goes Idle.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                | `Sim/SimMovementSystem.cs:195-196`                                                           | Fix: cap the spread term (e.g. `min(count, N)*0.5` with small N), or spread goals per unit (goal-spread already exists at issue time — reuse per-unit offsets).                                              |

### Medium

| Finding                                                                                                                                                                                                                                                                                                                                  | Where                                                                                               | Notes                                                                                                                                                                                      |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Route-miss extension retargets where the unit is, not where it is going: an off-route unit steers straight at the goal and queues its own sector, `AddStartSector` rebuilds behind it, and with throttled rebuilds the unit ping-pongs (straight-line, miss, rebuild-behind). In woods/corridors this reads as vibrating against a wall. | `Sim/SimMovementSystem.cs:176-183`, `Sim/OrderBook.cs:160-164`, `Pathing/SectorFieldCache.cs:17-22` | Fix: extend toward the goal sector (or the straight-line lookahead sector) and keep last valid heading instead of beelining — the `SectorFieldCache` doc comment already prescribes this.  |
| Possible diagonal corner-cutting mismatch between the whole-map and sector field builders can wedge units into wall pockets with no unstick logic anywhere in movement.                                                                                                                                                                  | `Pathing/FlowField.cs` (both builders), `Sim/SimMovementSystem.cs:303-312`                          | Fix: verify both builders share the corner rule; add a stuck detector (position delta ~0 over N ticks with live order → re-issue route / perpendicular nudge / re-queue sector extension). |

### Low / hardening (pathfinding)

- ~~**`OrderBook.GoalSnapRadius` (12) silently relocates rock/footprint goals up to 12 tiles.**~~ **Fixed (2026-10-18):** shown on the client, see above. Units "stop" somewhere unclicked. Show the snapped goal or reject rock goals for rallies (`Sim/OrderBook.cs:106-130`).
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

| Finding                                                                                                                                                                                                                                                                                                                                                                                                          | Where                                       | Notes                                                                                                                                                                                     |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The client predicts at the newest batch's tick with no render delay: every batch arrival snaps all units to the server's newest position, then the client extrapolates until the next snap. `serverTime = max(serverTime, tickTime)` on arrival, `PredictAll(serverTime + sinceBatch)` between. At 20 Hz that is a snap every ~50 ms on every unit, worse with network jitter since there is no dejitter buffer. | `Client/ClientWorld.cs:108-114,135,161-164` | Fix: render ~100 ms behind (`PredictAll` at `newestTickTime - 0.1s`) so batches usually arrive before their time is drawn and motion is continuous; only extrapolate past confirmed data. |

### Medium

| Finding                                                                                                                                                                                                                                                                                                                                                                                                                        | Where                                                                                                                    | Notes                                                                                                                                                                |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The 0.1 s correction blend is defeated by real frame timing: decay is `exp(-elapsed / 0.1)` on wall-clock time between `PredictAll` calls, so at 20 fps or after any hitch the blend vanishes in one frame, and any correction over `MaxBlendTiles` (2 tiles) skips blending entirely. Offscreen-threshold corrections (2.0 tiles) and resume changes routinely exceed it. Lower fps = snappier; weak machines report it most. | `Client/ClientUnitStore.cs:37-39,107-112,275-294`                                                                        | Fix: decay by tick-time not wall time, raise `BlendSeconds` to ~0.2 s, blend (don't cut) jumps up to ~4 tiles.                                                       |
| Correction quantisation plus resume jumps: `DeltaScale` 8 snaps corrections to a 1/8-tile grid while the server skips below 0.25 tiles (visible random-walk steps); `ApplyCorrection` re-anchors then resumes toward a possibly different leg, flipping predicted direction instantly; `StartRoute` restarts at the route's first waypoint, teleporting the prediction base while the unit is mid-leg.                         | `Net/Replication/MovementPrediction.cs:112-134`, `Assets/Resources/GameConfig.xml` (`DeltaScale`, `CorrectionThreshold`) | Fix: raise `DeltaScale` 8→16 and lower the threshold once a render delay absorbs the traffic; anchor `StartRoute` at the current predicted position, not waypoint 0. |
| Facing snaps instantly: `rotation[]` is set directly from velocity/target bearing each frame with no angular smoothing, and `UnitFrames.Direction` rounds to 8 facings, so heading noise flips the sprite 45° in one frame. Combined with the positional jitter above, units flicker between facings.                                                                                                                          | `Client/ClientWorld.cs:248-266`                                                                                          | Fix: slew `rotation[]` toward the target bearing at a capped turn rate.                                                                                              |

### Suggested fix order

1. Render delay (~100 ms behind newest tick) — single biggest win.
2. Frame-rate-independent blend (tick-time decay, ~0.2 s, blend up to ~4 tiles).
3. Smooth facing (capped turn rate).
4. Smaller corrections (`DeltaScale` 16, lower threshold, anchor `StartRoute` at predicted position).

## Fifth scan (2026-10-14): friend join fails from Play screen (playtest reports)

Playtesters report joining a friend's tunnel address works from the main menu but fails on the Play screen (Main menu, Play button, host/join screen).

### High

| Finding                                                                                                                                                                                                                                    | Where                                                                | Notes                                                                                                      |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------- |
| `ConnectToServer` silently drops joins while a client is already active: `if (!NetworkClient.active)` guards `StartClient` with no else path, so a Play-screen Join during a lingering connection does nothing but still shows Connecting. | `GameManager.cs:286-299`, `UI/Controllers/MenuController.cs:105-112` | Fix: if active, `StopClient` first then connect, or disable Join while `NetworkClient.active` with a hint. |
| Failed or cancelled joins leave stale `joiningAddress` so a second attempt misreports: cancel calls `StopClient` but `Update` only clears on inactive client, causing stuck Connecting or wrong Failed versus Refused text.                | `UI/Controllers/MenuController.cs:114-151`                           | Fix: clear `joiningAddress` on cancel and on disconnect callback, and reset `wasConnected` per attempt.    |

## v0.7 branch review (2026-10-16, unfixed)

Low severity, deferred from the `/code-review` of the siege client work:

- ~~A ghost that re-enters sight with less health than last seen flashes red.~~ **Fixed (2026-10-18):** `TrackHits` remembers whether the building was a ghost last frame and does not flash on the way back.
- ~~Repair sparks can fire on the last step of construction.~~ **Fixed (2026-10-18):** sparks need the building to have been finished at its previous health change too.
- ~~`BuildingPacking` sorts with the unstable `List.Sort`~~ **Fixed (v0.12):** ties break on x.
- ~~The Radar range ring (`ClearingRing.Create`) never releases its Material.~~ **Fixed (2026-10-18):** every ring shares one material (colours are per vertex).
- ~~A wall drag sends Build orders only for the first `Orders/MaxQueued + 1` tiles.~~ **Fixed (v0.16):** idle Builders take the next unfinished wall within `AutoWork/Radius` on their own, so they carry on along the line (`SimAutoWorkTests.ALongWallDragGetsBuiltToTheEnd`).
- ~~`ToArray` is duplicated in `UnitCommander` and `HudController`~~ **Fixed (2026-10-18):** both use `Enumerable.ToArray`. `InstancedBuildingRenderer.LastColourOf` is still an O(n·m) test hook.

## Whole-project review (2026-10-16)

A review of the server side (commands and validation, sim tick stages, replication, economy, Steam sign-in, `tools/steam-upload.sh`) found eight issues the sections above did not list. The first three are fixed.

### Fixed

| Finding                                                                                                                                                                                                                                                                                                  | Fix                                                                                                                                                                                                                                                                                                                                                                                                     |
| -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| A Steam sign-in still in progress when the client disconnected was never cleaned up: a late "accepted" verdict recorded the dead connection's Steam ID (never forgotten), accepted the dead connection, and never ended the Steam auth session; a retry within 10 s was refused as "Already signing in". | `GameManager.OnServerDisconnect` calls `SteamAuthenticator.OnServerDisconnect`, which drops the connection's wait and its `SteamAuthSessions` entry; `SteamAuthSessions.Forget` also drops an undrained verdict and ends an accepted session (`SteamAuthTests.AConnectionThatLeft*`).                                                                                                                   |
| Attack messages named the target to every client that saw the attacker, so a client learned the id of a unit or building in its fog (and that a ghost building was still alive).                                                                                                                         | The encoder names a target only when the client may know it: a unit in its interest set, or a building it owns or (unless hidden) its grid sees; otherwise the target is sent as 0 and the client draws no tracer (`ReplicationEncoder.MayKnowTarget`, `ReplicationService.AttackTargets`; `ReplicationTests.AttackOnAUnitInTheFogDoesNotNameIt`, `AttackOnABuildingNamesItOnlyToItsOwnerOrWhoSeesIt`). |
| A unit on a wall tile when the wall rose was not pushed off: `SimCommandSystem` cleared `PendingMoves` after applying wall work, discarding the push. Builders' occupied-tile check hid it.                                                                                                              | The pending orders and moves are cleared before the boundary applies any work (`SimWallTests.AUnitOnARisingWallIsPushedOffIt`).                                                                                                                                                                                                                                                                         |

### Still open (low)

- ~~`TryAddBuilding` charges before `Ids.Allocate()`, which throws when ids run out~~ **Fixed (v0.12):** the cost is refunded and the placement refused (`WorldStateManager.PlaceBuilding`).
- Unit upkeep is charged against a budget copied at the boundary while building upkeep and spawners spend from the same balance in between; `Sim.Spend` then takes what is left, so a short player gets some unit upkeep free (`WorldStateManager.OnStartServer`, `Sim.Spend`).
- ~~Nicknames keep format characters~~ **Fixed (v0.12):** format and line/paragraph separators are dropped (`CommandValidatorTests.Nickname_DropsInvisibleAndDirectionCharacters`). Duplicate names are still allowed.
- ~~Some loops over `ServerPlayers` don't skip a null identity~~ **Fixed (2026-10-18):** `CheckHQPlacementProgress`, `StartMatchServerSide`, `AssignTeams`, `DeclareWinner`, `DeclareDraw` and `WinLossSystem` skip one, like the rest.
- ~~`tools/steam-upload.sh` writes the description into the VDF unescaped~~ **Fixed (v0.12).** ~~Cancelling a blueprint counts as a building lost on the end screen.~~ **Fixed (2026-10-18):** a cancelled blueprint counts as neither built nor lost (`MatchStats.BlueprintCancelled`; `MatchStatsTests.ACancelledBlueprintIsNeitherBuiltNorLost`). (It built Windows while the docs still called Windows deferred; the docs now say Windows is built.)

## Replay and performance follow-up (2026-10-18)

Reviewed the current v0.12 replay path and the latest mixed-roster performance results.

### Medium

| Finding                                                                                                                                                                                                                                                                                                                                                                                                                       | Where                                                                                                                 | Notes                                                                                                                                                                                                                                                                                                                                                                                              |
| ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Fixed (2026-10-18):** playback stops when the cursor reaches the end of the file (`ReplayCursor.AtEnd`), and `ReplayPlayer.Incomplete` tells a cut-short recording from a normal end (`ReplayCursorTests.ReachesTheEndOfARecordingWithoutAnEndRecord`). Original finding: a replay stopped at its size cap (or when the writer queue is full) can be saved without an `End` record, but the viewer only stops playback when it applies that record. `MatchRecorder.End` retries the record through the ordinary size/queue limits and finalizes the file even if `Enqueue` refuses it; `ReplayPlayer` has no EOF completion path. The viewer therefore stays in Playing at the last frame indefinitely. | `Net/Replay/MatchRecorder.cs` (`End`, `Enqueue`); `Client/Replay/ReplayPlayer.cs` (`ReplayCursor.AdvanceTo`, `Apply`) | Stop cleanly when the cursor reaches EOF, or reserve capacity for the terminal record. EOF handling also covers otherwise valid partial recordings; if truncation must remain distinguishable from a normal end, expose an incomplete status instead of treating EOF as a clean outcome. `MatchRecorderTests.StopsAtTheSizeCapAndKeepsAPlayableFile` checks readability but not terminal playback. |

### Low / optimization

- **Replay open decompresses the entire recording synchronously.** `ReplayCursor` scans every record to find the last tick in its constructor, then reopens the file; `ReplayPlayer.Open` does this on the caller's thread. Large recordings can stall the menu and incur a full extra decompression pass before playback. Reuse the duration sidecar or add a validated footer/index; a sidecar is simpler but can be stale or missing, while an index adds format and recovery complexity. Moving the scan to a worker avoids a UI stall but needs a loading state and cancellation on close.
- **Replication remains the main tick cost and has little headroom.** The final v0.12 gate reports 16.3 ms replication p95 inside 23.8 ms `tick.main` p95, with one run at 25.1 ms; terrain's shared read reduced its measured p95 from 7.8 to 2.6 ms, but the per-client interest pass still revisits the lifetime set of changed tiles. This is already tracked in [known-issues.md](known-issues.md). Further indexing changed tiles by visibility region could reduce repeated scans, at the cost of index memory and more bookkeeping when terrain changes or clients' visibility moves; profile the next content load before taking on that complexity. See [v0.12 performance results](perf/v0.12/README.md).

## Command validation, lobby flow and match lifecycle review (2026-10-18)

Reviewed the server-authoritative control surfaces: `CommandGate`, `CommandValidator`, `GameCore`, `LobbyCommands`, `MatchSettings`, `OrderCommands`, `StartPoints`, and `WinLossSystem`.

### Confirmed no material issue found

- **Command validation is narrow and server-authoritative.** `CommandGate.Allow` rejects null or over-limit connections before the command runs, then each command verifies its actor and the match state (`CurrentState == Playing` or `Lobby` as appropriate). `TryActingOwner` is safe because the gate forbids a null `sender` or a missing `identity` before the owner id is read.
- **Lobby and team flow are consistent with the design.** `Cmd_SetMatchSettings`, `Cmd_SetColour`, `Cmd_SetOwnTeam`, `Cmd_SetReady`, `StartMatchServerSide` and `AssignTeams` all check the current state before mutating the match, and the lobby rules enforce colour/team range and readiness semantics. `MatchSettingsRules.IsValid` rejects invalid map sizes, seeds and pick-time values before they reach the server.
- **Win/loss state transitions are in the right lifecycle order.** `GameCore` transitions to `Playing` only after the HQ placement countdown, then `WinLossSystem` resolves the outcome after the world has been settled; the winner/draw declarations then wipe the world and send the end-screen target Rpcs. No null or stale connection path was visible in the reviewed call chain.

### Low / hardening

- ~~**Dev-only bots are intentionally outside the civilian player count.**~~ **Resolved (v0.14):** bots became AI seats; `MatchStartPlayerCount` counts humans plus AIs at launch (`SeatCount`), and a Scripted seat added mid-match adds one.
- **`ResetToLobby` clears both the live player list and the AI seats** (v0.14: `ClearAiPlayers`, which also destroys their player objects). Deliberate: a server whose last human left returns to an empty lobby, AIs included.
- ~~`DeclareWinner` / `DeclareDraw` assume populated `ServerPlayers` entries.~~ **Fixed (2026-10-18):** both skip a null identity.

The reviewed command, lobby and lifecycle surfaces are now covered in this ledger; no high-confidence bug remains in the server-owned control path as inspected.

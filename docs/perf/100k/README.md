# 100,000 units: benchmark and results (2026-11-07, updated 2026-11-08)

Goal: server gameplay at the largest scale, 100,000 units (8 players × 12,500, the unit cap) fighting on the 1024 map, with the
client at 140 fps or more on average and in the slowest 1 % of frames, a smooth server, orders that answer at once and big groups
that are easy to command. Branch `perf/100k-units`, on `main`.

## The benchmark: `tools/perf-100k.sh`

`tools/perf-100k.sh [runs] [player]` runs the same harness as `tools/perf-10k.sh` ([perf/v0.20](../v0.20/README.md)) at the
gate's full scale:

- **Match.** 8 owners × 12,500 units on the gate's 1024 map, seed 1: the v0.3 battle script on four fronts (a quarter of each
  army re-ordered every 2 s), the siege load, the v0.13 defences and reinforcements that keep each army at strength.
- **Server.** The host, headless (`-batchmode -nographics`), on the same machine as the client.
- **Measured client.** One real player over KCP on loopback, 1920×1080 fullscreen, default settings, VSync off, no frame cap.
  Every owner shares vision (`-perfShareVision`), so it knows all 100,000 units; every second it zooms out, as a player can (never
  past the player's limit), toward showing them all: about 11,000 to 15,000 are on screen.
- **Sampling, metrics and budgets** as in [perf/v0.20](../v0.20/README.md): every frame in `client1/frames.csv`, the 1 % low is the
  frame rate over the slowest 1 % of frames. Budgets here: client mean and 1 % low ≥ 140 fps; server `tick.main` p95 ≤ 28 ms (the
  roadmap's, now at 100,000 units); no errors or dropped connections.

### Machine

Intel Core Ultra 9 275HX (24 cores), NVIDIA GeForce RTX 5070 Ti Laptop GPU (Direct3D 12), 32 GB, Windows 11, on AC power, with
other programs running. Unity 6000.4.1f1, **Mono**, Windows release player (`WAR-2D/Build/Windows (Steam)`). The client is
CPU-bound: GPU 0.5 to 0.7 ms a frame.

## Results on a simulated owner's machine (2026-11-08)

The owner's machine (Ryzen 5 5600GT, 6 cores and 12 threads, Radeon RX 6600) is estimated here, not measured. Every game process
is held to 8 of the laptop's E-cores (CPUs 2 to 9, `cmd /c start "" /affinity 3FC`): a Skymont E-core at up to 4.6 GHz is close to
a Zen 3 core per thread, and 8 of them (no SMT) are close to the 5600GT's throughput (Cinebench R23: about 1,450 single and 11,600
multi for the 5600G family). Unity starts 23 job threads whatever the affinity, so every process gets `-job-worker-count 11`, as on
12 hardware threads (`WAR_PERF_HOST_ARGS`, `WAR_PERF_CLIENT_ARGS`). The GPU is the laptop's RTX at about 0.5 ms a frame; an RX 6600
has about half its throughput (Time Spy graphics about 8,060), so about 1 ms: not the limit at these frame rates. The game window
is kept in front during a run (on this laptop a window behind another one is slowed down).

**Before** is `main` (`471bd0f1`) with the benchmark harness and the 12,500 cap cherry-picked onto it; **after** is this branch.
Single runs, interleaved (before, after, before, ...) so both builds see the same machine load; medians in bold.

### A remote player (the host on other cores)

The measured client alone on the 8 E-cores and the headless host on the laptop's other cores, as when the player joins a game
someone else hosts.

| Metric | Before (3 runs) | Before | After (3 runs) | After |
|---|---|---|---|---|
| Client fps (frames ÷ time) | 58.3 / 60.7 / 56.5 | **58.3** | 164.1 / 162.2 / 174.8 | **164.1** |
| Client 1 % low fps | 33.7 / 33.3 / 32.1 | **33.3** | 101.5 / 100.6 / 106.1 | **101.5** |
| Frame time p50 (ms) | 16.47 / 15.68 / 16.92 | **16.47** | 5.96 / 6.06 / 5.60 | **5.96** |
| Frame time p95 (ms) | 24.44 / 23.41 / 25.32 | **24.44** | 7.93 / 7.99 / 7.38 | **7.93** |
| Frame time p99 (ms) | 27.62 / 27.23 / 28.86 | **27.62** | 8.96 / 9.03 / 8.48 | **8.96** |
| Frame time max (ms) | 38.2 / 35.2 / 36.2 | **36.2** | 17.1 / 17.6 / 16.5 | **17.1** |
| CPU main thread, mean (ms) | 17.15 / 16.47 / 17.69 | **17.15** | 6.08 / 6.16 / 5.72 | **6.08** |
| GPU frame, mean (ms) | 1.55 / 1.50 / 1.56 | **1.55** | 0.54 / 0.54 / 0.51 | **0.54** |
| Client process CPU (% of a core) | 160 / 166 / 160 | **160** | 447 / 439 / 441 | **441** |
| Client Unity memory (MB) | 458 / 474 / 486 | **474** | 508 / 481 / 498 | **498** |
| Units the client knows | 99,570 / 99,734 / 99,592 | **99,592** | 99,970 / 100,347 / 100,339 | **100,339** |
| Units on screen (mean) | 13,117 / 11,189 / 13,591 | **13,117** | 13,684 / 14,884 / 13,464 | **13,684** |
| Bandwidth mean (KB/s) | 435 / 396 / 433 | **433** | 477 / 542 / 509 | **509** |
| Server tick p50 (ms) | 12.5 / 12.4 / 12.6 | **12.5** | 14.4 / 14.3 / 14.2 | **14.3** |
| Server tick p95 (ms) | 20.2 / 19.7 / 19.3 | **19.7** | 23.1 / 23.4 / 22.6 | **23.1** |
| Server tick p99 (ms) | 68.7 / 69.1 / 72.1 | **69.1** | 32.2 / 33.4 / 30.7 | **32.2** |
| Server tick max (ms) | 79.2 / 78.7 / 84.5 | **79.2** | 56.8 / 44.3 / 37.4 | **44.3** |
| Server logged errors | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |
| Dropped connections | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |

### The hoster (host and client on the same 8 cores)

Host and client share the 8 E-cores, as when the owner hosts and plays on one PC.

| Metric | Before (3 runs) | Before | After (3 runs) | After |
|---|---|---|---|---|
| Client fps (frames ÷ time) | 55.2 / 53.9 / 54.3 | **54.3** | 186.7 / 172.9 / 183.2 | **183.2** |
| Client 1 % low fps | 20.0 / 31.1 / 27.7 | **27.7** | 84.8 / 76.0 / 78.0 | **78.0** |
| Frame time p50 (ms) | 16.74 / 17.66 / 17.42 | **17.42** | 5.18 / 5.59 / 5.26 | **5.26** |
| Frame time p95 (ms) | 26.88 / 26.42 / 26.24 | **26.42** | 6.84 / 7.53 / 7.01 | **7.01** |
| Frame time p99 (ms) | 40.23 / 29.97 / 31.51 | **31.51** | 9.86 / 10.80 / 10.37 | **10.37** |
| Frame time max (ms) | 73.2 / 37.2 / 52.5 | **52.5** | 19.4 / 24.4 / 21.7 | **21.7** |
| CPU main thread, mean (ms) | 18.12 / 18.54 / 18.41 | **18.41** | 5.35 / 5.78 / 5.45 | **5.45** |
| GPU frame, mean (ms) | 1.47 / 1.62 / 1.57 | **1.57** | 0.51 / 0.53 / 0.50 | **0.51** |
| Client process CPU (% of a core) | 154 / 157 / 161 | **157** | 416 / 414 / 403 | **414** |
| Client Unity memory (MB) | 465 / 459 / 460 | **460** | 502 / 482 / 489 | **489** |
| Units the client knows | 99,607 / 99,624 / 99,661 | **99,624** | 99,107 / 98,978 / 99,075 | **99,075** |
| Units on screen (mean) | 13,501 / 13,279 / 13,626 | **13,501** | 14,230 / 14,258 / 14,479 | **14,258** |
| Bandwidth mean (KB/s) | 484 / 527 / 495 | **495** | 513 / 541 / 557 | **541** |
| Server tick p50 (ms) | 16.4 / 16.9 / 16.9 | **16.9** | 25.0 / 27.7 / 27.3 | **27.3** |
| Server tick p95 (ms) | 31.1 / 31.6 / 31.4 | **31.4** | 45.6 / 51.2 / 49.8 | **49.8** |
| Server tick p99 (ms) | 79.6 / 82.2 / 80.0 | **80.0** | 57.0 / 60.4 / 54.8 | **57.0** |
| Server tick max (ms) | 116.2 / 128.2 / 122.1 | **122.1** | 94.4 / 76.1 / 71.1 | **76.1** |
| Server logged errors | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |
| Dropped connections | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |

### The hoster at the game's default frame cap (144 fps, `-perfFpsCap 144`)

`main` stays under 60 fps here, so the cap only touches the branch.

| Metric | Before (2 runs) | Before | After (2 runs) | After |
|---|---|---|---|---|
| Client fps (frames ÷ time) | 57.4 / 57.6 | **57.5** | 141.6 / 141.0 | **141.3** |
| Client 1 % low fps | 32.8 / 32.5 | **32.7** | 83.4 / 76.3 | **79.8** |
| Frame time p50 (ms) | 16.60 / 16.64 | **16.62** | 6.95 / 6.95 | **6.95** |
| Frame time p95 (ms) | 25.17 / 25.06 | **25.12** | 7.48 / 7.66 | **7.57** |
| Frame time p99 (ms) | 28.12 / 27.75 | **27.93** | 10.25 / 10.75 | **10.50** |
| Frame time max (ms) | 37.2 / 39.3 | **38.2** | 21.4 / 25.6 | **23.5** |
| CPU main thread, mean (ms) | 17.41 / 17.35 | **17.38** | 5.73 / 5.82 | **5.77** |
| GPU frame, mean (ms) | 1.46 / 1.43 | **1.44** | 0.55 / 0.54 | **0.55** |
| Client process CPU (% of a core) | 162 / 166 | **164** | 332 / 326 | **329** |
| Client Unity memory (MB) | 481 / 481 | **481** | 485 / 494 | **490** |
| Units the client knows | 99,517 / 99,583 | **99,550** | 98,890 / 98,369 | **98,629** |
| Units on screen (mean) | 13,992 / 13,963 | **13,978** | 15,586 / 14,881 | **15,234** |
| Bandwidth mean (KB/s) | 506 / 502 | **504** | 543 / 703 | **623** |
| Server tick p50 (ms) | 16.4 / 16.6 | **16.5** | 22.6 / 23.1 | **22.8** |
| Server tick p95 (ms) | 30.5 / 30.7 | **30.6** | 40.6 / 43.2 | **41.9** |
| Server tick p99 (ms) | 79.9 / 80.0 | **79.9** | 55.3 / 54.0 | **54.7** |
| Server tick max (ms) | 115.4 / 115.5 | **115.5** | 80.7 / 75.0 | **77.8** |
| Server logged errors | 0 / 0 | **0** | 0 / 0 | **0** |
| Dropped connections | 0 / 0 | **0** | 0 / 0 | **0** |

### The gate (headless host and 7 headless clients on the 8 cores, no rendering)

The `tools/perf-run.sh` form: normal interest, no shared vision.

| Metric | Before | After |
|---|---|---|
| Tick p50 (ms) | 10.0 / 10.5 (**10.2**) | 10.4 / 10.1 (**10.2**) |
| Tick p95 (ms) | 16.1 / 19.2 (**17.6**) | 16.2 / 16.5 (**16.4**) |
| Tick p99 (ms) | 71.0 / 71.8 (**71.4**) | 28.0 / 27.1 (**27.6**) |
| Tick max (ms) | 78.0 / 90.5 (**84.3**) | 43.4 / 41.7 (**42.5**) |
| Bandwidth mean (KB/s) | 218.5 / 220.6 (**219.5**) | 225.6 / 218.3 (**222.0**) |
| Bandwidth peak 1 s (KB/s) | 343.5 / 329.2 (**336.4**) | 389.8 / 317.2 (**353.5**) |
| Errors | 0.0 / 0.0 (**0.0**) | 0.0 / 0.0 (**0.0**) |

### Reading it

- **The client.** The remote player's frame rate is 2.8 times `main`'s (58 → 164 fps) and the hoster's 3.4 times (54 → 183):
  both pass 140. The 1 % low is about 3 times `main`'s (33 → 102 for the remote player, 28 → 78 for the hoster) but **under 140**
  (see "What is left" below). At the game's default 144 fps cap the hoster holds 141 fps, 1 % low 80.
- **The server alone (the gate)** is as fast or faster at every percentile: p50 10.2 ms on both, p95 17.6 → 16.4, p99 71 → 28,
  max 84 → 42 (the order waves: formations in Burst). Bandwidth is level (220 → 222 KB/s mean; peak 1 s 336 → 354, budget 768).
- **The server in the 100k benchmark.** p99 and max fall in every case (remote player 69 → 32 ms, hoster 80 → 57). **p50 and p95
  rise**: for the remote player 12.5 → 14.3 and 19.7 → 23.1 ms, inside the 28 ms budget; for the hoster 16.9 → 27.3 and
  31.4 → 49.8 ms (`main` misses the budget there too). Two causes: the branch sends each unit its real route as
  soon as its field exists (1.6 million route messages in a run against `main`'s 0.9 million; `main` corrects the unit's position
  later instead), which the remote player's case shows alone; and on the hoster's shared cores the client, at 3.4 times the frame
  rate, uses 4.1 of the 8 cores (`main`'s 1.6), so the server gets less of them. A frame cap gives some back: p95 42 ms at 144 fps,
  36 ms at 60 fps (a build before the last two commits). The order table's size is not the cause: with room for 256 orders a player
  the tick was the same.
- **Bandwidth in the 100k benchmark** rises 9 to 18 % (remote player 433 → 509 KB/s, hoster 495 → 541): the same prompt routes.
  Every owner shares vision there, so the player is sent all 100,000 units; in the gate's normal interest it is level.
- **Orders.** A player's click (`SimCommand.Urgent`) has its fields built on the next tick; AI, rally and scripted orders keep
  `main`'s per-tick budget, which is why the gate's bandwidth matches `main`.

On the full laptop (24 cores, no limits) an earlier build of this branch measured 75 → 182 fps mean and 36 → 83 fps 1 % low against
`main` (2026-11-07). The CSV files here are from the runs above: `100k-*` the remote player (`*-host.csv` the server, `*-client.csv`
the measured client) and `gate-*` the gate's first round. The client files' budget column is the 10k benchmark's 120 fps
(`PerfClient.FpsBudget`); this benchmark's goal is 140.

## What is left: the slowest 1 % of frames (2026-11-08)

Unity Profiler data (`-perfProfile`, Development build of this branch before its last two commits, 2,000 frames, the hoster on
the 8 E-cores): mean 6.6 ms a frame, p99 13.2 ms, the slowest 1 % 15.7 ms on average. Those frames are network-batch frames: the
network loop takes 4.5 ms in them (0.16 ms over all frames):

| In the slowest 1 % of frames | ms |
|---|---|
| Network loop (`NetworkLoop`), all of it | 4.5 |
| Unit messages (`Receive.Units`) | 2.3 |
| of which units leaving (`ClientUnitStore.Leave`: death effects, selection, events) | 1.0 |
| of which the wait for the previous batch's route job (`ApplyRoutes`, `JobHandle.Complete`) | 0.45 |
| Terrain changes (`Receive.Terrain`) | 0.4 |
| Mirror's own receive and dispatch (the rest, not edited: vendored code) | about 1.5 |

The rest of a slow frame is the draw and a present wait. A frame at 140 fps has 7.1 ms, so a batch frame would need about half of
its network work gone: spreading a batch's apply over several frames (Leave and corrections) is the next step, perhaps 10 to 20 fps
on the 1 % low. Since this profile, a death reads the camera view once a frame and makes no new strings (`e8ae3e75`), and a unit
that stood still is no longer sent a stub route while its new order's field is built (12 % fewer route messages, `ab03a2c0`).

## What was slow (profiler evidence, 2026-11-07)

Unity Profiler data (`-perfProfile`, Development build, 2,000 frames) of the branch merged with `main`, before its last two
commits. Mean 8.2 ms a frame (Development build), p99 15.9 ms; the slowest 1 % averaged 18.6 ms against 7.8 ms for the middle half.
What the slowest 1 % spent more than the middle half:

1. **Route arena compaction** (`ClientUnitStore.ApplyRoutes`): 13 to 17 ms in 4 of the 20 slowest frames. Every few order waves the
   arena of waypoints was compacted on the main thread. Now a prefix sum on the main thread (about 0.1 ms) and the copy over the
   job threads in small pieces, swapped in when done.
2. **Other messages** (`ClientUnitStore.ApplyOther`): +0.8 ms, up to 4 ms. Corrections (7,000 to 8,000 a batch at this scale) and
   health changes were decoded in managed code; now Burst. Enter and Leave are still managed (they raise events the effects use).
3. **The HUD**: +1.4 ms, from three single frames of 6 to 7.6 ms in its own method body (no marked child): likely first-call JIT
   under Mono. And the minimap's redraw every 0.2 s: 1.3 to 1.7 ms (Development build), of which the unit plot is 0.8 to 1.1 ms
   and the terrain fill 0.3 to 0.45 ms (now over the job threads).
4. **Waits for job threads** (`Draw.Prepare`, `UpdateAllRenderers`): +0.8 ms each in the slowest frames. The headless host runs
   on the same machine at 4.5 to 5.7 cores; when its tick and the client's jobs meet, the client waits.
5. **Garbage collection**: incremental steps of about 3 ms (7 in 2,000 frames) and 4 blocking collections of 2 to 3 ms. Steps are
   now 1 ms.

Constant per-frame costs cut on the way: every known unit was packed and uploaded each frame (now only those within 2.5 tiles of
the view: about 13,000 of 100,000); every building was packed and sorted (now only those in view), and its hit and finish tables
were rewritten each frame (now only on change); each effect set its sprite each frame (now when its frame changes); every
burning unit's fire was sorted (now only those in view); each own unit that appeared walked every building (now the player's
producers, once a frame).

## Tests

EditMode and PlayMode through the editor in batch mode (`Unity -runTests --burst-force-sync-compilation`) on Windows, at this
branch's head:

| Suite | Branch |
|---|---|
| EditMode | 1,537: 1,534 pass, 0 fail, 3 skipped (as on `main`) |
| PlayMode | 103: 101 pass in the last full run; the 2 that failed (`ArsenalMatchTests.ArtilleryShellsATargetOnAScoutsSight`, `HudTests.GiftToEnemyMovesResources`) pass when their fixtures run again |

A full PlayMode run here fails one or two timing-dependent tests, a different set each run, and each passes run alone: the two
above, `BastionClientTests.ATurretShootingOurUnitTurnsItsHeadAndFiresATracer` and
`PerformanceTests.MinimapRedrawAtTenThousandOwnUnitsIsUnderOneMillisecond` (it waits 30 s for its 10,000 units). The
select-buildings branch (`main` with only the selection change and the same six test fixes) fails the same way in full runs (the artillery test, the
minimap test, `AiPlayModeTests.APrivateOneVsOneAgainstEasyBuildsProducesAndAttacksTheHuman`), so these come from `main`
(`known-issues.md`). `main` on Windows also fails six tests every run; this branch fixes them (commit `6364ef38`).

New tests: `SimLifecycleTests.TheUnitCapIsTheMatchsUnitsSplitBetweenItsPlayers`, `ASplitCapRefusesTheSpawnPastItsShare`, an AI
seat counted in the cap at match start (`AiSeatPlayModeTests.AnAiCountsInOrderTeamsIncomeUpkeepStatsHudAndWinLoss`),
`SimMovementTests.OnlyAPlayersClickSkipsTheFieldBudget` (a click's fields are built on the next tick; an AI or scripted order
waits for the budget), the pushed and the still waiting units in
`ReplicationTests.AUnitWaitingForItsFieldGetsItsRouteAsSoonAsTheFieldReachesIt`,
`ReplicationTests.RoutesStayRightWhileTheArenaCompactsOnAJobThread` (400 orders through one store: the arena compacts on the job
threads while an untouched unit keeps its route and prediction), `HealthSetsOnlyKnownUnitsAndAMessageCutShortChangesNothing`,
and the Burst correction decoder checked against the managed one (and refusing a message cut short) in the message round trip.

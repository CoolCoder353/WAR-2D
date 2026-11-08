# 100,000 units: benchmark and results (2026-11-07, updated 2026-11-08 after the owner's runs and 2026-11-09 for 500 fps)

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
- **Every tick (fixed 2026-11-08).** The battle's orders, the top-ups and the tick samples now run on every tick
  (`SimTiming.TickEnded`). A host that falls behind its 20 Hz clock runs several ticks in one frame to catch up, and the harness
  used to act only on the last one, so the slower the host, the fewer orders and top-ups a run issued and the fewer ticks it
  sampled. That is why the owner's pinned runs settled at about 91,000 or about 100,000 units: here `main` on three cores saw
  only 250 of its 1,200 ticks and settled at 91,900 units in one run and 96,900 in the next. Now every run holds 99,500 to
  100,100 units, and `perf.tick.perFrame` (ticks a host frame ran: 1 when the host keeps up) shows how far the host fell behind.

### Machine

Intel Core Ultra 9 275HX (24 cores), NVIDIA GeForce RTX 5070 Ti Laptop GPU (Direct3D 12), 32 GB, Windows 11, on AC power, with
other programs running. Unity 6000.4.1f1, **Mono**, Windows release player (`WAR-2D/Build/Windows (Steam)`). The client is
CPU-bound: GPU 0.5 to 1.5 ms a frame.

## 500 fps on the owner's pinned layout (2026-11-09)

Goal: a mean of 500 fps or more for the 100k benchmark's client on the owner's pinned layout as simulated here (client on 3 E-cores,
host on 3 others, 5 job threads each; see below), all tests passing. Release builds, single runs; medians in bold. **Before** is this
branch at `331d380c` (the "after" column of the next section, 2026-11-08); **after** is this branch now.

| Metric | Before | After (5 runs) | After |
|---|---|---|---|
| Client fps (frames ÷ time) | **116.1** | 602.9 / 644.8 / 627.6 / 631.4 / 604.9 | **627.6** |
| Client 1 % low fps | **73.8** | 96.1 / 112.6 / 122.1 / 135.1 / 108.5 | **112.6** |
| Frame time p50 (ms) | **8.48** | 1.17 / 1.14 / 1.23 / 1.26 / 1.33 | **1.23** |
| Frame time p95 (ms) | **10.95** | 5.33 / 4.83 / 4.98 / 4.85 / 4.93 | **4.93** |
| Frame time p99 (ms) | **12.41** | 8.72 / 7.20 / 6.28 / 6.07 / 7.01 | **7.01** |
| Frame time max (ms) | **18.4** | 22.5 / 22.8 / 37.8 / 32.7 / 25.4 | **25.4** |
| CPU main thread, mean (ms) | **8.61** | 1.65 / 1.55 / 1.59 / 1.58 / 1.65 | **1.59** |
| GPU frame, mean (ms) | **0.74** | 0.46 / 0.47 / 0.49 / 0.49 / 0.49 | **0.49** |
| Client process CPU (% of a core) | **231** | 168 / 164 / 155 / 153 / 157 | **157** |
| Units the client knows | **100,227** | 99,890 / 99,824 / 99,973 / 99,990 / 99,901 | **99,901** |
| Units on screen (mean) | **13,827** | 14,203 / 13,289 / 14,341 / 13,368 / 13,000 | **13,368** |
| Bandwidth mean (KB/s) | **483** | 475 / 478 / 500 / 475 / 494 | **478** |
| Server tick p50 (ms) | **23.6** | 24.6 / 23.9 / 24.0 / 22.8 / 24.7 | **24.0** |
| Server tick p95 (ms) | **40.1** | 45.7 / 44.1 / 39.7 / 41.7 / 43.2 | **43.2** |
| Server tick p99 (ms) | **50.2** | 65.2 / 67.1 / 52.9 / 52.2 / 66.0 | **65.2** |
| Host ticks a frame, mean (1 = keeping up) | **1.11** | 2.95 / 2.45 / 2.23 / 1.13 / 3.90 | **2.45** |
| Server logged errors, dropped connections | **0, 0** | 0, 0 every run | **0, 0** |

`main` with the harness read 56.3 and 58.8 fps before and after these runs, against 58.2 in the next section's: the gain is not a
better machine state. On the owner's machine, at about two thirds of these numbers, the client should land near 400 fps. The GPU is
not the limit: 0.5 ms a frame here, about 1 ms on an RX 6600. The host's code did not change, yet it read a little slower (tick p95
40.1 → 43.2 ms, and it fell behind its clock in some runs): a laptop shares one power budget between the CPU and a GPU now drawing
600 frames a second, and this one's speed swung by up to a third between runs of the same build during the session. No server code
changed in this round, so the gate below (no rendering) still holds.

What changed, in the order the profiles called for it (Development-build profiles of 2,000 frames on the same layout):

- **Units away from the view wait for their turn.** `ClientUnitStore.PredictAll(now, cull, refresh, ahead)` predicts every unit
  drawn inside the view (with the turrets' margin and 6 tiles more), and the others a share at a time, each at least every 0.5 s
  (`OffViewRefreshSeconds`), less than the fastest unit or leap needs to cross that margin. The draw jobs skip a waiting unit. At
  100,000 known units about 14,000 are on screen. The wait test reads only arrays in slot order.
- **The units are packed at 60 Hz and carried on between packs.** Prediction, facing, frame, colour, hit feedback, turrets and the
  instance upload run at most every 1/60 s (`UnitPackSeconds`), or when the known count changes. Each instance now carries a velocity
  (`UnitInstance` is 40 bytes) toward where the next pack will draw it, and the frames between only draw the last pack again with
  the time since it (`_UnitDrawAhead`), so units, turrets, rings and bars still move every frame. Facing, animation frames, the hit
  flash, selection rings and the combat sprites (fires, lasers, leaps, reload bars) change at 60 Hz.
- **Drawing walks a route from where it last was.** A per-unit cursor (`RouteCursor`) resumes the route walk at the waypoint the last
  draw passed, and the known list goes back to index order whenever an eighth of it has moved, so the draw jobs read their per-index
  arrays forward (`SortKnown`). The server's copy of the prediction is untouched: corrections still apply exactly.
- **Ground marks are two meshes.** The 2,900 pooled marks (tread marks, scorch, rubble, craters and wrecks) were a sprite renderer
  each: hiding them alone took this client from about 450 to 600 fps. `Decals` now writes each mark as a quad in one mesh for the
  `Effects` atlas and one for the wrecks (`Units` atlas) over it, same pools, holds, fades and sorting orders, uploading only the
  changed range once a frame.
- **Less work every frame elsewhere.** The fog texture is alpha only and uploaded at most 30 times a second (a battle kept cells
  fading, and the whole grid went up every frame: 1.5 ms of the render thread). The HUD's shown state refreshes at 30 Hz (input is
  still read every frame; with no own HQ it walked every known building each frame). Buildings are packed at 30 Hz with a quarter of
  the view as margin and drawn again in between, and their smoke, fire and jammer timers are checked then. Effects step at most
  144 times a second and mark fades 20 times.

Tried and dropped: Direct3D 11 (`-force-d3d11`: no faster), starting the next pack's prediction at the end of the frame before (on
three cores it slowed the render thread: 419 → 346 fps in a Development build), and a third queued frame
(`QualitySettings.maxQueuedFrames = 3`: 7 to 13 % faster, but a frame more input lag; not taken).

## Results on the owner's pinned layout (2026-11-08)

The owner ran the benchmark on the reference machine (Ryzen 5 5600GT, 6 cores and 12 threads, Radeon RX 6600, Linux) with each
process pinned to half of it: the host on CPUs 0 to 2 and their SMT siblings, the client on 3 to 5 and theirs. The build posted
first measured 65 to 68 fps mean and 30 to 33 fps 1 % low at about 100,000 units there, and the tick p95 missed 28 ms in every
run: far from the 164 fps this page first estimated with 8 E-cores for the client alone.

The same split is simulated here: the client on 3 E-cores (CPUs 6 to 8) and the host on 3 others (CPUs 2 to 4), each with 5 job
threads (`-job-worker-count 5`), as on 6 hardware threads. Three Skymont E-cores without SMT are close to three Zen 3 cores with it,
but this laptop still reads higher than the owner's machine: the posted build's client measured 94 to 105 fps here under the full
load against 65 to 68 there (Linux and the RX 6600 driver among the differences). Read the tables as `main` against this branch on the
same layout; the owner's numbers are about two thirds of these.

**Before** is `main` (`471bd0f1`) with the benchmark harness (including the every-tick fix) and the 12,500 cap cherry-picked onto
it; **after** is this branch. Single runs, interleaved (before, after, before, ...); medians in bold.

### The 100k benchmark (client and host each on 3 cores)

| Metric | Before (3 runs) | Before | After (3 runs) | After |
|---|---|---|---|---|
| Client fps (frames ÷ time) | 58.6 / 58.2 / 58.1 | **58.2** | 116.1 / 116.1 / 109.9 | **116.1** |
| Client 1 % low fps | 29.5 / 29.7 / 29.5 | **29.5** | 73.8 / 74.0 / 60.6 | **73.8** |
| Frame time p50 (ms) | 15.95 / 16.18 / 16.08 | **16.08** | 8.47 / 8.48 / 8.87 | **8.48** |
| Frame time p95 (ms) | 24.95 / 25.11 / 25.12 | **25.11** | 10.95 / 10.93 / 11.73 | **10.95** |
| Frame time p99 (ms) | 29.88 / 29.71 / 30.64 | **29.88** | 12.41 / 12.37 / 14.01 | **12.41** |
| Frame time max (ms) | 47.8 / 47.6 / 48.3 | **47.8** | 18.2 / 18.4 / 37.4 | **18.4** |
| CPU main thread, mean (ms) | 17.06 / 17.17 / 17.20 | **17.17** | 8.61 / 8.61 / 9.09 | **8.61** |
| GPU frame, mean (ms) | 1.28 / 1.28 / 1.52 | **1.28** | 0.74 / 0.72 / 0.94 | **0.74** |
| Client process CPU (% of a core) | 141 / 141 / 139 | **141** | 231 / 232 / 225 | **231** |
| Units the client knows | 99,379 / 99,433 / 99,479 | **99,433** | 100,228 / 100,227 / 99,930 | **100,227** |
| Units on screen (mean) | 12,943 / 15,778 / 12,916 | **12,943** | 13,977 / 13,827 / 13,696 | **13,827** |
| Bandwidth mean (KB/s) | 448 / 404 / 453 | **448** | 466 / 483 / 500 | **483** |
| Server tick p50 (ms) | 22.4 / 22.9 / 22.6 | **22.6** | 23.3 / 23.6 / 24.0 | **23.6** |
| Server tick p95 (ms) | 41.2 / 38.0 / 40.3 | **40.3** | 39.0 / 40.9 / 40.1 | **40.1** |
| Server tick p99 (ms) | 79.2 / 78.4 / 78.0 | **78.4** | 50.2 / 50.3 / 49.7 | **50.2** |
| Server tick max (ms) | 127.9 / 115.8 / 98.7 | **115.8** | 88.1 / 83.3 / 82.8 | **83.3** |
| Host ticks a frame, mean (1 = keeping up) | 5.45 / 5.17 / 5.29 | **5.29** | 1.11 / 1.08 / 2.06 | **1.11** |
| Boundary wait for the last tick's jobs, mean (ms) | 25.1 / 28.4 / 24.8 | **25.1** | 11.3 / 11.0 / 16.8 | **11.3** |
| Vision stage, worker time (ms) | 53.0 / 52.2 / 51.8 | **52.2** | 9.9 / 9.7 / 9.7 | **9.7** |
| Server logged errors, dropped connections | 0, 0 | **0, 0** | 0, 0 | **0, 0** |

### The gate (headless host on 3 cores, its 7 headless clients on 3 others, no rendering)

The `tools/perf-run.sh` form: normal interest, no shared vision.

| Metric | Before | After |
|---|---|---|
| Tick p50 (ms) | 14.5 / 14.9 (**14.7**) | 15.1 / 14.7 (**14.9**) |
| Tick p95 (ms) | 22.5 / 22.8 (**22.6**) | 24.6 / 21.8 (**23.2**) |
| Tick p99 (ms) | 69.9 / 69.6 (**69.8**) | 37.7 / 32.9 (**35.3**) |
| Tick max (ms) | 88.6 / 104.5 (**96.6**) | 51.5 / 54.8 (**53.1**) |
| Bandwidth mean (KB/s) | 216.0 / 222.1 (**219.0**) | 218.4 / 221.1 (**219.8**) |
| Bandwidth peak 1 s (KB/s) | 336.9 / 332.3 (**334.6**) | 368.2 / 334.0 (**351.1**) |
| Errors | 0 / 0 | 0 / 0 |

### Reading it

- **The client** runs at twice `main`'s frame rate (58 → 116 fps) with 2.5 times its 1 % low (30 → 74) on three cores. Neither
  reaches 140 here, and on the owner's machine, at about two thirds of these numbers, this branch should land near 75 to 80 fps
  mean and 40 to 45 fps 1 % low (`main` about 40 and 18). See "What is left".
- **The host now keeps up in the 100k benchmark.** With every owner sharing vision, the vision stage stamped each unit on all
  eight grids: 52 ms of worker time every fourth tick on three cores, longer than a tick, so `main`'s host ran about five ticks a
  frame to catch up and each tick waited 25 ms for the last one's jobs. Grids that take their sources from the same players now
  share one stamp (`SharedVision`): 9.7 ms, and the host runs about one tick a frame. This also helps any team game (one stamp per
  team instead of per player). `tick.main` itself, which leaves the wait out, is level at p95 (40.3 → 40.1 ms), 1 ms higher at p50
  (22.6 → 23.6) and lower at p99 and max (78 → 50, 116 → 83); both builds miss 28 ms at p95 on three cores, mostly replication to
  seven clients that each know all 100,000 units.
- **The gate** is level within the noise at p50 and p95 (14.7 → 14.9 ms, 22.6 → 23.2: this branch's two runs were 24.6 and 21.8)
  and halves p99 and max (69.8 → 35.3, 96.6 → 53.1): the order waves' formations run in Burst.
- **Bandwidth.** In the gate it is level (219.0 → 219.8 KB/s mean, budget 256); the 1 s peak was 368 and 334 against `main`'s 337
  and 332 (budget 768). In the 100k benchmark it is 8 % higher (448 → 483 KB/s); there `main`'s host, five ticks behind, sends
  less.
- **Orders keep `main`'s timing.** This branch first built a player's click's fields at once and sent routes while a field was
  still growing. The owner found that it slowed the game and let a client force any number of field builds a tick; on three
  cores it also meant 67 % more bandwidth at 100,000 units and a gate host waiting longer for its jobs. It is removed: every order
  waits for the per-tick field budget, as on `main`, and an order carries at most the player's share of the match's units in ids.

The CSV files here are from these runs: `100k-*` the 100k benchmark (`*-host.csv` the server, `*-client.csv` the measured client)
and `gate-*` the gate's first round. The client files' budget column is the 10k benchmark's 120 fps (`PerfClient.FpsBudget`);
this benchmark's goal is 140.

### The earlier estimate (2026-11-08, superseded)

The first version of this page estimated the owner's machine with 8 E-cores for each process and 11 job threads: a remote player
measured 58 → 164 fps mean and 33 → 102 fps 1 % low, a hoster 54 → 183 and 28 → 78, with the instant response since removed. The owner's pinned runs show that gave the
client far more CPU than half of a 5600GT, and in its hoster runs this branch's host fell behind (280 of 1,200 ticks sampled), so
the every-tick bug made that load lighter. Those numbers are replaced by the tables above.

## What is left (2026-11-09)

A Development-build profile on the owner's layout before the ground marks became meshes (419 fps, 2.14 ms a frame) split the frames
in two. The frames between unit packs took 1.6 ms: Unity's rendering 0.62 ms of the main thread, the wait for the last frame's present
0.32 ms, the HUD's per-frame input 0.06 ms and many small scripts. The pack frames, 60 a second, took 5.5 ms: the main thread's part of
the prediction and draw jobs and its wait for the rest 2.0 ms (about 3 ms of CPU each for the prediction and the draw job, over the
14,000 units in view and the turn of the others), the combat sprites 0.45 ms and the new tread marks 0.33 ms. The marks' 2,900 sprite
renderers were most of the rendering; as meshes they are gone from it.

The slowest 1 % of frames (112 fps) are still pack frames that meet a network batch (units leaving and corrections) or a known-list sort.
Spreading a batch's apply over several frames is the next step there. On the server: at 100,000 units with shared vision, `tick.main`
p95 is about 40 ms on three cores against the 28 ms budget, most of it replication to seven clients that each know every unit.

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
| EditMode | 1,536: 1,533 pass, 0 fail, 3 skipped (the hand-run map seeds, as on `main`) (2026-11-09) |
| PlayMode | 103: 103 pass (2026-11-09) |

A full PlayMode run here has failed one or two timing-dependent tests, a different set each run, and each passes run alone:
`ArsenalMatchTests.ArtilleryShellsATargetOnAScoutsSight`, `HudTests.GiftToEnemyMovesResources`, `BastionClientTests.ATurretShootingOurUnitTurnsItsHeadAndFiresATracer` and
`PerformanceTests.MinimapRedrawAtTenThousandOwnUnitsIsUnderOneMillisecond` (it waits 30 s for its 10,000 units). The
select-buildings branch (`main` with only the selection change and the same six test fixes) fails the same way in full runs (the artillery test, the
minimap test, `AiPlayModeTests.APrivateOneVsOneAgainstEasyBuildsProducesAndAttacksTheHuman`), so these come from `main`
(`known-issues.md`). `main` on Windows also fails six tests every run; this branch fixes them (commit `6364ef38`).

New tests: `SimLifecycleTests.TheUnitCapIsTheMatchsUnitsSplitBetweenItsPlayers`, `ASplitCapRefusesTheSpawnPastItsShare`, an AI
seat counted in the cap at match start (`AiSeatPlayModeTests.AnAiCountsInOrderTeamsIncomeUpkeepStatsHudAndWinLoss`),
`ReplicationTests.RoutesStayRightWhileTheArenaCompactsOnAJobThread` (400 orders through one store: the arena compacts on the job
threads while an untouched unit keeps its route and prediction), `HealthSetsOnlyKnownUnitsAndAMessageCutShortChangesNothing`,
and the Burst correction decoder checked against the managed one (and refusing a message cut short) in the message round trip.
After the owner's runs: `SimVisionTests.PlayersWhoShareBothWaysSeeTheSameAndBothGridsReportChanges` and
`SimBastionTests.PlayersWhoShareBothWaysAreJammedAlikeAndSeeTheSame` (a shared stamp gives both grids the same cells, fog changes
and jam).
For 500 fps: `ClientSmoothingTests.AUnitAwayFromTheViewWaitsForItsTurnAndOneInItIsAimedAtItsNextPosition` (a unit in the view is
refreshed and aimed at its next position; away from it, units wait for their turn and keep their positions) and
`TheKnownListGoesBackToIndexOrderAndEachUnitKeepsItsDrawnPosition` (9,000 units, a third leave, the list is sorted again and each
slot keeps its unit's position). `DrawJobTests` feeds the draw job slots left for a later turn, and
`PolishEffectsTests.ATrackMarkTurnsToTheExactHeading` reads the tread mark's quad in the marks' mesh.

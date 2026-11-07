# 10,000 units at 120 fps: benchmark and results (2026-11-07)

Goal: one match of 10,000 simultaneously active units, the client at 120 fps or more (on average and in the slowest 1 % of
frames), and a stable server. Branch `perf/10k-units-120fps`, on `release/v0.20`.

## The benchmark: `tools/perf-10k.sh`

`tools/perf-10k.sh [runs] [player]` runs the existing perf harness (`Dev/PerfMatch.cs`, `Dev/PerfClient.cs`, `tools/perf-run.sh`)
at a new scale:

- **Match.** Two owners × 5,000 units (10,140 with the siege load's Builders and Diggers) on the gate's 1024 map, seed 1: the
  v0.3 battle script on one front (a quarter of each army re-ordered every 2 s, one batch in four an Attack order), the siege
  load, the v0.13 defences, and reinforcements that keep each army at strength. The match recorder runs (`-perfRecord`).
- **Server.** The host, headless (`-batchmode -nographics`); its own army is one side.
- **Measured client.** One real player joined over KCP on loopback, rendering at **1920×1080 fullscreen** with the default
  settings, **VSync off and no frame cap**. Both sides share vision (`-perfShareVision`), so it knows all 10,000 units, not only
  the enemies its army sees. Every second it zooms out, as a player can (in wheel steps, never past the player's limit), until
  every unit it knows is on screen: about 9,300 of 10,135 are inside the view at the median.
- **Sampling.** The client warms up for 10 s after the match starts and then records every frame until the host ends the match
  (1,200 sampled ticks, 60 s, after the host's own warm-up). Frame time is `Time.unscaledDeltaTime`; CPU main thread, render
  thread and GPU time come from `FrameTimingManager` (Frame Timing Stats is now on in the player settings). The frame rate is
  frames over time (fixed 2026-11-07: it was the mean of each frame's own rate, which reads 2 to 3 % high here; the table's
  values come from `frames.csv`, so they are frames over time; the committed CSVs' `perf.client.fps` rows are the old mean). The 1 % low is the
  frame rate over the slowest 1 % of frames (their mean frame time). `client1/frames.csv` keeps every frame with the GC count.
- **Server metrics.** `perf.tick.main` (main-thread tick, the boundary's wait excluded), CPU of the process, Unity memory
  (native and managed reserved: Mono reports no working set on Windows) each second in `memory.csv`, its growth over the
  window, logged errors and exceptions, dropped connections, and the command queue's length each tick.

Other tools: `-perfBreakdown` on the client (bytes per message type), `-perfProfile <file.raw>` (Unity Profiler data from a
Development build: `WAR-2D/Build/Windows (Dev)`), `-perfSampleTicks` (a soak).

### Budgets

| Metric | Budget | Source |
|---|---|---|
| Client fps, mean | ≥ 120 | this goal (the roadmap's client budget is ≥ 60 fps with 10,000 own units on screen) |
| Client 1 % low | ≥ 120 | this goal ("sustain") |
| Server `tick.main` p95 | ≤ 25 ms | roadmap §3 (set at 80,000 units) |
| Bandwidth per client | ≤ 256 KB/s mean, ≤ 768 KB/s peak 1 s | roadmap §3 |
| Server logged errors | 0 | proposed here |
| Dropped connections | 0 | proposed here |
| Server memory growth | ≤ 64 MB over the sampled minute (10,000-unit runs only) | proposed here |

### Machine

Not the roadmap's reference machine (Ryzen 5 5600GT, Radeon RX 6600, Linux), which isn't available here. Measured on:
Intel Core Ultra 9 275HX (24 cores), NVIDIA GeForce RTX 5070 Ti Laptop GPU (Direct3D 12, driver 32.0.16.1692; the display is on
the Intel iGPU, Optimus), 32 GB, Windows 11 Home 26200, on AC power, Balanced plan, with other programs running (other Claude
sessions, Discord). Unity 6000.4.1f1, Mono, Windows release player (`-buildWindows64Player`).

The client is CPU-bound (GPU 0.5 ms a frame), so the reference machine's slower CPU matters more than its GPU. The client's
main thread is now 2.3 ms a frame and its slowest 1 % 4.7 ms on average: about 3.5 times headroom against 8.3 ms here. Re-run
`tools/perf-10k.sh 3` on the reference machine (Linux build) before relying on 120 fps there.

## Results

Interleaved single runs (before, after, before, ...) so both builds see the same machine load. **Before** is `release/v0.20`
plus the benchmark harness (commits `50ecf7f9`, `ead08668`); **after** is the branch's head. Medians in bold.

| Metric | Before (3 runs) | Before | After (3 runs) | After |
|---|---|---|---|---|
| Client fps (frames ÷ time) | 162.0 / 176.4 / 177.5 | **176.4** | 430.6 / 416.2 / 427.4 | **427.4** |
| Client 1 % low fps | 69.7 / 102.3 / 97.1 | **97.1** | 218.7 / 201.2 / 213.2 | **213.2** |
| Frame time p50 (ms) | 5.9 / 5.7 / 5.7 | **5.7** | 2.3 / 2.3 / 2.3 | **2.3** |
| Frame time p95 (ms) | 9.0 / 7.0 / 7.0 | **7.0** | 3.0 / 3.3 / 3.1 | **3.1** |
| Frame time p99 (ms) | 12.0 / 8.4 / 8.7 | **8.7** | 4.0 / 4.2 / 4.0 | **4.0** |
| Frame time max (ms) | 30.8 / 14.3 / 13.4 | **14.3** | 10.4 / 10.6 / 9.1 | **10.4** |
| CPU main thread (ms) | 6.2 / 5.7 / 5.6 | **5.7** | 2.3 / 2.4 / 2.3 | **2.3** |
| CPU render thread (ms) | 0.3 / 0.3 / 0.3 | **0.3** | 0.2 / 0.2 / 0.2 | **0.2** |
| GPU frame (ms) | 0.5 / 0.5 / 0.5 | **0.5** | 0.5 / 0.5 / 0.5 | **0.5** |
| Client process CPU (% of a core) | 276 / 279 / 289 | **279** | 501 / 478 / 495 | **495** |
| Client Unity memory (MB) | 426 / 402 / 399 | **402** | 404 / 418 / 397 | **404** |
| Client managed heap (MB) | 42.7 / 43.4 / 41.6 | **42.7** | 43.9 / 48.5 / 43.5 | **43.9** |
| Units the client knows | 10,133 / 10,136 / 10,135 | **10,135** | 10,133 / 10,135 / 10,136 | **10,135** |
| Units on screen (p50) | 9,631 / 9,599 / 9,241 | **9,599** | 9,298 / 9,069 / 9,328 | **9,298** |
| Bandwidth mean (KB/s) | 385 / 251 / 377 | **377** | 63 / 60 / 62 | **62** |
| Bandwidth peak 1 s (KB/s) | 842 / 555 / 681 | **681** | 92 / 97 / 91 | **92** |
| Server units | 10,111 | **10,111** | 10,111 | **10,111** |
| Server tick p50 (ms) | 2.9 / 2.6 / 2.8 | **2.8** | 3.3 / 3.1 / 3.1 | **3.1** |
| Server tick p95 (ms) | 8.4 / 6.4 / 7.3 | **7.3** | 8.2 / 8.3 / 6.7 | **8.2** |
| Server tick p99 (ms) | 12.4 / 10.9 / 11.1 | **11.1** | 12.5 / 12.8 / 12.1 | **12.5** |
| Server process CPU (% of a core) | 59 / 65 / 64 | **64** | 66 / 71 / 64 | **66** |
| Server Unity memory (MB) | 556 / 572 / 572 | **572** | 560 / 576 / 567 | **567** |
| Server memory growth in the minute (MB) | 32 / 48 / 48 | **48** | 32 / 48 / 40 | **40** |
| Server logged errors | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |
| Dropped connections | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |
| Command queue, longest | 830 / 776 / 860 | **830** | 788 / 806 / 762 | **788** |

Every budget passes after (`tools/perf-10k.sh` exits 0); before, the client's 1 % low and the bandwidth fail. Run-to-run
noise on this machine is large: an hour earlier, with less else running, the same before build measured 182 fps and an
earlier after build 440. Compare runs taken side by side only.

The server's tick rose 0.9 ms at p95 (encoding the route copies, `Messages.RouteCopies`, for each client and the recorder);
it is a third of the 25 ms budget.

### The 80,000-unit gate (the existing §4.1 gate, headless host, 7 real clients)

| Metric | Before | After |
|---|---|---|
| `tick.main` p95 (budget 25 ms) | 13.9 ms | 13.9 ms |
| `tick.replication` p95 | 7.0 ms | 6.8 ms |
| Bandwidth mean (budget 256 KB/s) | 230 KB/s | 210 KB/s |
| Bandwidth peak 1 s (budget 768 KB/s) | 421 KB/s | 334 KB/s |
| Errors, dropped connections | not measured | 0, 0 |

Three runs each (`WAR_PERF_HEADLESS_HOST=1 tools/perf-run.sh 3 <player> 7`), the before ones on the before build; medians.
Files: `80k-before-run*.csv`, `80k-after-run*.csv`.

### Soak: ten minutes at 10,000 units

`WAR_PERF_HOST_ARGS="-perfSampleTicks 12000" tools/perf-10k.sh 1` on the after build: every budget passes. The server's Unity
memory is 526 MB at its first second and 574 MB from the end of the first minute to the tenth (`10k-soak-memory.csv`: 574.1 MB
flat, slope 0.0 MB a minute over the second half); its managed heap moves between 45 and 57 MB with collections. Tick p95
7.0 ms (one tick of 12,000 reached 25.1 ms); process CPU 60 % of a core on average, 162 % at most; no errors, no dropped
connections; the command queue, sampled each tick, was empty at p95 and peaked at 761, as in the one-minute runs (the battle
script's order batches), so it doesn't grow. The client
held 413 fps and 169 in its slowest 1 % over the ten minutes; bandwidth 45 KB/s mean, 103 KB/s peak.

## What was slow (profiler evidence)

1. **Presentation stalls (the benchmark, not the game).** The first windowed runs had 3 to 6 frames of ~500 ms each, which alone
   set the 1 % low. Unity Profiler (Development build): every one was `GfxDeviceD3D12.WaitForLastPresent / DXGI.WaitOnSwapChain`,
   with the game's own work at its usual 5 ms; GPU time 0.5 ms. A window behind another one on this Optimus laptop. The game's
   default is fullscreen, which the benchmark now uses: no frame over 30 ms since.
2. **The client's per-unit draw loop.** Main thread 8.2 ms a frame (Development build), of which `UnitCommander.LateUpdate`
   (`ClientWorld.Draw`'s managed loop over 10,000 units: facing, frame, colour, hit feedback, track marks, turret) took 4.5 ms.
   Now a Burst job (`PrepareUnitsJob`): 4.5 → about 1 ms.
3. **Bandwidth: routes.** `-perfBreakdown` on the client: MoveOrder was 25.3 of 25.9 MB of unit messages in 55 s (97 %), about
   4,600 routes a second at 99 bytes; corrections 0.6 MB. Units of one order share their corridor, so routes now copy runs of the
   earlier routes in their message (protocol 22): 377 → 62 KB/s.
4. **The slowest frames: batch frames.** With markers (`ClientUnitStore.DecodeMoveOrder`, `ApplyRoutes`, `ApplyOther`), the
   frames over 7 ms spent 3.8 ms (up to 6) applying routes and 0.9 ms decoding them: one batch can carry 2,500 routes when the
   battle script re-orders both armies. Now a Burst job (`ApplyRoutesJob`); without it, the branch's 1 % low was 104 to 153 fps
   (two of three runs under 120), with it 201 to 219.

GC: incremental GC is off in this project; a collection takes 8 to 12 ms every 12 to 15 s at 10,000 units. Two or three of the
slowest 1 % of frames in a minute; not the bottleneck.

## Tests

EditMode and PlayMode through the editor in batch mode (`Unity -runTests`), against the same suites on `release/v0.20`
(worktree at `5ed41044`):

| Suite | release/v0.20 | Branch |
|---|---|---|
| EditMode | 1,482: 1,475 pass, 4 fail, 3 skipped | 1,489: 1,482 pass, the same 4 fail, the same 3 skipped |
| PlayMode | 100: 98 pass, 2 fail | 100: 98 pass, the same 2 fail (in an earlier full run the flaky minimap test also failed; alone it passed twice on the branch and once on `release/v0.20`) |

The failures are the same on both and already known (`known-issues.md`, the eighth scan): `ManualTests.TheCompiledWordsAreTheApprovedFile`,
`ReplayCursorTests.AdvancesByTickTimeAndRestarts`, `ReplayCursorTests.ReachesTheEndOfARecordingWithoutAnEndRecord`,
`UpnpProtocolTests.UsesUrlBaseWhenGiven`, `DebugApiPlayModeTests.ScreenshotWritesAPng`, `LobbyTests.MenuBattleRunsAndIsGoneOnceHostIsPressed`.
The 3 skipped are the same 3 on both. `PerformanceTests.MinimapRedrawAtTenThousandOwnUnitsIsUnderOneMillisecond` is flaky on
both: alone, it timed out once on `release/v0.20` and once of twice on the branch (its 30 s wait for 9,000 known units).

New tests: `ReplicationTests.MoveOrderRoutesCopyTheRunsTheyShareAndDecodeExactly`, `MoveOrderMessagesStayInsideTheBytesAndWaypointsTheDecoderAllows`,
`CopyStepsThatReachOutsideTheirMessageAreRefused`, `RoutesLongerThanAnyServerSendsAreRefusedBeforeTheUnitIsKnown`,
`AUnitOrderedTwiceInOneMessageEndsOnTheSecondRoute`, `AMoveOrderPastTheRouteCapChangesNothing`;
`ReplayFormatTests.RefusesOtherFormats(4)`.

## Changes to checks (flagged)

- `ReplayFormatTests` pins format 5 (was 4) and refuses 4: the format bumped, as at every bump before.
- `PolishEffectsTests.AVehicleLaysAMarkEveryHalfTile` disposes its `WorldEffects` (its track state is native memory now). No
  assertion changed.
- The perf gate budgets the host's `perf.fps` only when the host renders: a headless host draws nothing (`perf-run.sh` already
  said its fps means nothing, and the v0.18 report counted its headless gate as a pass despite that row). A rendering host keeps
  the 60 fps budget.
- New budgets: errors 0 and dropped connections 0 (every PerfMatch run), memory growth ≤ 64 MB (10,000-unit runs). The 80k gate
  grew 98 MB in its minute, mostly the managed heap and lists reaching working size; it has no memory budget.
- The benchmark's client runs fullscreen (the game's default) after windowed runs stalled in the OS's presentation.

## v0.20 Onslaught: the §4.1 gate with fire, shells, leaps and aims (2026-11-07)

`tools/perf-run.sh 3` against the `release/v0.20` Linux build, 80,000 units, the roster with Bombers in it (`PerfMatch.MixedType`:
5 % each of Artillery, Snipers, Flamethrowers and Bombers). Run on the owner's machine, side by side with the v0.19 build
(`Builds/LinuxReview`, run with an empty config folder so its remembered lobby couldn't take two clients' seats). Files:
`gate-run1..3.csv` and `gate-v0.19-baseline.csv`.

| | v0.19 (1 run) | v0.20 (3 runs, median) | budget |
|---|---|---|---|
| tick.main p95 | 26.4 ms | **32.6 ms** (30.8, 32.6, 32.7) | 28 ms: **fails** |
| tick.main mean | 19.9 ms | 22.3 ms | |
| host fps mean | 18.7 | 22.8 | 60: fails on this machine for both |
| errors, drops | 0, 0 | 0, 0 | pass |
| bandwidth avg / peak 1 s | | 167 / 319 KB/s | pass |

**Fixed on the way** (each measured with one gate run):

- Blast alerts scanned every unit for every blast: 35 ms a tick (p95 62 ms). They now walk the hash cells each blast reaches: 0.2 ms.
- The combat news's status bits: a full diff for every client every tick (3 ms), then its list of every unit's bits built on the
  main thread (2.7 ms at 80,000 units). Now a client in sync gets only the tick's changes and the units it met or lost, and the
  list is built by a job running alongside the sends (0.1 ms). Landed and incoming shells are worked out once a tick.
- Bombers of one owner after the same unit share its army's centre (one scan a tick, not one per Bomber).
- **Incremental GC is on** (it was off). With it off, collections of the ~110 MB host heap (about 17 a minute) stalled a tick by
  20 to 60 ms: 37 slow ticks outside the order waves in one run, 8 with it on; tick p95 33.9 → 32.2 ms. The 10k client benchmark
  on this machine (1 run each): mean 196 / 198 fps, 1 % low 94.8 fps off, 98.8 fps on.

**What is left over 28 ms.** Against v0.19, the main thread's mean is up 2.4 ms: the combat news 1.1 to 1.8 ms (about 90 µs of
managed dictionary work per client), the wait for the route upkeep +0.6 ms and the sends +0.4 ms (the workers are busier: the
combat jobs +0.7 to 1.4 ms, vision +0.7 to 1.0 ms with the Scout's sight of 21), match work +0.35 ms. Two things set the p95:

1. **The bots' order waves.** About 2.3 % of ticks carry a wave of orders from the harness's bots (`cmd.orders` 45 to 72 ms; those
   ticks take 63 to 101 ms). They were in v0.19's run too. Without them v0.20's p95 is about 28.4 ms.
2. **The rest of the tail**: ticks where the periodic work (radars, interest rebuilds, alarms, auto-work) lands with the heavier
   replication, 29 to 44 ms.

Not the Bombers: with the v0.19 roster (`WAR_PERF_NO_BOMBERS=1`) the p95 was 32.3 ms. Removing the combat news entirely would
take it to about 31 ms (from the per-tick numbers). Passing 28 ms needs either a cheaper tail (the status pass as a Burst job per
client, the route and send waits) or a decision on the budget or on the order waves in the harness: the owner's call.

New gate diagnostics: `perf.split.repl.combat` and `perf.split.repl.combatStatus`; `WAR_PERF_TICKS=1` writes every tick's numbers
to `ticks.csv` beside `perf.csv` (to see what is slow together on the slow ticks); `WAR_PERF_NO_BOMBERS=1` deals the v0.19 roster.

**The 10k client benchmark on this machine** (`tools/perf-10k.sh 1`, v0.20): mean 198 fps, 1 % low 98.8 fps (budget 120:
fails), server tick p95 13.6 ms. PR 45 measured 201 to 219 fps 1 % low on its machine; this machine wasn't measured on the tree
before v0.20, so how much of the gap is v0.20's combat drawing and how much the machine isn't known.

## v0.20 Onslaught: `ai-bench --brief` (2026-11-07)

90 matches (6 seeds × 256/512/1024 × five pairings), the build's own config; the full report is [ai-bench.md](ai-bench.md).

| | v0.20 | last bench (2026-11-03, 240 matches) |
|---|---|---|
| Hard beats Normal | **83 %** (15/18; 256 6/6, 512 4/6, 1024 5/6) | 71 % |
| Normal beats Easy | **94 %** (17/18; 256 6/6, 512 6/6, 1024 5/6) | 83 % |
| matches at the 40-minute cap | 19 of 90 (21 %), 0 errors | 70 of 240 (29 %) |

The exit criteria (every match ends; the stronger side wins at least 80 % on every map size) are **not met**: Hard-Normal on 512
is 4/6, and 19 matches capped (8 of them Hard-Hard). They were not met on 2026-11-03 either; the ladder is better than then.
Banks are far larger than on 2026-11-03 (Hard 1.57 M at 10 minutes, 6,210/s income) from the owner's `Balance` commit on
`main` (mining 10 → 25, rich gems ×2.5 → ×15, passive 5 → 20), not from v0.20, which changed no economy number.

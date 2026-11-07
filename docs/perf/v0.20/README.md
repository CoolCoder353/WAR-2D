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
`tools/perf-10k.sh 3` on the reference machine (Linux build) before relying on 120 fps there. Until then,
[the simulated reference machine](#simulated-reference-machine-limited-cpu-and-gpu) below estimates it.

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

### Simulated reference machine (limited CPU and GPU)

An estimate, not the reference machine: the after build on this laptop with the game held to fewer, slower cores. The
275HX's E-cores (Skymont, up to 4.6 GHz) are close to a Zen 3 core of the 5600GT (4.6 GHz) per thread; six E-cores without
SMT have fewer hardware threads than its 6 cores and 12 threads, eight about its throughput. Unity counts all 24 cores
whatever the affinity, so it starts 23 job threads in each process; `-job-worker-count 11` gives the 11 it starts on 12
threads. The Intel iGPU (`-force-device-index 1`) is much slower than an RX 6600; the RTX at 0.5 ms a frame is faster (an
RX 6600 has about half its graphics throughput, so about 1 ms). How it was run, from `cmd` (the processes inherit the
affinity mask):

```
start "" /affinity FC /wait /b bash -c "WAR_PERF_CLIENT_ARGS='-job-worker-count 11' WAR_PERF_HOST_ARGS='-job-worker-count 11' tools/perf-10k.sh 3 Builds/opt/WAR-2D.exe"
```

`FC` is E-cores 2 to 7 and `3FC` E-cores 2 to 9 (`GetLogicalProcessorInformationEx`); `WAR_PERF_CLIENT_ARGS` is new in
`perf-run.sh`. "Alone" ran the server on E-cores 14 to 21 (affinity set on each process after it started), as for a player
who joins rather than hosts. Three runs each:

| Setup (server on the same cores unless "alone") | Client fps | Client 1 % low | Main thread (ms) | GPU (ms) | Tick p95 (ms) |
|---|---|---|---|---|---|
| 6 E-cores, iGPU, 23 job threads | 215.7 / 220.4 / 210.0 | 126.6 / 110.8 / 111.4 | 4.6 / 4.5 / 4.8 | 4.3 / 4.2 / 4.4 | 11.1 / 10.7 / 11.2 |
| 6 E-cores, RTX, 23 job threads | 269.4 / 260.1 / 273.4 | 101.4 / 94.3 / 96.4 | 3.7 / 3.8 / 3.7 | 0.5 | 12.3 / 13.3 / 13.3 |
| 6 E-cores, RTX, 11 job threads | 306.6 / 319.2 / 318.5 | 141.8 / 136.2 / 109.3 | 3.3 / 3.1 / 3.1 | 0.5 | 11.4 / 10.6 / 10.3 |
| Client alone on 6 E-cores, RTX, 11 | 206.5 / 206.4 / 210.6 | 119.2 / 122.3 / 130.2 | 4.8 / 4.8 / 4.7 | 4.0 | 9.3 / 10.4 / 9.3 |
| 8 E-cores, RTX, 11 job threads | 316.3 / 331.6 / 336.4 | 117.7 / 127.4 / 131.2 | 3.2 / 3.0 / 3.0 | 0.5 | 9.1 / 9.0 / 9.7 |

Every run: the server's budgets pass (no errors, no dropped connections, growth 32 to 56 MB), bandwidth 60 to 77 KB/s mean.
In the "alone" runs the RTX took 4.0 ms a frame: the laptop shares one power budget between the CPU and the GPU, and with
14 cores busy it gave the GPU less.

**Result.** The mean frame rate holds 1.7 to 2.8 times the 120 fps budget on every setup. The 1 % low doesn't hold
reliably: the medians pass on the three setups with Unity's thread count for the target (122 to 136), but single runs fall to
109 to 119. A Development build profiled on 6 E-cores (`-perfProfile`) shows no single cause in its 31 frames over 7 ms of 2,000:
the swap chain waits (4 to 9 ms), the unit draw's main-thread work stretches from 1.0 ms to 2 to 5 ms when the main thread
loses its core, and one collection (6.4 ms). The largest main-thread item is the unit draw (1.0 ms a frame):
`PrepareUnitsJob` and the ring job ran on the main thread (`.Run()`). On the reference machine the 1 % low is likely near
the budget; confirm it there.

**The draw job over the job threads (branch `perf/parallel-draw-job`).** `PrepareUnitsJob` is an `IJobParallelFor` now, with
`GatherUnitsJob` putting its lists in unit order (the same output, `DrawJobTests`). Six interleaved pairs on 6 E-cores with 11
job threads, server on the same cores (before = this branch's base, after = the parallel job):

| Pair | Before fps | After fps | Before 1 % low | After 1 % low | Before main (ms) | After main (ms) |
|---|---|---|---|---|---|---|
| 1 | 201.7 | 256.9 | 16.8 | 47.9 | 4.94 | 3.88 |
| 2 | 280.1 | 310.6 | 114.2 | 120.3 | 3.56 | 3.21 |
| 3 | 224.6 | 255.0 | 95.5 | 117.7 | 4.45 | 3.92 |
| 4 | 282.3 | 267.5 | 130.8 | 114.6 | 3.54 | 3.73 |
| 5 | 266.9 | 313.0 | 113.4 | 104.5 | 3.74 | 3.19 |
| 6 | 332.6 | 354.9 | 100.9 | 106.8 | 3.00 | 2.81 |

Pairs 1 to 3 ran a build without the emit bits' per-frame clear (about 10 KB); pair 1 ran while the machine was very busy
(tick p95 25 ms). The mean frame rate rose in 5 of 6 pairs and the main thread fell 0.2 to 1.1 ms in 5 of 6; the 1 % low
rose in 4 of 6, within the noise: its slow frames are the cores' contention, which this doesn't remove. The client uses more
CPU (240 to 262 % of a core against 193 to 230 %). Server: no errors, no dropped connections, bandwidth 58 to 84 KB/s on both.

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

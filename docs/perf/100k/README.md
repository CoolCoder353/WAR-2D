# 100,000 units: benchmark and results (2026-11-07)

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

## Results

Interleaved single runs (before, after, before, ...) so both builds see the same machine load. **Before** is `main` (`1f1fe53c`,
v0.20) plus the benchmark harness and the 12,500 cap (commit `7eb5d07e`); **after** is the branch before the waiting-stub fix (`5ef68b47`), so its bandwidth is that
fix's "before" (755 KB/s; level with `main` after it, see the simulated machine below). Medians in bold.

| Metric | Before (3 runs) | Before | After (3 runs) | After |
|---|---|---|---|---|
| Client fps (frames ÷ time) | 75.1 / 75.4 / 76.0 | **75.4** | 180.3 / 181.6 / 185.7 | **181.6** |
| Client 1 % low fps | 37.2 / 36.1 / 26.6 | **36.1** | 80.4 / 83.2 / 90.1 | **83.2** |
| Frame time p50 (ms) | 12.52 / 12.45 / 12.32 | **12.45** | 5.20 / 5.18 / 5.10 | **5.18** |
| Frame time p95 (ms) | 20.23 / 20.03 / 19.73 | **20.03** | 8.34 / 8.32 / 8.05 | **8.32** |
| Frame time p99 (ms) | 24.71 / 24.39 / 23.53 | **24.39** | 10.77 / 10.43 / 9.91 | **10.43** |
| Frame time max (ms) | 33.2 / 42.8 / 508.6 | **42.8** | 20.5 / 39.0 / 15.1 | **20.5** |
| CPU main thread, mean (ms) | 13.31 / 13.25 / 13.16 | **13.25** | 5.54 / 5.50 / 5.38 | **5.50** |
| GPU frame, mean (ms) | 1.36 / 1.22 / 1.42 | **1.36** | 0.51 / 0.50 / 0.50 | **0.50** |
| Client process CPU (% of a core) | 199 / 215 / 197 | **199** | 656 / 675 / 702 | **675** |
| Client Unity memory (MB) | 471 / 479 / 496 | **479** | 477 / 478 / 488 | **478** |
| Units the client knows | 99,355 / 99,560 / 99,434 | **99,434** | 99,716 / 99,805 / 99,747 | **99,747** |
| Units on screen (mean) | 12,989 / 13,043 / 13,353 | **13,043** | 13,185 / 11,854 / 11,924 | **11,924** |
| Bandwidth mean (KB/s) | 399 / 410 / 417 | **410** | 810 / 755 / 708 | **755** |
| Server tick p50 (ms) | 12.1 / 12.0 / 12.2 | **12.1** | 16.4 / 16.1 / 15.7 | **16.1** |
| Server tick p95 (ms) | 18.7 / 18.3 / 19.0 | **18.7** | 25.7 / 26.1 / 25.2 | **25.7** |
| Server tick p99 (ms) | 72.8 / 73.8 / 72.6 | **72.8** | 48.1 / 52.2 / 45.1 | **48.1** |
| Server tick max (ms) | 95.5 / 84.6 / 81.7 | **84.6** | 66.4 / 62.9 / 60.9 | **62.9** |
| Server logged errors | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |
| Dropped connections | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |

**Against the goal.** The client's mean frame rate is 2.4 times `main`'s (75 → 182 fps) and passes 140. Its 1 % low is 2.3
times `main`'s (36 → 83 fps) but **misses 140**: frame time p50 5.2 ms, p99 10.4 ms. What is left is in "What was slow" below
and in `known-issues.md`.

**The server.** No errors or dropped connections. `tick.main` p99 fell from 73 to 48 ms and its max from 85 to 63 ms (the order
waves: formations in Burst). Its p50 rose from 12.1 to 16.1 ms and its p95 from 18.7 to 25.7 ms, inside the 28 ms budget: this
branch builds an order's fields at once and sends its routes as soon as the field exists (they waited for spare time), and the
client, now at 180 fps instead of 75, draws more work from the shared machine; the two were not measured apart.

**Bandwidth** rose from 410 to 755 KB/s in this load, where every owner shares vision, so the player is sent all 100,000 units
(its budget, 256 KB/s, is for the gate's normal interest). In the gate's own load (the `tools/perf-run.sh` form: headless host, 7 headless clients with normal interest, no shared vision; one run each, `gate-before-host.csv`, `gate-after-host.csv`): bandwidth 219 → 246 KB/s mean (budget 256) and 311 → 410 KB/s peak 1 s (budget 768); `tick.main` p95 12.9 → 12.5 ms and p99 68 → 32 ms; no errors.

Run-to-run noise on this machine is large: in an earlier interleaved set, one build of this branch (before its last two commits)
measured 127 to 173 fps mean (GPU time 0.55 or 0.73 ms a frame from run to run, the same build). Compare runs taken side by
side only.

## Simulated owner's machine (8 E-cores, 11 job threads)

An estimate of the reference machine (Ryzen 5 5600GT, 6 cores and 12 threads, Radeon RX 6600), not the machine itself. Every
process (host and clients, as when the owner hosts and plays) is held to 8 of the laptop's E-cores (`cmd /c start "" /affinity 3FC`,
CPUs 2 to 9): a Skymont E-core at up to 4.6 GHz is close to a Zen 3 core per thread, and 8 of them (no SMT) are close to the 5600GT's
throughput (Cinebench R23: about 1,450 single and 11,600 multi for the 5600G family). Unity starts 23 job threads whatever the
affinity, so host and clients get `-job-worker-count 11`, as on 12 hardware threads (`WAR_PERF_HOST_ARGS`, `WAR_PERF_CLIENT_ARGS`).
The GPU is the laptop's RTX at 0.5 ms a frame; an RX 6600 has about half its throughput (Time Spy graphics about 8,060), so about
1 ms: not the limit at these frame rates. Before is `main` with the harness (`Builds/base`), after this branch; interleaved.

### 100k benchmark, uncapped (the benchmark's own setting)

| Metric | Before (3 runs) | Before | After (3 runs) | After |
|---|---|---|---|---|
| Client fps (frames ÷ time) | 55.4 / 59.6 / 60.5 | **59.6** | 192.4 / 175.2 / 177.6 | **177.6** |
| Client 1 % low fps | 24.1 / 31.9 / 32.7 | **31.9** | 87.2 / 78.5 / 78.1 | **78.5** |
| Frame time p50 (ms) | 17.00 / 15.90 / 15.70 | **15.90** | 4.97 / 5.55 / 5.42 | **5.42** |
| Frame time p95 (ms) | 25.79 / 24.15 / 23.95 | **24.15** | 6.78 / 7.56 / 7.40 | **7.40** |
| Frame time p99 (ms) | 33.30 / 28.27 / 27.78 | **28.27** | 9.45 / 10.81 / 10.86 | **10.81** |
| Frame time max (ms) | 75.1 / 41.8 / 37.6 | **41.8** | 23.4 / 18.8 / 38.8 | **23.4** |
| CPU main thread, mean (ms) | 18.05 / 16.76 / 16.52 | **16.76** | 5.19 / 5.70 / 5.62 | **5.62** |
| GPU frame, mean (ms) | 1.60 / 1.55 / 1.53 | **1.55** | 0.48 / 0.49 / 0.50 | **0.49** |
| Client process CPU (% of a core) | 153 / 164 / 159 | **159** | 417 / 404 / 411 | **411** |
| Client Unity memory (MB) | 477 / 486 / 482 | **482** | 488 / 485 / 488 | **488** |
| Units the client knows | 99,405 / 99,439 / 99,486 | **99,439** | 88,278 / 99,003 / 98,815 | **98,815** |
| Units on screen (mean) | 13,350 / 13,769 / 13,242 | **13,350** | 11,771 / 14,031 / 13,358 | **13,358** |
| Bandwidth mean (KB/s) | 484 / 525 / 546 | **525** | 434 / 533 / 576 | **533** |
| Server tick p50 (ms) | 16.1 / 15.9 / 16.1 | **16.1** | 21.7 / 27.1 / 25.5 | **25.5** |
| Server tick p95 (ms) | 30.3 / 31.9 / 30.4 | **30.4** | 42.6 / 45.8 / 47.9 | **45.8** |
| Server tick p99 (ms) | 79.2 / 78.5 / 77.9 | **78.5** | 70.1 / 61.6 / 56.1 | **61.6** |
| Server tick max (ms) | 118.6 / 92.2 / 90.1 | **92.2** | 83.9 / 78.7 / 70.3 | **78.7** |
| Server logged errors | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |
| Dropped connections | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |

### 100k benchmark at the game's default frame cap (144 fps, `-perfFpsCap 144`)

`main` stays under 60 fps here, so the cap only touches the branch.

| Metric | Before (3 runs) | Before | After (3 runs) | After |
|---|---|---|---|---|
| Client fps (frames ÷ time) | 55.9 / 54.8 / 55.9 | **55.9** | 137.6 / 139.7 / 139.5 | **139.5** |
| Client 1 % low fps | 30.2 / 30.9 / 30.8 | **30.8** | 55.8 / 77.3 / 72.7 | **72.7** |
| Frame time p50 (ms) | 17.02 / 17.43 / 17.10 | **17.10** | 6.95 / 6.95 / 6.95 | **6.95** |
| Frame time p95 (ms) | 25.79 / 26.09 / 25.80 | **25.80** | 8.66 / 8.12 / 8.06 | **8.12** |
| Frame time p99 (ms) | 30.57 / 29.90 / 29.44 | **29.90** | 13.70 / 11.23 / 11.65 | **11.65** |
| Frame time max (ms) | 42.0 / 38.8 / 43.0 | **42.0** | 33.1 / 19.1 / 18.9 | **19.1** |
| CPU main thread, mean (ms) | 17.89 / 18.25 / 17.87 | **17.89** | 6.30 / 6.18 / 6.31 | **6.30** |
| GPU frame, mean (ms) | 1.49 / 1.50 / 1.51 | **1.50** | 0.56 / 0.55 / 0.56 | **0.56** |
| Client process CPU (% of a core) | 162 / 155 / 161 | **161** | 354 / 346 / 351 | **351** |
| Client Unity memory (MB) | 485 / 489 / 485 | **485** | 481 / 483 / 488 | **483** |
| Units the client knows | 99,503 / 99,446 / 99,399 | **99,446** | 93,952 / 91,972 / 99,091 | **93,952** |
| Units on screen (mean) | 12,945 / 15,070 / 13,977 | **13,977** | 11,059 / 10,223 / 12,787 | **11,059** |
| Bandwidth mean (KB/s) | 523 / 515 / 477 | **515** | 486 / 579 / 549 | **549** |
| Server tick p50 (ms) | 16.4 / 16.7 / 16.1 | **16.4** | 22.2 / 22.6 / 24.2 | **22.6** |
| Server tick p95 (ms) | 30.2 / 31.2 / 29.8 | **30.2** | 42.7 / 44.7 / 43.6 | **43.6** |
| Server tick p99 (ms) | 79.8 / 81.3 / 80.3 | **80.3** | 91.2 / 67.0 / 73.9 | **73.9** |
| Server tick max (ms) | 124.4 / 103.5 / 121.1 | **121.1** | 108.6 / 115.2 / 86.0 | **108.6** |
| Server logged errors | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |
| Dropped connections | 0 / 0 / 0 | **0** | 0 / 0 / 0 | **0** |

### The gate (headless host, 7 headless clients, no rendering)

| Metric | Before | After |
|---|---|---|
| Tick p50 (ms) | 10.6 / 10.5 (**10.6**) | 10.1 / 10.2 (**10.1**) |
| Tick p95 (ms) | 18.4 / 16.3 (**17.4**) | 15.5 / 15.6 (**15.6**) |
| Tick p99 (ms) | 72.0 / 71.1 (**71.6**) | 45.6 / 43.9 (**44.8**) |
| Tick max (ms) | 91.7 / 80.4 (**86.0**) | 60.4 / 60.4 (**60.4**) |
| Bandwidth mean (KB/s) | 225.2 / 223.7 (**224.4**) | 244.0 / 243.1 (**243.5**) |
| Bandwidth peak 1 s (KB/s) | 326.2 / 346.6 (**336.4**) | 351.5 / 338.0 (**344.7**) |
| Errors | 0.0 / 0.0 (**0.0**) | 0.0 / 0.0 (**0.0**) |

**Reading it.** The server alone (the gate) is faster at every percentile. In the 100k benchmark its tick p50 and p95 rise: the
client, now at 2.5 to 3 times the frame rate, uses 3.5 to 4 of the 8 cores (`main`'s 1.6), and the separate server process gets
less of them; its p99 and max still fall. Bandwidth is level with `main` in the 100k benchmark (525 → 533 KB/s uncapped) and one
normal-interest client (72 → 71 KB/s), and 9 % higher in the gate (224 → 244 KB/s, budget 256): the branch accepts 64 orders a
player instead of 32 (clicks past 32 were refused silently), so more orders run.

## What was slow (profiler evidence)

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

EditMode and PlayMode through the editor in batch mode (`Unity -runTests`) on Windows:

| Suite | Branch (`--burst-force-sync-compilation`, after the waiting-stub fix and the split cap) |
|---|---|
| EditMode | 1,535: 1,532 pass, 0 fail, 3 skipped (as on `main`) |
| PlayMode | 103: 101 pass in the full run; the 2 that timed out (`BastionClientTests.ATurretShootingOurUnitTurnsItsHeadAndFiresATracer`, `PerformanceTests.MinimapRedrawAtTenThousandOwnUnitsIsUnderOneMillisecond`) pass run alone |

`main` on Windows fails 7 (the six this branch fixes, commit `6364ef38`, and the minimap test, which times out waiting for its 10,000 units: `main`'s client takes over 30 s to know them). `AiPlayModeTests.AnEasyAndANormalAiPlaceMineBuildProduceAndAttack`, which failed in full runs without synchronous Burst (`known-issues.md`), passes in full runs with it.

New tests: `SimLifecycleTests.TheUnitCapIsTheMatchsUnitsSplitBetweenItsPlayers`, `ASplitCapRefusesTheSpawnPastItsShare`, the pushed waiting unit in `ReplicationTests.AUnitWaitingForItsFieldGetsItsRouteAsSoonAsTheFieldReachesIt`, `ReplicationTests.RoutesStayRightWhileTheArenaCompactsOnAJobThread` (400 orders through one store: the arena compacts
on the job threads while an untouched unit keeps its route and prediction), `HealthSetsOnlyKnownUnitsAndAMessageCutShortChangesNothing`,
and the Burst correction decoder checked against the managed one (and refusing a message cut short) in the message round trip.

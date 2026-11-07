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
v0.20) plus the benchmark harness and the 12,500 cap (commit `7eb5d07e`); **after** is the branch's head. Medians in bold.

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

| Suite | `main` (measured on `feature/select-buildings-one-type`: `main` plus five selection tests) | Branch |
|---|---|---|
| EditMode | 1,532: 1,525 pass, 4 fail, 3 skipped | 1,533: 1,530 pass, 0 fail, 3 skipped |
| PlayMode | 103: 100 pass, 3 fail | 103: 102 pass, 1 fail |

`main`'s failures on Windows are the known ones this branch fixes (commit `6364ef38`: `ManualTests.TheCompiledWordsAreTheApprovedFile`,
the two `ReplayCursorTests`, `UpnpProtocolTests.UsesUrlBaseWhenGiven`, `DebugApiPlayModeTests.ScreenshotWritesAPng`,
`LobbyTests.MenuBattleRunsAndIsGoneOnceHostIsPressed`) and `PerformanceTests.MinimapRedrawAtTenThousandOwnUnitsIsUnderOneMillisecond`,
which timed out waiting for its 9,000 units (flaky, `known-issues.md`). The branch's one failure is
`AiPlayModeTests.AnEasyAndANormalAiPlaceMineBuildProduceAndAttack` ("eventually one of them attacks"), unstable in full runs since
2026-11-06 (3 of 22 full runs failed it; alone it passed 8 of 8): run alone afterwards, it passed (1 of 1).

New tests: `ReplicationTests.RoutesStayRightWhileTheArenaCompactsOnAJobThread` (400 orders through one store: the arena compacts
on the job threads while an untouched unit keeps its route and prediction), `HealthSetsOnlyKnownUnitsAndAMessageCutShortChangesNothing`,
and the Burst correction decoder checked against the managed one (and refusing a message cut short) in the message round trip.

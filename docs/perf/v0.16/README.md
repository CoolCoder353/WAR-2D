# v0.16 performance gate (Clarity)

## Re-run on a quiet machine (2026-11-03): PASS

Three runs of `WAR_PERF_HEADLESS_HOST=1 tools/perf-run.sh 3` against a Linux release build built from `release/v0.16` at 5a4d829 plus the working-tree performance fixes below and longer-lasting ground marks (client only). Same load as below (8 owners, ~80,000 units, siege load, turrets, Jammers, 7 KCP clients, recorder on, formation orders, auto-work). **The Unity editor was closed** (as in v0.13); 9 of 31 GB in use, KDE's file indexer still running.

| Run | tick.main p95 | tick.wait p95 | replication p95 | part.settled p95 | part.combat p95 | part.vision p95 | part.commands p95 | part.stages p95 |
|---|---|---|---|---|---|---|---|---|
| v0.13 (editor closed), [perf/v0.13](../v0.13/README.md) | 21.3 ms | 4.1 ms | 14.9 ms | 15.7 ms | 9.5 ms | 7.9 ms | – | – |
| v0.14 median (editor open), [perf/v0.14](../v0.14/README.md) | 27.5 ms | 28.1 ms | 17.7 ms | 19.2 ms | 12.4 ms | 11.1 ms | 4.4 ms | 5.9 ms |
| v0.16 quiet run 1 | 19.7 ms | 25.4 ms | 13.6 ms | 14.5 ms | 10.6 ms | 8.7 ms | 4.0 ms | 2.9 ms |
| v0.16 quiet run 2 | 19.0 ms | 28.3 ms | 13.9 ms | 14.6 ms | 10.6 ms | 8.8 ms | 2.4 ms | 2.9 ms |
| v0.16 quiet run 3 | 21.2 ms | 27.7 ms | 14.4 ms | 15.3 ms | 10.6 ms | 9.2 ms | 3.5 ms | 3.0 ms |

**Budget: `tick.main` p95 ≤ 25 ms: PASS** (19.7 ms median). Bandwidth ≤ 256 KB/s average (205 KB/s) and ≤ 768 KB/s peak (587 KB/s): PASS. `perf.fps` (52, "FAIL") is meaningless with a headless host, as in v0.13 (batchmode caps the loop at 60). Terrain replication is 0.9 ms p95, attack targets 0.1 ms.

What it says: the earlier runs' slowdown was the machine. `part.stages` is back to 3 ms (10 to 17 ms under memory pressure), and the tick is faster than v0.13's. Combat (10.6 ms) and vision (8.8 ms) are about 1 ms over v0.13, in line with the v0.14 additions. The boundary wait (`tick.wait`, ~27 ms) is the job chain running between ticks and does not count against the budget, but it is what the next tick would stall on if the job chain grew: combat and vision are the parts to watch. Files: `perf-quiet-run*.csv`.

**Performance fixes in this build (no gameplay change):**

- *Idle Builders count (client, 4 Hz):* each standing Builder was checked against every building the client knows (Builders × buildings); the v0.16 idle-Builders fix sent more Builders into that loop. The owner's blueprints and damaged buildings are now collected once per count (`Selection.CollectWorkSites`).
- *Auto-work (host, each boundary):* each idle Builder that looked scanned every building twice. The living blueprints and buildings under the repair share are collected once per boundary, only when a Builder looks (`SimCommandSystem.CollectWorkBuildings`).

**Not done: these would change how the game plays, so they are the owner's call.** None is needed for the gate now. They are the levers if the load grows:

- *Combat (10.6 ms), the biggest part:* `<TargetSearchSliceTicks>` 8 → 12 cuts the target search by about a third. Idle units would then take up to 0.6 s to notice an enemy (0.4 s now).
- *Vision (8.8 ms):* `<VisionIntervalTicks>` 4 → 6 recomputes fog at about 3 Hz instead of 5 Hz. The fog edge would trail moving units visibly more.
- *Replication (encode 4.4 ms, send 5.2 ms):* raising `<CorrectionThreshold>` (0.25 tiles) or `<OffscreenIntervalTicks>` (20) sends fewer corrections. On-screen units would snap more often when a prediction is corrected.
- *Formations:* a unit within `<SlotTiles>` (12) of its goal checks a clear line to its slot every tick. Checking every second tick, or lowering `SlotTiles`, would cost units a little precision at corners. It was not measured on its own (it sits inside the movement stage, which is part of the job chain), and is likely small.

## First run under memory pressure (2026-11-02)

Three runs of `WAR_PERF_HEADLESS_HOST=1 tools/perf-run.sh 3` on 2026-11-02 against a Linux release build (`Builds/Linux/WAR-2D.x86_64`, built that day from `release/v0.16` at 4057d2c). The load is the v0.13/v0.14 Bastion load (8 owners, ~80,000 units, siege load, turrets, Jammers, 7 KCP clients, recorder on), now with the harness's army orders sent **with formations** (`PerfMatch`) and idle Builders doing **auto-work**. It is also the first measurement of the 2026-11-01 terrain-replication change.

**The machine was starved of memory for the whole run**: 27 of 31 GB in use by other processes (several Java processes, a `clang++` build, containers) and swap full; the kernel's OOM killer had fired minutes earlier (it also killed the first attempt at this build's Burst compile). The Unity editor was open, as in v0.14. These numbers are not a fair reading of v0.16.

| Run | tick.main p95 | tick.wait p95 | replication p95 | part.settled p95 | part.combat p95 | part.vision p95 | part.commands p95 | part.stages p95 |
|---|---|---|---|---|---|---|---|---|
| v0.14 median (editor open), [perf/v0.14](../v0.14/README.md) | 27.5 ms | 28.1 ms | 17.7 ms | 19.2 ms | 12.4 ms | 11.1 ms | 4.4 ms | 5.9 ms |
| v0.16 run 1 | 52.7 ms | 103.6 ms | 26.8 ms | 28.3 ms | 32.4 ms | 28.6 ms | 3.3 ms | 9.6 ms |
| v0.16 run 2 | 37.0 ms | 41.9 ms | 24.3 ms | 25.0 ms | 16.4 ms | 16.4 ms | 3.9 ms | 15.7 ms |
| v0.16 run 3 | 39.0 ms | 35.0 ms | 20.9 ms | 21.2 ms | 13.2 ms | 15.3 ms | 3.5 ms | 16.7 ms |

**Budget: `tick.main` p95 ≤ 25 ms: FAIL** (39.0 ms median), client ≥ 60 fps: FAIL (headless clients stalled with the machine). Bandwidth ≤ 256 KB/s average (113 KB/s) and ≤ 768 KB/s peak (404 KB/s): PASS. The terrain split (`perf.split.repl.terrain`) is 0.6 ms p95.

What the numbers say:

- **The new boundary work is not the cost.** `part.commands` (where formation orders are slotted and idle Builders look for work) is 3.3 to 3.9 ms, against v0.14's 3.5 to 4.8 ms.
- **Everything else is slower at once**, the job chains v0.16 barely touched (combat, vision) as much as the main-thread parts, and the runs spread from 37 to 53 ms with a 104 ms boundary wait in run 1: the signature of a machine swapping, as in v0.14's first run.
- `part.stages` (main-thread scheduling of the Burst stages) rose from 5 to 8 ms to 10 to 17 ms. The movement stage gained the formation arrays and a clear-line check for units near their slot; this is the one part to look at if a clean re-run stays high (`-perfBreakdown`).

**To do:** re-run on a quiet machine (editor closed, nothing else holding memory) before calling any of this a regression. Files: `perf-scripted-run*.csv`.

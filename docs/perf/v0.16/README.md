# v0.16 performance gate (Clarity)

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

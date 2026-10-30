# v0.13 performance gate (Bastion)

`WAR_PERF_HEADLESS_HOST=1 tools/perf-run.sh 3` against a Linux release build (`Builds/Linux/WAR-2D.x86_64`, built 2026-10-23 from `release/v0.13-v0.14` with the v0.13 work), editor closed, 8 owners, ~80,000 units (the v0.12 spec roster), 7 real KCP clients, recorder on. **The gate passes: `perf.tick.main` p95 21.3 ms against 25 ms.**

## Siege load

The v0.7 siege load (walls, Radars, bombs, Builders, Diggers) plus, per owner (owner decision O4): **40 Gun turrets** in two rows 6 and 8 tiles in front of the army's start, 2 tiles apart across the front and facing the opposing army (the battle at the front's midpoint is inside their range), and **4 Jammers** (two on the front's flanks, two by the HQ). All are free and have 1e6 health, so the turrets fire at the battle for the whole run and the Jammers keep jamming (`Dev/PerfMatch.PlaceDefences`; the log shows 40 turrets and 4 Jammers for every owner). That is 320 turrets and 32 Jammers.

## Results

| Build | tick.main p95 | tick.wait p95 | replication p95 | part.combat p95 | part.vision p95 | bw avg / peak | record KB/s |
|---|---|---|---|---|---|---|---|
| v0.12 final (2026-10-18, windowed host), [perf/v0.12](../v0.12/README.md) | 23.8 ms (23.7 / 23.8 / 25.1) | 10.1 ms | 16.3 ms | not measured | not measured | 212 / 459 KB/s | 604 |
| **v0.13, + 320 turrets and 32 Jammers (headless host)** | **21.3 ms** (20.4 / 22.1 / 21.3) | 4.1 ms | 14.9 ms | 9.2 ms | 7.9 ms | 213 / 443 KB/s | 705 |

Budget: `tick.main` p95 ≤ 25 ms (PASS), bandwidth ≤ 256 KB/s average (PASS) and ≤ 768 KB/s peak (PASS). Files: `perf-bastion-headless-run*.csv`.

**`perf.part.combat` and `perf.part.vision`** (new in v0.13, `Sim/StageClock.cs`) are not main-thread parts like the other `perf.part.*` rows: they are the wall time of the combat and vision stages' job chains on the worker threads, from a stamp job just before each stage's first job to one after its last. They reach `tick.main` only through the boundary wait, which stays low (4.1 ms p95), so the turret jobs and the jam do not threaten the budget. Vision runs on one tick in four, so its p95 is a vision tick (its mean is 1.8 ms). The jam mask is rebuilt only when the set of jamming Jammers or the share-vision masks change, which in this load is once, when they are placed.

## Caveats

- **The host ran headless** (`-batchmode -nographics`, the new `WAR_PERF_HEADLESS_HOST=1` switch in `tools/perf-run.sh`). On 2026-10-23 a windowed player hung at start on this machine (after display detection, before graphics init, with any graphics API, the v0.12 dev build too), so the windowed gate could not run. The tick numbers are therefore not strictly comparable with v0.12's windowed host: the host no longer renders, which frees main-thread and worker time. **`perf.fps` (57, FAIL) is meaningless here**: batchmode caps the loop at 60 frames per second. Re-run windowed (`tools/perf-run.sh 3`) when the display works, to confirm the gate and the fps budget.
- The runs used the first form of the border-vision fix (every trace started from an open tile). The final form traces as before and retries only when a trace is blocked at a rock end tile, so it does no more work on open ground and slightly more next to rock; not re-measured.
- Recording rose from 604 to 705 KB/s (p95): the turrets' attack events reach the recorder (it sees everything).

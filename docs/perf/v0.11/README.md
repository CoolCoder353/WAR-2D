# v0.11 performance gate (cave maps)

`tools/perf-run.sh 3` against a Linux release build, 2026-10-17: 8 owners, ~80,000 units, 7 real KCP clients, recorder on, on the v0.11 region-graph layout (1024 map, `CaveDepth` 16). The Unity editor was open but **paused** (`kill -STOP`) for each gate: idle, it spun at ~2 cores (main, graphics and HID input threads) and took that from the gate. `perf-run1..3.csv` are the passing runs.

| Build | tick.main p95 | replication p95 | fps | bw avg / peak | record KB/s |
|---|---|---|---|---|---|
| Roomier maps, no cave edges (1 run, editor running) | 25.2 ms | 17.9 ms | 137 | 201 / 492 KB/s | 557 |
| Cave edges, v0.11 pathing fix (3 runs, editor running) | 34.7 ms | — | 86 | 433 / 1,850 KB/s | 1,420 |
| Cave edges, **old** pathing (3 runs, editor running) | 33.5 ms | 23.9 ms | 80 | 345 / 1,780 KB/s | 1,396 |
| + a rotation resends only a changed way ahead (3 runs, editor running) | 36.9 ms | 28.4 ms | 107 | 285 / 916 KB/s | 816 |
| + routes simplified within `RouteTolerance` 1.0 (3 runs, editor paused) | 31.0 ms | 23.2 ms | 115 | 240 / 516 KB/s | 559 |
| **+ traces in parallel (median of 3, editor paused)** | **24.9 ms** | 17.8 ms | 123 | 255 / 520 KB/s | 543 |

Budget: tick.main p95 ≤ 25 ms, fps ≥ 60, bandwidth ≤ 256 KB/s average and ≤ 768 KB/s peak. **Passes**, narrowly on tick time and average bandwidth.

**What the cave edges cost.** The old pathing scored the same as the fixed one, so it was the map. A virtual-client run with `-perfBreakdown` showed MoveOrder bytes for one client going from 2.1 MB (no cave edges) to 48 MB: 264,000 routes sent instead of 35,000, and three times the size. Two causes in `RouteMaintenance`:

1. The 40-tick rotation retraces every moving unit from the cell it is in, and the old test for "same route" compared that first step, so a unit that had only walked on was resent. On a winding cave route, nearly every rotation was. Now a rotation's route is resent only when the way ahead differs (the end of the old route), which halved MoveOrder bytes.
2. A cave route is a staircase of eight-way turns (~165 bytes). Routes are now simplified back from the goal, keeping a turn only where the straight line would stray more than `RouteTolerance` tiles from the traced path (72 bytes a route; corrections rose from 0.4 to 0.9 MB a minute, far less than the routes saved).

The tick time was route upkeep: a single-threaded job that every other replication step waited for (4.6 ms mean). The traces now run in parallel, with one job committing the changed routes (2.0 ms).

**Margin.** Without cave edges the same harness measured 25.2 ms with the editor running, so the remaining cost is the battle itself (the roomier layout keeps more units in sight of each other), not the caves. Several clients see ~300 KB/s on their own; the average passes because the far fronts' clients see less. MoveOrder is still most of the bytes, mostly the harness re-ordering a quarter of each army every 2 s. The next saving would be sharing a route's common tail between the units of one order, which changes the wire and replay formats.

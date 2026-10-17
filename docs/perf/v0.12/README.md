# v0.12 performance gate (Arsenal)

`tools/perf-run.sh 3` against a Linux release build, 2026-10-17 (an unattended run; the editor closed, 8 owners, ~80,000 units, 7 real KCP clients, recorder on). **The first runs failed the tick budget; after the fixes below the gate passes (2026-10-18).**

| Build | tick.main p95 | tick.wait p95 | replication p95 | fps | bw avg / peak | record KB/s |
|---|---|---|---|---|---|---|
| v0.11 code, same machine today (all Tanks) | 25.8 ms | 30.3 ms | 18.6 ms | 113 | 247 / 502 KB/s | 542 |
| v0.12 code, all-Tank armies (`WAR_PERF_TANKS_ONLY=1`) | 27.2 ms | 45.5 ms | 18.8 ms | 97 | 207 / 526 KB/s | 501 |
| **v0.12, the spec roster** (60 % Tanks, 15 % Scouts, 10 % Heavies, 5 % each Artillery, Snipers, Flamethrowers) | **29.9 ms** | 36.0 ms | 19.5 ms | 65 | 187 / 487 KB/s | 605 |
| **v0.12 spec roster, per-type search reach** (2026-10-18) | **29.7 ms** | 13.4 ms | 20.7 ms | 83 | 212 / 466 KB/s | 607 |
| **v0.12 spec roster, + Burst Radar scan and shared terrain read** (2026-10-18, final) | **23.8 ms** (23.7 / 23.8 / 25.1) | 10.1 ms | 16.3 ms | 99 | 212 / 459 KB/s | 604 |

Budget: tick.main p95 ≤ 25 ms, fps ≥ 60, bandwidth ≤ 256 KB/s average and ≤ 768 KB/s peak. All three pass in the final runs (the last row). Before the fixes **tick.main did not** (this machine also missed it on the v0.11 code, 25.8 ms, against the 24.9 ms recorded on 2026-10-17 earlier, so about 1 ms of the gap was the machine). `perf.tick.main` is the main-thread time of a tick without the boundary wait.

**Where the 4 ms went (first runs, before the fixes).** About 1.4 ms is the v0.12 code on an unchanged roster (the extra combat jobs to schedule, the fog lookup, armour lookups, larger job structs). About 2.7 ms more comes with the new roster. Making the splash job parallel (`IJobParallelForDefer`) changed nothing (29.9 ms before and after), so splash itself is not the cost. Not profiled: the likely suspects are main-thread work that scales with deaths and movement (`RemoveDead` structural changes with Flamethrower and Artillery kills; Scouts at speed 9 raise correction and route traffic), and scheduling the three new jobs each tick.

**Re-run with the search fix (2026-10-18).** `tools/perf-run.sh 3` against a fresh Linux release build, editor closed, same machine. Per run, tick.main p95 29.7 / 30.2 / 29.7 ms; tick.wait p95 10.4 / 16.1 / 13.4 ms; fps mean 88 / 83 / 78. The fix cut the boundary wait by about two thirds (36.0 → 13.4 ms) and raised fps (65 → 83), but **tick.main is unchanged and the gate still fails**: the remaining cost is main-thread work, not the jobs. Replication's share of the tick grew (18.6 ms on v0.11, 20.7 ms here), which leaves about 9 ms of other main-thread work against about 7 ms on v0.11. Next step: profile a mixed-roster run (`-perfBreakdown`, the Unity profiler on a development build) to split the main-thread tick; suspects are replication with more corrections (fast Scouts, 2×2 units) and `RemoveDead` structural changes. Files: `perf-mixed-searchfix-run*.csv`.

**Found after the first runs (2026-10-18):** `NearestEnemyJob` scanned `SearchCells` around every unit, and `SearchCells` comes from the longest range in the config. The Sniper's 16 raised it from 1 to 4 (cell size 5), so every Tank scanned 81 hash cells instead of 9, in both the Tank-only and the mixed runs, which fits the jump in `tick.wait`. Each unit now scans only its own type's reach (re-run above).

**Done (see the next section).** The mixed-roster run was profiled and the two spikes it found were fixed. Files of the first runs: `perf-mixed-run*.csv`, `perf-tanks-only-run*.csv`, `perf-v0.11-baseline-run*.csv`.

## Profiling the main-thread tick (2026-10-18)

`perf.csv` now splits `tick.main` (`SimTiming`, `PerfSplits`; written by `Dev/PerfMatch` as `perf.part.*` and `perf.split.*`). The parts: the boundary's `RemoveDead`, upkeep, order bookkeeping and `Settled` handlers, the command stage, and the Burst stages from gather to the end. Inside `Settled`: each part of `WorldStateManager.OnSettled` (`match.*`) and of replication (`repl.*`).

**Before the fixes** (spec roster, `perf-mixed-split-before-run1.csv` and the other runs that day), p95 in ms: `settled` 26.5 of `tick.main` 30.5; inside it replication 20.7 and the match handler 6.2. The remaining parts were small: commands 3.5 to 4.6, Burst stages about 4, `RemoveDead` 0.3, orders 0.5. Two spikes that run only on vision ticks (1 tick in 4) stacked on the same ticks and made the p95 tail:

| Spike | p95 before | p95 after | Cause | Fix |
|---|---|---|---|---|
| `match.radars` (`WorldStateManager.WatchRadars`) | 6.2 ms | 0.4 ms | `RadarService.EnemyInRange` scanned up to 81 hash cells (Radar sight 20) in managed code for each of the 40 Radars; around a Radar the cells are full of its owner's own army, so a scan rarely stopped early | One Burst `IJobParallelFor` over all Radars (`RadarService.EnemyInRangeJob`) |
| `repl.terrain` (`ReplicationService.SendTerrain`) | 7.8 ms | 2.6 ms | Each of the 9 clients (8 players plus the recorder) re-read the current cell of every tile changed since the match began (`TerrainCellOf` per tile per client) | The cells are read once per tick and shared (`TerrainInterest.Update(IReadOnlyList<TerrainCell>, ...)`) |

Rewriting `MarkUnpaidOwners` (every 20 ticks) into a Burst count by owner slot made no measurable difference; it stays because it is simpler and allocation-free.

**What pushed tick.main from 25.8 to 29.7 ms.** Neither spike is v0.12 code; both grow with the match. The v0.12 roster makes them worse: splash and Artillery damage walls, so more tiles change (`ChangedSinceStart` grows faster), and a mixed army packs more units around each Radar. A Tank-only v0.12 run had the same split shape with smaller numbers (`tick.main` 27.7, replication 19.2, match handler 5.8). Replication's steady parts are each 1.3 to 6 ms (routes 3.1, interest 2.2, encode 5.6, send 6.0 at p95) and are now most of the tick; they are where to look if the budget gets tight again.

**Final runs** (`perf-mixed-final-run*.csv`): `tick.main` p95 median 23.8 ms (run 3 at 25.1 ms is just over), `tick.wait` 10.1 ms, replication 16.3 ms, fps 99, bandwidth 212 KB/s average and 459 KB/s peak. The gate passes, with 1.2 ms of headroom.


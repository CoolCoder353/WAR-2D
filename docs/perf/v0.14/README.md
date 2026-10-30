# v0.14 performance gate (AI opponents)

Two runs of `tools/perf-run.sh` on 2026-10-24 against a Linux release build (`Builds/Linux/WAR-2D.x86_64`, built that day from `release/v0.13-v0.14` with the v0.14 work and the owner's uncommitted working-tree changes), headless host (`WAR_PERF_HEADLESS_HOST=1`, as in v0.13: a windowed player hangs on this machine). **The Unity editor was open during every run** (it was in use; 80 to 140 % CPU while idle, more while importing), unlike v0.13's runs with the editor closed.

## 1. The gate with Scripted seats (load unchanged)

`WAR_PERF_HEADLESS_HOST=1 tools/perf-run.sh 3`: the v0.13 Bastion load (8 owners, ~80,000 units, siege load, 320 Gun turrets, 32 Jammers), the 7 non-host owners Scripted AI seats (no brain), 7 real KCP clients, recorder on.

| Run | tick.main p95 | tick.wait p95 | replication p95 | part.settled p95 | part.combat p95 | part.vision p95 | part.ai p95 | bw avg / peak | record KB/s |
|---|---|---|---|---|---|---|---|---|---|
| v0.13 (editor closed), [perf/v0.13](../v0.13/README.md) | **21.3 ms** | 4.1 ms | 14.9 ms | 15.7 ms | 9.5 ms | 7.9 ms | – | 213 / 443 KB/s | 705 |
| **v0.14 (editor open)** | **27.5 ms** (28.7 / 27.5 / 25.0) | 28.1 ms | 17.7 ms | 19.2 ms | 12.4 ms | 11.1 ms | 0 | 213 / 596 KB/s | 721 |

**Budget: `tick.main` p95 ≤ 25 ms: FAIL** (27.5 ms median of three; the third run 25.0 ms). Bandwidth ≤ 256 KB/s average and ≤ 768 KB/s peak: PASS. Files: `perf-scripted-run*.csv`.

What it is not: the AI (Scripted seats have no brain; `perf.part.ai` is 0), and not the owner's `<Orders><MaxQueued>` change from 4 to 30 (a run with the host on a copy of the config with 4, `WAR_PERF_HOST_ARGS="-configFile …"`, gave 26.6 ms). Every part is 20 to 30 % slower than in v0.13 at once (the main-thread parts that don't depend on jobs, replication and `Settled` handlers, as much as the job chains), and the first run, started while the editor was still importing, was the slowest (36.8 ms in an earlier set): that points at the machine (the open editor competing for the cores) rather than at a code path. **To do:** re-run with the editor closed before calling it a regression; if it stays over 25 ms, profile the tick (`-perfBreakdown`, the `perf.split.*` rows).

## 2. Seven Hard AIs on the 1024 map

`WAR_PERF_AI=Hard WAR_PERF_HEADLESS_HOST=1 tools/perf-run.sh 3` (new: `Dev/PerfMatch.cs` `-perfAi`): seven lobby AI seats, Hard, Random styles, play a real Free-for-all on the gate's 1024 map (seed 1) with the host sitting out; the match is fast-forwarded 8× through 12 minutes of play, then 1,200 ticks are sampled at normal speed. No clients join (the AIs make the load).

| Run | tick.main p95 | tick.main max | part.ai p95 | part.ai max | units (p95) |
|---|---|---|---|---|---|
| 1 | 4.1 ms | 9.2 ms | 0.66 ms | 1.13 ms | 595 |
| 2 | 2.6 ms | 8.6 ms | 0.63 ms | 0.96 ms | 365 |
| 3 | 3.8 ms | 10.0 ms | 0.67 ms | 1.02 ms | 465 |

**Budgets: `perf.part.ai` p95 ≤ 2 ms: PASS (0.66 ms); `tick.main` p95 ≤ 25 ms: PASS (3.8 ms).** One AI thinks per tick (round robin), so `part.ai` is one view build plus one brain think. Files: `perf-7hard-run*.csv`.

Caveat: twelve minutes into a 7-AI 1024 match the AIs have a few hundred units between them, far from the 80,000 of the Scripted gate; the AI's cost grows with what it sees (its view build and the army and threat scans), so a late, crowded AI match would cost more. The AI bench's 40-minute 1v1 games reach 300 to 700 units per AI; a long 8-player AI match was not measured.

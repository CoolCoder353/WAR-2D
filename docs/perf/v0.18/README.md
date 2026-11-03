# v0.18 Briefing: AI bench and performance

## AI bench (2026-11-03)

`tools/ai-bench.sh --brief 16` on the v0.18 build, the same 16 seeds as v0.17's run ([v0.17 report](../v0.17/README.md)). The only balance change is the Suicide bomber (last gasp at 10 % health, aiming for the middle). The AI's own use of bombers is unchanged. Full report: `PerfResults/ai-bench-20261103-192405/report.md` (not in the repository).

| Pairing | 256 | 512 | 1024 |
|---|---|---|---|
| Hard beats Normal, v0.17 → v0.18 | 88 % → 94 % | 62 % → 69 % | 31 % → 50 % |
| Normal beats Easy, v0.17 → v0.18 | 100 % → 100 % | 81 % → 94 % | 44 % → 56 % |

The ladder holds and no pairing got worse, so nothing was tuned. With 16 games a row, a difference of a game or two is noise. The bench's own exit criteria still say "not met" because long 1024 games reach the 40-minute cap: 70 of 240 games capped, the same issue v0.17 reported. One match failed with "Address already in use", the bench's known port clash, not a game fault.

## Performance gate: PASS, narrowly (2026-11-03)

`WAR_PERF_HEADLESS_HOST=1 tools/perf-run.sh 3`, with one batch of orders in four sent as Attack orders, bombers in the roster and the recorder's team fog on. Files: `perf-run*.csv`.

| Build | tick.main p95 (runs) | median | wait | replication | settled | combat | vision | commands | stages |
|---|---|---|---|---|---|---|---|---|---|
| v0.18 | 23.2 / 24.6 / 24.5 | **24.5 ms (PASS)** | 32.7 | 14.1 | 15.1 | 10.6 | 9.2 | 3.1 | 3.6 |
| v0.17 (quiet) | 22.6 / 18.3 / 23.8 | 22.6 ms | 31.3 | 13.6 | 14.4 | 10.3 | 9.2 | 4.6 | 2.9 |

**Budget: `tick.main` p95 ≤ 25 ms: PASS.** Bandwidth is within budget. This run was not on a fully quiet machine (Firefox and KDE's file indexer were running, which v0.17's quiet run had stopped), and the v0.18 run shows only 0.5 to 0.7 ms more in replication, settled and stages, so most of the gap may be the machine. It leaves little margin: re-run on a quiet machine before release. An earlier run with the editor still using two cores measured 28.2 ms and doesn't count.

**Not measured:** client frame time with 10,000 own units selected and Alt held (spec section 11); `tick.main` above covers the server only.

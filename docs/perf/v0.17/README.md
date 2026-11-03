# v0.17 Heartland: AI bench and performance

## AI bench (2026-11-03)

`tools/ai-bench.sh --brief 16` (16 seeds, the same in both runs), first on a v0.16 build (`release/v0.16` at 4a2ec85, before any v0.17 change), then on v0.17 (rich gems, the AI's gem-worth scoring). Full reports: [ai-bench-v016-baseline.md](ai-bench-v016-baseline.md), [ai-bench-v017.md](ai-bench-v017.md).

| Pairing | 256 | 512 | 1024 |
|---|---|---|---|
| Hard beats Normal, v0.16 → v0.17 | 81 % → 88 % | 69 % → 62 % | 31 % → 31 % (caps 9 → 7) |
| Normal beats Easy, v0.16 → v0.17 | 100 % → 100 % | 94 % → 81 % | 81 % → 44 % (caps 3 → 9) |

**Accepted by the owner on 2026-11-03, not tuned.** What it says:

- **On 1024 the rich middle makes games longer, not shorter** (spec §5 asked to note it). Easy won no 1024 game against Normal: of 16, Normal won 7 and 9 reached the 40-minute cap with Normal far ahead (armies of 300 to 830 against Easy's 16 to 107, mostly a Normal Turtle). The extra income builds bigger armies that don't finish in time. The win-rate drop is caps, not losses.
- **Hard against Normal on 1024 was already broken in v0.16** (31 %, 9 caps) before any v0.17 change; v0.17 doesn't change it.
- With 16 games a row, differences of a game or two (94 → 81 %) are within the noise.

If finishing matters, the levers are the AI's expansion and attack timing (`<Ai><Play><ExpansionDistanceScale>`, `MaxedAttackResources`, the Turtle style's attack thresholds), tuned with `node tools/ai-tune.js` per [tuning.md](../../tuning.md).

## Performance gate (2026-11-03, not yet passed)

`WAR_PERF_HEADLESS_HOST=1 tools/perf-run.sh 3`, editor closed, the v0.17 player built at db452cb; then the same on the v0.16 player (4a2ec85) straight after, to separate the machine from the code. The machine was busier than for v0.16's morning gate (Firefox open and busy, KDE's file indexer running): v0.16 itself read 24.9 ms against 19.7 ms that morning.

| Build | tick.main p95 (runs) | median | wait | replication | settled | combat | vision | commands | stages |
|---|---|---|---|---|---|---|---|---|---|
| v0.16 (morning, quiet) | 19.7 / 19.0 / 21.2 | 19.7 ms | 27.7 | 13.9 | 14.6 | 10.6 | 8.8 | 3.5 | 2.9 |
| v0.16 (same hour as v0.17) | 22.9 / 24.9 / 25.2 | **24.9 ms (PASS)** | 33.7 | 14.7 | 15.5 | 10.7 | 10.0 | 4.5 | 3.2 |
| v0.17 | 26.0 / 27.1 / 26.6 | **26.6 ms (FAIL)** | 33.8 | 15.4 | 16.5 | 10.5 | 10.2 | 4.9 | 3.6 |

Bandwidth passes (v0.17: 210 KB/s average, 672 KB/s peak).

What it says: most of the gap to the morning is the machine; **v0.17 itself costs about 1.7 ms**, spread over the boundary (`part.commands` +0.4, `part.settled` +1.0, replication +0.7). No v0.17 code runs per tick at that scale (the perf load has no AI; the sim gained a rim check in the dig stage), so the likely cause is the map: every seed's map changed, so the harness's armies meet elsewhere on the gate's 1024 map. To do: re-run both builds on a quiet machine (Firefox closed); if v0.17 still fails, profile with `-perfBreakdown` and compare where the harness's fronts land. Files: `perf-v017-run*.csv`, `perf-v016-same-day-run*.csv`.

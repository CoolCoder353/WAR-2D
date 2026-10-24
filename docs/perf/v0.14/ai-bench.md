# v0.14 AI bench

`tools/ai-bench.sh` (spec §12): 1v1 Free-for-all, AI vs AI, one headless player per match (`Dev/AiBench.cs`, `-perf -aiBench`), the host sitting out, fast-forwarded. Styles are drawn per seat from a seeded generator (the same pair for a seed in every pairing); the seat order alternates by seed. Exit criteria: every match ends (a draw at the time cap is a failure) and Hard beats Normal, Normal beats Easy, in ≥ 80% on every map size.

## Results

| Pairing | Map | Games | Stronger wins | Win rate | Draws / caps / errors | Length median / max (min) |
| --- | --- | --- | --- | --- | --- | --- |
| Hard-Normal | 256 | 6 | 4 | 67% | 0 / 0 / 0 | 4.1 / 10.8 |
| Hard-Normal | 512 | 6 | 3 | 50% | 0 / 0 / 0 | 6.5 / 26.9 |
| Hard-Normal | 1024 | 6 | 3 | 50% | 0 / 3 / 0 | 8.2 / 30.1 |
| Normal-Easy | 256 | 6 | 3 | 50% | 0 / 0 / 0 | 3.9 / 8.6 |
| Normal-Easy | 512 | 6 | 4 | 67% | 0 / 1 / 0 | 9.2 / 10.4 |
| Normal-Easy | 1024 | 6 | 4 | 67% | 0 / 1 / 0 | 8.8 / 22.2 |
| Hard-Hard | 256 | 6 | – | – | 0 / 0 / 0 | 5.7 / 8.8 |
| Hard-Hard | 512 | 6 | – | – | 0 / 3 / 0 | 19.6 / 38.1 |
| Hard-Hard | 1024 | 6 | – | – | 0 / 1 / 0 | 8.7 / 18.7 |
| Normal-Normal | 256 | 6 | – | – | 0 / 0 / 0 | 5.3 / 25.2 |
| Normal-Normal | 512 | 6 | – | – | 0 / 0 / 0 | 9.5 / 20.7 |
| Normal-Normal | 1024 | 6 | – | – | 0 / 2 / 0 | 10.4 / 17.3 |
| Easy-Easy | 256 | 6 | – | – | 0 / 0 / 0 | 7.6 / 11.3 |
| Easy-Easy | 512 | 6 | – | – | 0 / 1 / 0 | 11.7 / 17.9 |
| Easy-Easy | 1024 | 6 | – | – | 0 / 0 / 0 | 12.5 / 31.7 |

**Exit criteria: NOT met.**

## By style

Stronger side's wins by its own style and by the weaker side's style (all sizes):

| Pairing | Style | Stronger plays it: won / games | Weaker plays it: stronger won / games |
| --- | --- | --- | --- |
| Hard-Normal | Rusher | 6 / 7 | 5 / 6 |
| Hard-Normal | Turtle | 0 / 4 | 2 / 5 |
| Hard-Normal | Expander | 3 / 4 | 3 / 5 |
| Hard-Normal | Balanced | 1 / 3 | 0 / 2 |
| Normal-Easy | Rusher | 5 / 7 | 5 / 6 |
| Normal-Easy | Turtle | 0 / 4 | 1 / 5 |
| Normal-Easy | Expander | 4 / 4 | 4 / 5 |
| Normal-Easy | Balanced | 2 / 3 | 1 / 2 |

Mirror matches, wins by style (all sizes):

| Pairing | Rusher | Turtle | Expander | Balanced |
| --- | --- | --- | --- | --- |
| Hard-Hard | 5 / 13 | 1 / 9 | 5 / 9 | 3 / 5 |
| Normal-Normal | 6 / 13 | 4 / 9 | 5 / 9 | 1 / 5 |
| Easy-Easy | 5 / 13 | 3 / 9 | 6 / 9 | 3 / 5 |

## How they played

Means per seat over every match (all pairings and sizes): first attack, attacks, retreats, peak army, highest balance, unpaid upkeep seconds, concessions.

| Difficulty | Seats | First attack (min) | Attacks | Retreats | Peak army | Highest balance | Unpaid s | Conceded |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Hard | 54 | 5.2 | 2.2 | 1.1 | 157 | 5445 | 0 | 0 |
| Normal | 72 | 6.0 | 1.9 | 0.8 | 102 | 5796 | 1 | 0 |
| Easy | 54 | 6.4 | 1.2 | 0.0 | 53 | 4913 | 161 | 0 |

## Failures

- `1024_Hard-Hard_102.json`: cap, 40.0 min, Hard Expander army 361 buildings 126 vs Hard Turtle army 120 buildings 106
- `1024_Hard-Normal_101.json`: cap, 40.0 min, Hard Balanced army 291 buildings 148 vs Normal Balanced army 296 buildings 105
- `1024_Hard-Normal_102.json`: cap, 40.0 min, Normal Expander army 157 buildings 63 vs Hard Turtle army 122 buildings 108
- `1024_Hard-Normal_106.json`: cap, 40.0 min, Normal Turtle army 69 buildings 77 vs Hard Expander army 520 buildings 148
- `1024_Normal-Easy_102.json`: cap, 40.0 min, Easy Expander army 93 buildings 45 vs Normal Turtle army 80 buildings 94
- `1024_Normal-Normal_101.json`: cap, 40.0 min, Normal Balanced army 487 buildings 136 vs Normal Balanced army 160 buildings 62
- `1024_Normal-Normal_102.json`: cap, 40.0 min, Normal Expander army 125 buildings 55 vs Normal Turtle army 89 buildings 74
- `512_Easy-Easy_105.json`: cap, 39.9 min, Easy Rusher army 128 buildings 44 vs Easy Rusher army 94 buildings 59
- `512_Hard-Hard_101.json`: cap, 40.0 min, Hard Turtle army 308 buildings 212 vs Hard Turtle army 405 buildings 230
- `512_Hard-Hard_105.json`: cap, 40.0 min, Hard Rusher army 63 buildings 47 vs Hard Rusher army 259 buildings 99
- `512_Hard-Hard_106.json`: cap, 40.0 min, Hard Rusher army 264 buildings 84 vs Hard Rusher army 70 buildings 56
- `512_Normal-Easy_102.json`: cap, 40.0 min, Easy Turtle army 130 buildings 71 vs Normal Turtle army 136 buildings 78

## Upsets (the weaker side won)

- `1024_Normal-Easy_104.json`: Easy Turtle beat Normal Rusher in 13.1 min
- `256_Hard-Normal_104.json`: Normal Expander beat Hard Balanced in 10.8 min
- `256_Hard-Normal_106.json`: Normal Balanced beat Hard Turtle in 1.3 min
- `256_Normal-Easy_103.json`: Easy Turtle beat Normal Balanced in 8.6 min
- `256_Normal-Easy_105.json`: Easy Rusher beat Normal Rusher in 4.2 min
- `256_Normal-Easy_106.json`: Easy Balanced beat Normal Turtle in 8.0 min
- `512_Hard-Normal_101.json`: Normal Turtle beat Hard Turtle in 19.1 min
- `512_Hard-Normal_102.json`: Normal Turtle beat Hard Turtle in 26.9 min
- `512_Hard-Normal_105.json`: Normal Rusher beat Hard Rusher in 3.8 min
- `512_Normal-Easy_101.json`: Easy Turtle beat Normal Turtle in 10.4 min

## Tuning history (2026-10-24, plan Task 10; written by hand: re-running the bench rewrites this whole file, so carry this section over)

The run above is `tools/ai-bench.sh 6 7` on the final code and config (Linux release build of 2026-10-24 with the owner's working-tree changes, 32× fast-forward, 40-minute cap, 7 matches at once beside the editor). Earlier runs on the way, Hard-Normal and Normal-Easy only, stronger side's win rate at 256 / 512 / 1024 (caps in brackets):

| Run | Change | Hard-Normal | Normal-Easy |
| --- | --- | --- | --- |
| Baseline (4 seeds) | Task 7 as committed | 50 / 50 / 25 % (0 / 1 / 2) | 100 / 50 / 25 % (0 / 2 / 2) |
| 4 seeds | Attack staging, maxed-out attacks, counter-attacks from Defend, rich expansion | 75 / 50 / 25 % (0 / 0 / 2) | 100 / 50 / 0 % (0 / 1 / 1) |
| 6 seeds | Stuck blueprints cancelled | 33 / 50 / 17 % (1 / 3 / 3) | 67 / 67 / 67 % (1 / 1 / 1) |
| 6 seeds | Threats at expansions defended (the army no longer froze in Defend) | 67 / 17 / 17 % (0 / 1 / 3) | 67 / 67 / 0 % (0 / 1 / 5) |
| 6 seeds | Recalls only for a real threat; last-seen armies forgotten when reached empty; scouting visits start sites first | 50 / 50 / 33 % (0 / 2 / 2) | 100 / 67 / 83 % (0 / 1 / 1) |
| 6 seeds | Staging waits from the first arrival; a marching group's retreat check weighs the whole group | 83 / 33 / 33 % (0 / 3 / 1) | 83 / 100 / 50 % (0 / 0 / 3) |
| 6 seeds | Staging and gathering points on open ground (one had been on rock) | 50 / 33 / 50 % (0 / 1 / 1) | 100 / 100 / 67 % (0 / 0 / 1) |
| 8 seeds, Hard only | Hard: `IncomePerProducer` 120 (a producer only per 120 income it has: producers' upkeep was starving its army) | 88 / 75 / 62 % (0 / 1 / 3) | – |
| 8 seeds | + several scouts (by map size), early turrets | 25 / 25 / 75 % (1 / 3 / 1) | 88 / 100 / 88 % (0 / 0 / 1) |
| 8 seeds | + no retreat when maxed out | 62 / 50 / 38 % (0 / 3 / 3) | 88 / 100 / 75 % (0 / 0 / 2) |
| 8 seeds | + remembered enemy HQs (a Jammer hides a base it saw), scout stop timeouts, chasing a last-seen army times out | 38 / 38 / 38 % (0 / 2 / 5) | 88 / 88 / 75 % (0 / 0 / 2) |
| 8 seeds | + the army sweeps unseen start sites when it knows no target; chokepoint wall gap 2 → 6 | 38 / 50 / 88 % (1 / 3 / 0) | 75 / 62 / 75 % (0 / 2 / 0) |

**Reading it:** with 6 to 8 games a cell the noise is ±30 points (the same code and seeds gave Normal-Easy 88–100 % in one run and 50–67 % in the final one: the simulation's job timing makes runs differ), so no cell above is a reliable pass or fail. What the timelines and army logs (each result's `timeline` and `armyLog`) show consistently: the style matchup decides most short games (a Rusher's first push at 2.5 minutes kills a greedy HQ on 256 whatever the difficulty), Turtles lose most games and cause most caps (their walls and Jammers slow both sides; Hard Turtle won 0 of 4 against Normal), and Hard's edge shows in its economy (peak army 157 against Normal's 102) more than in its fights. Remaining caps are mostly 1024 and 512 games where one side has a large army but keeps losing it at the other's turret lines or never finds the base.

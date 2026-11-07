# v0.14 AI bench

`tools/ai-bench.sh` (spec §12): 1v1 Free-for-all, AI vs AI, one headless player per match (`Dev/AiBench.cs`, `-perf -aiBench`), the host sitting out, fast-forwarded. Styles are drawn per seat from a seeded generator (the same pair for a seed in every pairing); the seat order alternates by seed. Exit criteria: every match ends (a draw at the time cap is a failure) and Hard beats Normal, Normal beats Easy, in ≥ 80% on every map size.

Config: the build's own `GameConfig.xml`.

## Results

| Pairing | Map | Games | Stronger wins | Win rate | Draws / caps / errors | Length median / max (min) |
| --- | --- | --- | --- | --- | --- | --- |
| Hard-Normal | 256 | 6 | 6 | 100% | 0 / 0 / 0 | 3.1 / 6.7 |
| Hard-Normal | 512 | 6 | 4 | 67% | 0 / 2 / 0 | 8.4 / 28.7 |
| Hard-Normal | 1024 | 6 | 5 | 83% | 0 / 1 / 0 | 11.6 / 27.9 |
| Normal-Easy | 256 | 6 | 6 | 100% | 0 / 0 / 0 | 2.6 / 4.8 |
| Normal-Easy | 512 | 6 | 6 | 100% | 0 / 0 / 0 | 6.0 / 34.4 |
| Normal-Easy | 1024 | 6 | 5 | 83% | 0 / 0 / 0 | 16.5 / 28.9 |
| Hard-Hard | 256 | 6 | – | – | 0 / 1 / 0 | 3.9 / 5.1 |
| Hard-Hard | 512 | 6 | – | – | 0 / 3 / 0 | 8.2 / 13.0 |
| Hard-Hard | 1024 | 6 | – | – | 0 / 4 / 0 | 20.9 / 32.2 |
| Normal-Normal | 256 | 6 | – | – | 0 / 0 / 0 | 7.5 / 21.8 |
| Normal-Normal | 512 | 6 | – | – | 0 / 2 / 0 | 9.7 / 18.3 |
| Normal-Normal | 1024 | 6 | – | – | 0 / 3 / 0 | 9.0 / 23.9 |
| Easy-Easy | 256 | 6 | – | – | 0 / 0 / 0 | 6.7 / 17.7 |
| Easy-Easy | 512 | 6 | – | – | 0 / 0 / 0 | 16.3 / 21.1 |
| Easy-Easy | 1024 | 6 | – | – | 0 / 3 / 0 | 10.3 / 12.3 |

**Exit criteria: NOT met.**

## By style

Stronger side's wins by its own style and by the weaker side's style (all sizes):

| Pairing | Style | Stronger plays it: won / games | Weaker plays it: stronger won / games |
| --- | --- | --- | --- |
| Hard-Normal | Rusher | 7 / 7 | 5 / 6 |
| Hard-Normal | Turtle | 2 / 4 | 3 / 5 |
| Hard-Normal | Expander | 3 / 4 | 5 / 5 |
| Hard-Normal | Balanced | 3 / 3 | 2 / 2 |
| Normal-Easy | Rusher | 7 / 7 | 6 / 6 |
| Normal-Easy | Turtle | 4 / 4 | 5 / 5 |
| Normal-Easy | Expander | 3 / 4 | 4 / 5 |
| Normal-Easy | Balanced | 3 / 3 | 2 / 2 |

Mirror matches, wins by style (all sizes):

| Pairing | Rusher | Turtle | Expander | Balanced |
| --- | --- | --- | --- | --- |
| Hard-Hard | 5 / 13 | 2 / 9 | 2 / 9 | 1 / 5 |
| Normal-Normal | 7 / 13 | 3 / 9 | 1 / 9 | 2 / 5 |
| Easy-Easy | 5 / 13 | 2 / 9 | 6 / 9 | 2 / 5 |

## How they played

Means per seat over every match (all pairings and sizes): first attack, attacks, retreats, peak army, highest balance, unpaid upkeep seconds, concessions.

| Difficulty | Seats | First attack (min) | Attacks | Retreats | Peak army | Highest balance | Unpaid s | Conceded |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Hard | 54 | 5.5 | 1.6 | 0.3 | 840 | 9513623 | 1 | 0 |
| Normal | 72 | 5.6 | 1.6 | 0.4 | 404 | 3358086 | 0 | 0 |
| Easy | 54 | 6.2 | 1.7 | 0.5 | 230 | 269195 | 0 | 0 |

## Failures

- `1024_Easy-Easy_101.json`: cap, 40.0 min, Easy Balanced army 278 buildings 31 vs Easy Balanced army 913 buildings 70
- `1024_Easy-Easy_104.json`: cap, 40.0 min, Easy Turtle army 56 buildings 43 vs Easy Rusher army 351 buildings 48
- `1024_Easy-Easy_105.json`: cap, 40.0 min, Easy Expander army 240 buildings 30 vs Easy Rusher army 236 buildings 28
- `1024_Hard-Hard_101.json`: cap, 40.0 min, Hard Balanced army 408 buildings 176 vs Hard Balanced army 844 buildings 262
- `1024_Hard-Hard_102.json`: cap, 40.0 min, Hard Expander army 251 buildings 128 vs Hard Turtle army 1885 buildings 138
- `1024_Hard-Hard_103.json`: cap, 40.0 min, Hard Expander army 1217 buildings 266 vs Hard Expander army 181 buildings 154
- `1024_Hard-Hard_104.json`: cap, 40.0 min, Hard Turtle army 379 buildings 204 vs Hard Rusher army 1607 buildings 278
- `1024_Hard-Normal_105.json`: cap, 40.0 min, Hard Expander army 2336 buildings 105 vs Normal Rusher army 7 buildings 136
- `1024_Normal-Normal_101.json`: cap, 40.0 min, Normal Balanced army 385 buildings 58 vs Normal Balanced army 230 buildings 60
- `1024_Normal-Normal_103.json`: cap, 40.0 min, Normal Expander army 757 buildings 180 vs Normal Expander army 317 buildings 94
- `1024_Normal-Normal_106.json`: cap, 40.0 min, Normal Turtle army 844 buildings 82 vs Normal Expander army 248 buildings 29
- `256_Hard-Hard_102.json`: cap, 40.0 min, Hard Rusher army 388 buildings 142 vs Hard Rusher army 855 buildings 144
- `512_Hard-Hard_101.json`: cap, 40.0 min, Hard Turtle army 912 buildings 223 vs Hard Turtle army 158 buildings 71
- `512_Hard-Hard_102.json`: cap, 40.0 min, Hard Turtle army 715 buildings 217 vs Hard Turtle army 495 buildings 67
- `512_Hard-Hard_106.json`: cap, 40.0 min, Hard Rusher army 588 buildings 112 vs Hard Rusher army 634 buildings 104
- `512_Hard-Normal_101.json`: cap, 40.0 min, Hard Turtle army 1112 buildings 162 vs Normal Turtle army 303 buildings 78
- `512_Hard-Normal_102.json`: cap, 40.0 min, Normal Turtle army 216 buildings 87 vs Hard Turtle army 2619 buildings 191
- `512_Normal-Normal_102.json`: cap, 40.0 min, Normal Turtle army 707 buildings 182 vs Normal Turtle army 133 buildings 46
- `512_Normal-Normal_104.json`: cap, 40.0 min, Normal Expander army 232 buildings 85 vs Normal Rusher army 272 buildings 90

## Upsets (the weaker side won)

- `1024_Normal-Easy_103.json`: Easy Expander beat Normal Expander in 16.6 min

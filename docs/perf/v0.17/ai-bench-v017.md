# v0.14 AI bench

`tools/ai-bench.sh` (spec §12): 1v1 Free-for-all, AI vs AI, one headless player per match (`Dev/AiBench.cs`, `-perf -aiBench`), the host sitting out, fast-forwarded. Styles are drawn per seat from a seeded generator (the same pair for a seed in every pairing); the seat order alternates by seed. Exit criteria: every match ends (a draw at the time cap is a failure) and Hard beats Normal, Normal beats Easy, in ≥ 80% on every map size.

Config: the build's own `GameConfig.xml`.

## Results

| Pairing | Map | Games | Stronger wins | Win rate | Draws / caps / errors | Length median / max (min) |
| --- | --- | --- | --- | --- | --- | --- |
| Hard-Normal | 256 | 16 | 14 | 88% | 0 / 0 / 0 | 4.6 / 18.1 |
| Hard-Normal | 512 | 16 | 10 | 62% | 0 / 2 / 1 | 11.4 / 22.0 |
| Hard-Normal | 1024 | 16 | 5 | 31% | 0 / 7 / 0 | 9.9 / 31.8 |
| Normal-Easy | 256 | 16 | 16 | 100% | 0 / 0 / 0 | 4.2 / 17.6 |
| Normal-Easy | 512 | 16 | 13 | 81% | 0 / 2 / 1 | 10.6 / 31.6 |
| Normal-Easy | 1024 | 16 | 7 | 44% | 0 / 9 / 0 | 19.1 / 32.9 |
| Hard-Hard | 256 | 16 | – | – | 0 / 4 / 0 | 4.1 / 24.8 |
| Hard-Hard | 512 | 16 | – | – | 0 / 7 / 0 | 14.0 / 27.6 |
| Hard-Hard | 1024 | 16 | – | – | 0 / 10 / 0 | 13.8 / 21.2 |
| Normal-Normal | 256 | 16 | – | – | 0 / 1 / 0 | 6.1 / 35.4 |
| Normal-Normal | 512 | 16 | – | – | 0 / 4 / 1 | 12.5 / 33.3 |
| Normal-Normal | 1024 | 16 | – | – | 0 / 7 / 0 | 10.3 / 35.5 |
| Easy-Easy | 256 | 16 | – | – | 0 / 1 / 0 | 8.0 / 19.2 |
| Easy-Easy | 512 | 16 | – | – | 0 / 3 / 0 | 15.0 / 36.8 |
| Easy-Easy | 1024 | 16 | – | – | 0 / 3 / 0 | 11.9 / 25.5 |

**Exit criteria: NOT met.**

## By style

Stronger side's wins by its own style and by the weaker side's style (all sizes):

| Pairing | Style | Stronger plays it: won / games | Weaker plays it: stronger won / games |
| --- | --- | --- | --- |
| Hard-Normal | Rusher | 11 / 14 | 7 / 12 |
| Hard-Normal | Turtle | 5 / 10 | 8 / 13 |
| Hard-Normal | Expander | 6 / 13 | 9 / 12 |
| Hard-Normal | Balanced | 7 / 10 | 5 / 10 |
| Normal-Easy | Rusher | 13 / 14 | 10 / 12 |
| Normal-Easy | Turtle | 4 / 10 | 10 / 13 |
| Normal-Easy | Expander | 10 / 13 | 9 / 12 |
| Normal-Easy | Balanced | 9 / 10 | 7 / 10 |

Mirror matches, wins by style (all sizes):

| Pairing | Rusher | Turtle | Expander | Balanced |
| --- | --- | --- | --- | --- |
| Hard-Hard | 12 / 27 | 3 / 23 | 6 / 26 | 6 / 20 |
| Normal-Normal | 15 / 27 | 3 / 23 | 8 / 25 | 9 / 19 |
| Easy-Easy | 13 / 27 | 6 / 23 | 13 / 26 | 9 / 20 |

## How they played

Means per seat over every match (all pairings and sizes): first attack, attacks, retreats, peak army, highest balance, unpaid upkeep seconds, concessions.

| Difficulty | Seats | First attack (min) | Attacks | Retreats | Peak army | Highest balance | Unpaid s | Conceded |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Hard | 143 | 5.9 | 2.3 | 0.6 | 314 | 20958 | 1 | 0 |
| Normal | 188 | 5.9 | 2.3 | 0.6 | 207 | 25129 | 1 | 0 |
| Easy | 143 | 6.6 | 2.6 | 0.5 | 73 | 15649 | 1 | 0 |

## Failures

- `1024_Easy-Easy_107.json`: cap, 40.0 min, Easy Expander army 42 buildings 45 vs Easy Turtle army 43 buildings 42
- `1024_Easy-Easy_108.json`: cap, 40.0 min, Easy Balanced army 15 buildings 49 vs Easy Turtle army 219 buildings 108
- `1024_Easy-Easy_111.json`: cap, 39.9 min, Easy Turtle army 160 buildings 92 vs Easy Turtle army 64 buildings 50
- `1024_Hard-Hard_102.json`: cap, 40.0 min, Hard Expander army 453 buildings 126 vs Hard Turtle army 351 buildings 110
- `1024_Hard-Hard_103.json`: cap, 40.0 min, Hard Expander army 205 buildings 120 vs Hard Expander army 664 buildings 213
- `1024_Hard-Hard_104.json`: cap, 40.0 min, Hard Turtle army 418 buildings 159 vs Hard Rusher army 497 buildings 143
- `1024_Hard-Hard_105.json`: cap, 40.0 min, Hard Expander army 423 buildings 122 vs Hard Rusher army 323 buildings 174
- `1024_Hard-Hard_106.json`: cap, 40.0 min, Hard Turtle army 303 buildings 150 vs Hard Expander army 21 buildings 124
- `1024_Hard-Hard_107.json`: cap, 40.0 min, Hard Expander army 217 buildings 148 vs Hard Turtle army 569 buildings 109
- `1024_Hard-Hard_110.json`: cap, 40.0 min, Hard Turtle army 187 buildings 192 vs Hard Balanced army 353 buildings 134
- `1024_Hard-Hard_113.json`: cap, 40.0 min, Hard Turtle army 436 buildings 172 vs Hard Expander army 251 buildings 195
- `1024_Hard-Hard_114.json`: cap, 40.0 min, Hard Rusher army 161 buildings 90 vs Hard Rusher army 683 buildings 130
- `1024_Hard-Hard_115.json`: cap, 40.0 min, Hard Balanced army 579 buildings 201 vs Hard Expander army 221 buildings 98
- `1024_Hard-Normal_103.json`: cap, 40.0 min, Hard Expander army 309 buildings 119 vs Normal Expander army 360 buildings 136
- `1024_Hard-Normal_106.json`: cap, 40.0 min, Normal Turtle army 150 buildings 77 vs Hard Expander army 949 buildings 157
- `1024_Hard-Normal_108.json`: cap, 40.0 min, Normal Balanced army 65 buildings 61 vs Hard Turtle army 540 buildings 189
- `1024_Hard-Normal_110.json`: cap, 40.0 min, Normal Turtle army 292 buildings 198 vs Hard Balanced army 764 buildings 180
- `1024_Hard-Normal_113.json`: cap, 40.0 min, Hard Turtle army 459 buildings 170 vs Normal Expander army 468 buildings 134
- `1024_Hard-Normal_115.json`: cap, 40.0 min, Hard Balanced army 948 buildings 252 vs Normal Expander army 185 buildings 95
- `1024_Hard-Normal_116.json`: cap, 40.0 min, Normal Turtle army 226 buildings 125 vs Hard Balanced army 59 buildings 70
- `1024_Normal-Easy_102.json`: cap, 40.0 min, Easy Expander army 106 buildings 75 vs Normal Turtle army 117 buildings 79
- `1024_Normal-Easy_103.json`: cap, 39.9 min, Normal Expander army 312 buildings 103 vs Easy Expander army 68 buildings 41
- `1024_Normal-Easy_106.json`: cap, 40.0 min, Easy Turtle army 54 buildings 48 vs Normal Expander army 189 buildings 115
- `1024_Normal-Easy_108.json`: cap, 40.0 min, Easy Balanced army 107 buildings 54 vs Normal Turtle army 303 buildings 88
- `1024_Normal-Easy_111.json`: cap, 40.0 min, Normal Turtle army 460 buildings 92 vs Easy Turtle army 21 buildings 37
- `1024_Normal-Easy_112.json`: cap, 40.0 min, Easy Rusher army 63 buildings 47 vs Normal Turtle army 832 buildings 158
- `1024_Normal-Easy_113.json`: cap, 40.0 min, Normal Turtle army 467 buildings 118 vs Easy Expander army 16 buildings 42
- `1024_Normal-Easy_114.json`: cap, 39.9 min, Easy Rusher army 61 buildings 49 vs Normal Rusher army 383 buildings 88
- `1024_Normal-Easy_116.json`: cap, 40.0 min, Easy Turtle army 28 buildings 73 vs Normal Balanced army 383 buildings 99
- `1024_Normal-Normal_102.json`: cap, 40.0 min, Normal Expander army 142 buildings 63 vs Normal Turtle army 374 buildings 95
- `1024_Normal-Normal_104.json`: cap, 40.0 min, Normal Turtle army 375 buildings 156 vs Normal Rusher army 84 buildings 45
- `1024_Normal-Normal_106.json`: cap, 39.9 min, Normal Turtle army 301 buildings 108 vs Normal Expander army 330 buildings 111
- `1024_Normal-Normal_109.json`: cap, 40.0 min, Normal Rusher army 604 buildings 159 vs Normal Turtle army 217 buildings 79
- `1024_Normal-Normal_110.json`: cap, 40.0 min, Normal Turtle army 337 buildings 86 vs Normal Balanced army 207 buildings 146
- `1024_Normal-Normal_111.json`: cap, 40.0 min, Normal Turtle army 517 buildings 103 vs Normal Turtle army 223 buildings 89
- `1024_Normal-Normal_113.json`: cap, 40.0 min, Normal Turtle army 100 buildings 165 vs Normal Expander army 430 buildings 151
- `256_Easy-Easy_103.json`: cap, 40.0 min, Easy Balanced army 28 buildings 30 vs Easy Turtle army 22 buildings 32
- `256_Hard-Hard_106.json`: cap, 39.9 min, Hard Balanced army 88 buildings 118 vs Hard Turtle army 229 buildings 240
- `256_Hard-Hard_108.json`: cap, 40.0 min, Hard Rusher army 272 buildings 167 vs Hard Balanced army 287 buildings 205
- `256_Hard-Hard_110.json`: cap, 40.0 min, Hard Balanced army 149 buildings 157 vs Hard Expander army 401 buildings 181
- `256_Hard-Hard_111.json`: cap, 40.0 min, Hard Expander army 214 buildings 185 vs Hard Balanced army 251 buildings 182
- `256_Normal-Normal_108.json`: cap, 40.0 min, Normal Rusher army 295 buildings 181 vs Normal Balanced army 113 buildings 118
- `512_Easy-Easy_102.json`: cap, 40.0 min, Easy Turtle army 68 buildings 34 vs Easy Turtle army 50 buildings 34
- `512_Easy-Easy_108.json`: cap, 40.0 min, Easy Expander army 160 buildings 66 vs Easy Expander army 45 buildings 36
- `512_Easy-Easy_110.json`: cap, 39.9 min, Easy Balanced army 201 buildings 70 vs Easy Turtle army 96 buildings 92
- `512_Hard-Hard_101.json`: cap, 40.0 min, Hard Turtle army 138 buildings 102 vs Hard Turtle army 707 buildings 173
- `512_Hard-Hard_102.json`: cap, 40.0 min, Hard Turtle army 669 buildings 145 vs Hard Turtle army 152 buildings 87
- `512_Hard-Hard_104.json`: cap, 40.0 min, Hard Expander army 732 buildings 170 vs Hard Rusher army 145 buildings 111
- `512_Hard-Hard_107.json`: cap, 40.0 min, Hard Expander army 90 buildings 112 vs Hard Rusher army 451 buildings 198
- `512_Hard-Hard_108.json`: cap, 40.0 min, Hard Expander army 932 buildings 164 vs Hard Expander army 20 buildings 45
- `512_Hard-Hard_109.json`: cap, 40.0 min, Hard Balanced army 873 buildings 198 vs Hard Balanced army 51 buildings 91
- `512_Hard-Hard_111.json`: cap, 40.0 min, Hard Expander army 611 buildings 166 vs Hard Balanced army 139 buildings 75
- `512_Hard-Normal_107.json`: cap, 40.0 min, Hard Expander army 1094 buildings 209 vs Normal Rusher army 213 buildings 151
- `512_Hard-Normal_111.json`: cap, 40.0 min, Hard Expander army 803 buildings 180 vs Normal Balanced army 33 buildings 53
- `512_Hard-Normal_114.json`: error (could not host: Address already in use)
- `512_Normal-Easy_110.json`: cap, 40.0 min, Easy Balanced army 114 buildings 88 vs Normal Turtle army 377 buildings 110
- `512_Normal-Easy_114.json`: error (could not host: Address already in use)
- `512_Normal-Easy_115.json`: cap, 40.0 min, Normal Expander army 352 buildings 113 vs Easy Balanced army 152 buildings 72
- `512_Normal-Normal_101.json`: cap, 39.9 min, Normal Turtle army 8 buildings 55 vs Normal Turtle army 444 buildings 112
- `512_Normal-Normal_107.json`: cap, 40.0 min, Normal Expander army 290 buildings 153 vs Normal Rusher army 171 buildings 124
- `512_Normal-Normal_110.json`: cap, 39.9 min, Normal Balanced army 195 buildings 121 vs Normal Turtle army 128 buildings 66
- `512_Normal-Normal_113.json`: error (could not host: Address already in use)
- `512_Normal-Normal_116.json`: cap, 39.9 min, Normal Turtle army 436 buildings 125 vs Normal Balanced army 13 buildings 54

## Upsets (the weaker side won)

- `1024_Hard-Normal_104.json`: Normal Turtle beat Hard Rusher in 12.5 min
- `1024_Hard-Normal_105.json`: Normal Rusher beat Hard Expander in 8.3 min
- `1024_Hard-Normal_111.json`: Normal Turtle beat Hard Turtle in 16.4 min
- `1024_Hard-Normal_112.json`: Normal Rusher beat Hard Turtle in 18.8 min
- `256_Hard-Normal_102.json`: Normal Rusher beat Hard Rusher in 3.0 min
- `256_Hard-Normal_106.json`: Normal Balanced beat Hard Turtle in 7.8 min
- `512_Hard-Normal_106.json`: Normal Rusher beat Hard Rusher in 4.0 min
- `512_Hard-Normal_112.json`: Normal Balanced beat Hard Expander in 11.4 min
- `512_Hard-Normal_115.json`: Normal Balanced beat Hard Expander in 7.0 min

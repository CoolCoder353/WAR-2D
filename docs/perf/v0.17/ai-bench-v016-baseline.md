# v0.14 AI bench

`tools/ai-bench.sh` (spec §12): 1v1 Free-for-all, AI vs AI, one headless player per match (`Dev/AiBench.cs`, `-perf -aiBench`), the host sitting out, fast-forwarded. Styles are drawn per seat from a seeded generator (the same pair for a seed in every pairing); the seat order alternates by seed. Exit criteria: every match ends (a draw at the time cap is a failure) and Hard beats Normal, Normal beats Easy, in ≥ 80% on every map size.

Config: the build's own `GameConfig.xml`.

## Results

| Pairing | Map | Games | Stronger wins | Win rate | Draws / caps / errors | Length median / max (min) |
| --- | --- | --- | --- | --- | --- | --- |
| Hard-Normal | 256 | 16 | 13 | 81% | 0 / 1 / 0 | 4.0 / 12.2 |
| Hard-Normal | 512 | 16 | 11 | 69% | 0 / 4 / 1 | 11.0 / 28.7 |
| Hard-Normal | 1024 | 16 | 5 | 31% | 0 / 9 / 0 | 6.9 / 34.7 |
| Normal-Easy | 256 | 16 | 16 | 100% | 0 / 0 / 0 | 3.6 / 7.2 |
| Normal-Easy | 512 | 16 | 15 | 94% | 0 / 0 / 1 | 10.7 / 38.0 |
| Normal-Easy | 1024 | 16 | 13 | 81% | 0 / 3 / 0 | 16.7 / 34.6 |
| Hard-Hard | 256 | 16 | – | – | 0 / 3 / 0 | 4.3 / 9.4 |
| Hard-Hard | 512 | 16 | – | – | 0 / 8 / 0 | 7.2 / 36.2 |
| Hard-Hard | 1024 | 16 | – | – | 0 / 10 / 0 | 11.8 / 34.9 |
| Normal-Normal | 256 | 16 | – | – | 0 / 2 / 0 | 5.5 / 33.5 |
| Normal-Normal | 512 | 16 | – | – | 0 / 9 / 1 | 5.5 / 8.6 |
| Normal-Normal | 1024 | 16 | – | – | 0 / 9 / 0 | 18.5 / 39.4 |
| Easy-Easy | 256 | 16 | – | – | 0 / 2 / 0 | 7.6 / 26.9 |
| Easy-Easy | 512 | 16 | – | – | 0 / 5 / 0 | 15.6 / 35.5 |
| Easy-Easy | 1024 | 16 | – | – | 0 / 5 / 0 | 12.2 / 36.5 |

**Exit criteria: NOT met.**

## By style

Stronger side's wins by its own style and by the weaker side's style (all sizes):

| Pairing | Style | Stronger plays it: won / games | Weaker plays it: stronger won / games |
| --- | --- | --- | --- |
| Hard-Normal | Rusher | 11 / 14 | 7 / 12 |
| Hard-Normal | Turtle | 2 / 10 | 9 / 13 |
| Hard-Normal | Expander | 7 / 13 | 7 / 12 |
| Hard-Normal | Balanced | 9 / 10 | 6 / 10 |
| Normal-Easy | Rusher | 14 / 14 | 12 / 12 |
| Normal-Easy | Turtle | 9 / 10 | 11 / 13 |
| Normal-Easy | Expander | 12 / 13 | 11 / 12 |
| Normal-Easy | Balanced | 9 / 10 | 10 / 10 |

Mirror matches, wins by style (all sizes):

| Pairing | Rusher | Turtle | Expander | Balanced |
| --- | --- | --- | --- | --- |
| Hard-Hard | 10 / 27 | 5 / 23 | 8 / 26 | 4 / 20 |
| Normal-Normal | 11 / 27 | 3 / 23 | 9 / 25 | 4 / 19 |
| Easy-Easy | 11 / 27 | 7 / 23 | 12 / 26 | 6 / 20 |

## How they played

Means per seat over every match (all pairings and sizes): first attack, attacks, retreats, peak army, highest balance, unpaid upkeep seconds, concessions.

| Difficulty | Seats | First attack (min) | Attacks | Retreats | Peak army | Highest balance | Unpaid s | Conceded |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Hard | 143 | 5.7 | 2.3 | 0.5 | 284 | 12626 | 1 | 0 |
| Normal | 188 | 5.9 | 2.3 | 0.4 | 202 | 30689 | 1 | 0 |
| Easy | 143 | 6.7 | 2.6 | 0.5 | 73 | 15248 | 1 | 0 |

## Failures

- `1024_Easy-Easy_106.json`: cap, 39.9 min, Easy Turtle army 29 buildings 46 vs Easy Expander army 53 buildings 55
- `1024_Easy-Easy_110.json`: cap, 40.0 min, Easy Turtle army 162 buildings 98 vs Easy Balanced army 64 buildings 54
- `1024_Easy-Easy_111.json`: cap, 40.0 min, Easy Turtle army 40 buildings 56 vs Easy Turtle army 77 buildings 84
- `1024_Easy-Easy_113.json`: cap, 40.0 min, Easy Turtle army 141 buildings 87 vs Easy Expander army 29 buildings 45
- `1024_Easy-Easy_114.json`: cap, 40.0 min, Easy Rusher army 179 buildings 100 vs Easy Rusher army 132 buildings 68
- `1024_Hard-Hard_101.json`: cap, 40.0 min, Hard Balanced army 410 buildings 252 vs Hard Balanced army 582 buildings 281
- `1024_Hard-Hard_103.json`: cap, 40.0 min, Hard Expander army 480 buildings 212 vs Hard Expander army 45 buildings 92
- `1024_Hard-Hard_105.json`: cap, 40.0 min, Hard Expander army 502 buildings 197 vs Hard Rusher army 681 buildings 232
- `1024_Hard-Hard_108.json`: cap, 39.9 min, Hard Balanced army 687 buildings 163 vs Hard Turtle army 297 buildings 142
- `1024_Hard-Hard_111.json`: cap, 40.0 min, Hard Turtle army 689 buildings 233 vs Hard Turtle army 383 buildings 182
- `1024_Hard-Hard_112.json`: cap, 40.0 min, Hard Rusher army 530 buildings 163 vs Hard Turtle army 332 buildings 191
- `1024_Hard-Hard_113.json`: cap, 40.0 min, Hard Turtle army 109 buildings 103 vs Hard Expander army 458 buildings 205
- `1024_Hard-Hard_114.json`: cap, 40.0 min, Hard Rusher army 500 buildings 203 vs Hard Rusher army 352 buildings 148
- `1024_Hard-Hard_115.json`: cap, 40.0 min, Hard Balanced army 726 buildings 246 vs Hard Expander army 121 buildings 102
- `1024_Hard-Hard_116.json`: cap, 40.0 min, Hard Turtle army 220 buildings 186 vs Hard Balanced army 792 buildings 251
- `1024_Hard-Normal_102.json`: cap, 40.0 min, Normal Expander army 205 buildings 109 vs Hard Turtle army 514 buildings 185
- `1024_Hard-Normal_105.json`: cap, 40.0 min, Hard Expander army 386 buildings 174 vs Normal Rusher army 337 buildings 146
- `1024_Hard-Normal_106.json`: cap, 40.0 min, Normal Turtle army 143 buildings 105 vs Hard Expander army 478 buildings 191
- `1024_Hard-Normal_108.json`: cap, 40.0 min, Normal Balanced army 313 buildings 158 vs Hard Turtle army 387 buildings 145
- `1024_Hard-Normal_111.json`: cap, 40.0 min, Hard Turtle army 266 buildings 137 vs Normal Turtle army 268 buildings 72
- `1024_Hard-Normal_112.json`: cap, 40.0 min, Normal Rusher army 207 buildings 132 vs Hard Turtle army 320 buildings 181
- `1024_Hard-Normal_113.json`: cap, 40.0 min, Hard Turtle army 419 buildings 218 vs Normal Expander army 405 buildings 182
- `1024_Hard-Normal_114.json`: cap, 40.0 min, Normal Rusher army 395 buildings 151 vs Hard Rusher army 371 buildings 165
- `1024_Hard-Normal_115.json`: cap, 40.0 min, Hard Balanced army 306 buildings 118 vs Normal Expander army 201 buildings 120
- `1024_Normal-Easy_102.json`: cap, 40.0 min, Easy Expander army 84 buildings 58 vs Normal Turtle army 294 buildings 113
- `1024_Normal-Easy_107.json`: cap, 40.0 min, Normal Expander army 214 buildings 74 vs Easy Turtle army 115 buildings 83
- `1024_Normal-Easy_110.json`: cap, 39.9 min, Easy Turtle army 33 buildings 79 vs Normal Balanced army 217 buildings 90
- `1024_Normal-Normal_103.json`: cap, 39.9 min, Normal Expander army 121 buildings 98 vs Normal Expander army 159 buildings 129
- `1024_Normal-Normal_104.json`: cap, 40.0 min, Normal Turtle army 68 buildings 98 vs Normal Rusher army 334 buildings 150
- `1024_Normal-Normal_107.json`: cap, 39.9 min, Normal Expander army 203 buildings 117 vs Normal Turtle army 211 buildings 109
- `1024_Normal-Normal_108.json`: cap, 40.0 min, Normal Balanced army 411 buildings 143 vs Normal Turtle army 172 buildings 94
- `1024_Normal-Normal_109.json`: cap, 40.0 min, Normal Rusher army 121 buildings 40 vs Normal Turtle army 404 buildings 152
- `1024_Normal-Normal_110.json`: cap, 39.9 min, Normal Turtle army 240 buildings 149 vs Normal Balanced army 152 buildings 115
- `1024_Normal-Normal_111.json`: cap, 40.0 min, Normal Turtle army 168 buildings 94 vs Normal Turtle army 430 buildings 165
- `1024_Normal-Normal_114.json`: cap, 40.0 min, Normal Rusher army 263 buildings 88 vs Normal Rusher army 208 buildings 132
- `1024_Normal-Normal_115.json`: cap, 40.0 min, Normal Balanced army 28 buildings 51 vs Normal Expander army 303 buildings 111
- `256_Easy-Easy_106.json`: cap, 39.9 min, Easy Balanced army 90 buildings 70 vs Easy Turtle army 19 buildings 72
- `256_Easy-Easy_110.json`: cap, 40.0 min, Easy Balanced army 121 buildings 73 vs Easy Expander army 21 buildings 50
- `256_Hard-Hard_101.json`: cap, 40.0 min, Hard Rusher army 129 buildings 155 vs Hard Rusher army 150 buildings 71
- `256_Hard-Hard_103.json`: cap, 39.9 min, Hard Balanced army 229 buildings 162 vs Hard Turtle army 148 buildings 110
- `256_Hard-Hard_108.json`: cap, 40.0 min, Hard Rusher army 124 buildings 130 vs Hard Balanced army 123 buildings 149
- `256_Hard-Normal_106.json`: cap, 40.0 min, Normal Balanced army 119 buildings 145 vs Hard Turtle army 76 buildings 112
- `256_Normal-Normal_104.json`: cap, 40.0 min, Normal Expander army 136 buildings 116 vs Normal Balanced army 65 buildings 103
- `256_Normal-Normal_106.json`: cap, 40.0 min, Normal Balanced army 56 buildings 125 vs Normal Turtle army 215 buildings 144
- `512_Easy-Easy_102.json`: cap, 40.0 min, Easy Turtle army 74 buildings 76 vs Easy Turtle army 79 buildings 55
- `512_Easy-Easy_103.json`: cap, 40.0 min, Easy Expander army 144 buildings 57 vs Easy Expander army 198 buildings 69
- `512_Easy-Easy_106.json`: cap, 40.0 min, Easy Rusher army 20 buildings 56 vs Easy Rusher army 51 buildings 63
- `512_Easy-Easy_109.json`: cap, 39.9 min, Easy Balanced army 140 buildings 85 vs Easy Balanced army 8 buildings 23
- `512_Easy-Easy_114.json`: cap, 39.9 min, Easy Expander army 89 buildings 32 vs Easy Rusher army 87 buildings 44
- `512_Hard-Hard_102.json`: cap, 40.0 min, Hard Turtle army 302 buildings 213 vs Hard Turtle army 269 buildings 202
- `512_Hard-Hard_106.json`: cap, 40.0 min, Hard Rusher army 197 buildings 131 vs Hard Rusher army 45 buildings 72
- `512_Hard-Hard_107.json`: cap, 40.0 min, Hard Expander army 500 buildings 223 vs Hard Rusher army 236 buildings 194
- `512_Hard-Hard_109.json`: cap, 40.0 min, Hard Balanced army 214 buildings 110 vs Hard Balanced army 223 buildings 126
- `512_Hard-Hard_111.json`: cap, 40.0 min, Hard Expander army 418 buildings 228 vs Hard Balanced army 269 buildings 228
- `512_Hard-Hard_112.json`: cap, 40.0 min, Hard Balanced army 59 buildings 113 vs Hard Expander army 513 buildings 172
- `512_Hard-Hard_115.json`: cap, 40.0 min, Hard Expander army 485 buildings 226 vs Hard Balanced army 161 buildings 113
- `512_Hard-Hard_116.json`: cap, 40.0 min, Hard Turtle army 441 buildings 209 vs Hard Balanced army 162 buildings 137
- `512_Hard-Normal_105.json`: cap, 40.0 min, Hard Rusher army 399 buildings 207 vs Normal Rusher army 214 buildings 182
- `512_Hard-Normal_108.json`: cap, 40.0 min, Normal Expander army 66 buildings 147 vs Hard Expander army 301 buildings 203
- `512_Hard-Normal_110.json`: cap, 39.9 min, Normal Balanced army 112 buildings 120 vs Hard Turtle army 364 buildings 228
- `512_Hard-Normal_112.json`: cap, 40.0 min, Normal Balanced army 101 buildings 108 vs Hard Expander army 578 buildings 202
- `512_Hard-Normal_114.json`: error (could not host: Address already in use)
- `512_Normal-Easy_114.json`: error (could not host: Address already in use)
- `512_Normal-Normal_101.json`: cap, 40.0 min, Normal Turtle army 277 buildings 198 vs Normal Turtle army 322 buildings 175
- `512_Normal-Normal_102.json`: cap, 39.9 min, Normal Turtle army 411 buildings 188 vs Normal Turtle army 302 buildings 184
- `512_Normal-Normal_105.json`: cap, 39.9 min, Normal Rusher army 149 buildings 150 vs Normal Rusher army 557 buildings 164
- `512_Normal-Normal_107.json`: cap, 40.0 min, Normal Expander army 272 buildings 190 vs Normal Rusher army 395 buildings 95
- `512_Normal-Normal_108.json`: cap, 40.0 min, Normal Expander army 283 buildings 146 vs Normal Expander army 252 buildings 157
- `512_Normal-Normal_109.json`: cap, 39.9 min, Normal Balanced army 381 buildings 181 vs Normal Balanced army 80 buildings 119
- `512_Normal-Normal_110.json`: cap, 39.9 min, Normal Balanced army 1 buildings 107 vs Normal Turtle army 406 buildings 128
- `512_Normal-Normal_111.json`: cap, 39.9 min, Normal Expander army 25 buildings 94 vs Normal Balanced army 102 buildings 91
- `512_Normal-Normal_113.json`: error (could not host: Address already in use)
- `512_Normal-Normal_116.json`: cap, 40.0 min, Normal Turtle army 209 buildings 137 vs Normal Balanced army 244 buildings 173

## Upsets (the weaker side won)

- `1024_Hard-Normal_103.json`: Normal Expander beat Hard Expander in 4.8 min
- `1024_Hard-Normal_107.json`: Normal Turtle beat Hard Expander in 25.3 min
- `256_Hard-Normal_102.json`: Normal Rusher beat Hard Rusher in 12.2 min
- `256_Hard-Normal_114.json`: Normal Turtle beat Hard Turtle in 3.6 min

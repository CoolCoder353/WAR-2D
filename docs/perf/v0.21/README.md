# v0.21 Warpath: AI bench

`tools/ai-bench.sh --brief`, 90 matches (6 seeds × 3 map sizes × 5 pairings), 40-minute cap, 32× fast-forward, on a
Linux release build (`BuildCommands.LinuxBench`) of the commit named. The brief's v0.21 lines (idle army minutes,
give-ups, posts, forward bases, breaches) come from the phase 0 readouts.

## Baseline (2026-11-22, `9a885a0a`: v0.20 AI with the readouts, no behaviour change)

```
AI bench: 90 matches, map 256/512/1024; exit criteria NOT met
  Hard-Normal    83% (15/18, 95% 61%–94%) [256: 6/6, 512: 6/6, 1024: 3/6]  caps 3, draws 0, errors 0, median 7.7 min
  Normal-Easy    72% (13/18, 95% 49%–88%) [256: 6/6, 512: 5/6, 1024: 2/6]  caps 5, draws 0, errors 0, median 5.2 min
  Hard-Hard      18 games            caps 10, draws 0, errors 0, median 4.1 min
  Normal-Normal  18 games            caps 8, draws 0, errors 0, median 11.3 min
  Easy-Easy      18 games            caps 2, draws 0, errors 0, median 7.1 min
  At 10 min (mean): Hard bank 639526, income 3034/s, upkeep 885/s, army 569 · Normal bank 347028, income 1676/s, upkeep 514/s, army 322 · Easy bank 52650, income 539/s, upkeep 393/s, army 218
  Mean highest balance: Hard 3285908 · Normal 2224391 · Easy 130631
  Killed/lost: Hard 0.80 · Normal 0.75 · Easy 0.45; raids/seat: Hard 1.1 · Normal 0.8 · Easy 0.4
  Idle army min/seat: Hard 0.3 · Normal 0.8 · Easy 1.5; give-ups/seat: Hard 1.1 · Normal 0.8 · Easy 0.4
  Unfinished: 28 caps
```

What it says: armies hardly idle at the gathering point (under two minutes a match); the stalls are 28 of 90 matches
reaching the cap, mostly 1024 maps and mirrors, with armies marching on targets ("next target … building False",
"gave up on …: not reached in 240 s"). Money piles up into the millions by minute 10 (the rich-gem economy, out of
scope here).

## First after run (2026-11-22, `86984444`: every phase, the forward-base fix)

```
AI bench: 90 matches, map 256/512/1024; exit criteria NOT met
  Hard-Normal    61% (11/18, 95% 39%–80%) [256: 6/6, 512: 2/6, 1024: 3/6]  caps 5, draws 0, errors 0, median 4.5 min
  Normal-Easy    83% (15/18, 95% 61%–94%) [256: 6/6, 512: 5/6, 1024: 4/6]  caps 2, draws 0, errors 0, median 6.6 min
  Hard-Hard      18 games            caps 6, draws 0, errors 0, median 7.8 min
  Normal-Normal  18 games            caps 6, draws 0, errors 0, median 7.3 min
  Easy-Easy      18 games            caps 3, draws 0, errors 0, median 6.6 min
  At 10 min (mean): Hard bank 410521, income 2089/s, upkeep 554/s, army 329 · Normal bank 250562, income 1523/s, upkeep 412/s, army 264 · Easy bank 50233, income 500/s, upkeep 253/s, army 127
  Mean highest balance: Hard 2972248 · Normal 2007413 · Easy 201386
  Killed/lost: Hard 0.81 · Normal 0.74 · Easy 0.41; raids/seat: Hard 5.4 · Normal 1.2 · Easy 0.3
  Idle army min/seat: Hard 0.4 · Normal 0.5 · Easy 1.1; give-ups/seat: Hard 0.8 · Normal 0.6 · Easy 0.5
  Per seat: posts Hard 11.6 · Normal 5.8 · Easy 0.0; forward bases Hard 3.6 · Normal 3.4 · Easy 0.0; breaches Hard 0.0 · Normal 0.0 · Easy 0.0
  Unfinished: 22 caps
```

An earlier run on `c0099517` (before the forward-base fix: a forward base chosen and dropped every think, 85 starts a
seat) gave Hard-Normal 61%, Normal-Easy 94%, 23 caps.

| Against the baseline | Baseline | After | |
| --- | --- | --- | --- |
| Capped matches | 28 | 22 | better, far from the spec's 5 |
| Hard beats Normal | 83% | 61% (both runs) | **worse** |
| Normal beats Easy | 72% | 83% | better |
| Give-ups per seat (Hard, Normal, Easy) | 1.1, 0.8, 0.4 | 0.8, 0.6, 0.5 | a little better |
| Highest balance (Hard, Normal) | 3.29 M, 2.22 M | 2.97 M, 2.01 M | −10% |
| Army at 10 min (Hard, Normal) | 569, 322 | 329, 264 | **smaller** |
| Raids per seat (Hard) | 1.1 | 5.4 | as designed |

Reading it: the new behaviour runs (posts, raids, forward bases, fortifying all show up), but Hard's army at minute 10 is
42% smaller and Hard now loses a third of its matches to Normal. Likely causes, to check before tuning: queue top-ups at
Low priority slow a rich AI's production; three posts and three raids split Hard's army before its main attacks; and
forward-base producers spend money that went into units. No breach started in 90 matches: AI walls always leave a gap,
so a target behind them is still open; breaches matter against a player who walls in fully. Idle armies were already
rare (under two minutes a match), so the idle bar changes little in the bench. Not met: the spec's §9 exit criteria
(the ladder, at most 5 caps, idle and banked halved).

## After the Hard fix (2026-11-22, `30e4ddaf`)

The cause: "banked" (1,500 held for 30 s) comes in a Hard AI's first minute, and the banked rung claimed its most
expansions at once, so its Builders scattered and its home gems went half mined (32 Miners and 975/s at minute 5,
against 46 and 2,014/s in the baseline). The late rungs now wait for `Overflowing` (`<Ai><Warpath><OverflowSeconds>`).

```
AI bench: 90 matches, map 256/512/1024; exit criteria NOT met
  Hard-Normal    83% (15/18, 95% 61%–94%) [256: 5/6, 512: 6/6, 1024: 4/6]  caps 2, draws 0, errors 0, median 3.2 min
  Normal-Easy    94% (17/18, 95% 74%–99%) [256: 6/6, 512: 6/6, 1024: 5/6]  caps 1, draws 0, errors 0, median 7.2 min
  Hard-Hard      18 games            caps 9, draws 0, errors 0, median 9.2 min
  Normal-Normal  18 games            caps 5, draws 0, errors 0, median 12.4 min
  Easy-Easy      18 games            caps 3, draws 0, errors 0, median 9.1 min
  At 10 min (mean): Hard bank 557754, income 2385/s, upkeep 753/s, army 471 · Normal bank 300085, income 1600/s, upkeep 442/s, army 240 · Easy bank 62333, income 499/s, upkeep 258/s, army 116
  Mean highest balance: Hard 2968128 · Normal 1513429 · Easy 158969
  Killed/lost: Hard 0.84 · Normal 0.72 · Easy 0.43; raids/seat: Hard 8.8 · Normal 1.3 · Easy 0.5
  Idle army min/seat: Hard 0.4 · Normal 0.6 · Easy 1.5; give-ups/seat: Hard 1.1 · Normal 0.6 · Easy 0.3
  Per seat: posts Hard 11.2 · Normal 6.0 · Easy 0.0; forward bases Hard 1.8 · Normal 1.7 · Easy 0.0; breaches Hard 0.0 · Normal 0.0 · Easy 0.0
  Unfinished: 20 caps
```

| Against the baseline | Baseline | Now |
| --- | --- | --- |
| Hard beats Normal | 83% | 83% |
| Normal beats Easy | 72% | 94% |
| Capped matches | 28 | 20 |
| Highest balance (Hard, Normal) | 3.29 M, 2.22 M | 2.97 M, 1.51 M |
| Give-ups per seat (Hard, Normal, Easy) | 1.1, 0.8, 0.4 | 1.1, 0.6, 0.3 |

Still not met from §9: at most 5 caps (the 1024 mirrors still run to 40 minutes), idle and banked halved (idle armies
were never the problem; the bank is the rich-gem economy's). Each pairing has 18 matches, so a rate moves by about 20
points within its interval.

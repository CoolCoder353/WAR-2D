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

## After (v0.21, all phases)

Run at the end of the update; see below.

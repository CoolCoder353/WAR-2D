# Tuning the game

How to change balance numbers and the AI, and how to tell whether a change helped. Every number is in `Assets/Resources/GameConfig.xml`. Three tools work on it, from seconds to hours. Each prints a short summary, so a change can be judged (by you or by Claude) without reading match logs:

| Step | Tool | Takes | Tells you |
| --- | --- | --- | --- |
| Look | `node tools/balance-sheet.js` | under a second, no build | unit numbers, equal-cost fights, turret checks, flagged outliers |
| Measure | `tools/ai-bench.sh --brief` | minutes | AI-vs-AI win rates with their uncertainty, unfinished matches, money left unspent |
| Tune | `node tools/ai-tune.js <spec>` | hours, unattended | the best of the values you list, confirmed on fresh seeds |

The bench and the tuner need a Linux build (`Builds/Linux/WAR-2D.x86_64`, any build has the bench: it runs with `-perf`), `python3` (the full report) and `node`. The balance sheet needs only `node`.

## Where the numbers are

| Section | What it sets |
| --- | --- |
| `<Resources>` | Starting resources, passive and Miner income, decay while upkeep is unpaid |
| `<Units>` | Per unit: `Health`, `Damage`, `Range`, `AttackInterval`, `MoveSpeed`, `UpfrontCost`, `RunningCost` (upkeep per second), `Radius`, `SizeClass`, `Sight`, `Armour`, `BuildSeconds`, and `Splash`, `MinRange`, `BlastRadius`/`BlastDamage` where they apply |
| `<DamageTable>` | A multiplier per attacker and target class (`LightUnit`, `HeavyUnit`, `Building`, `Wall`; `Unit` means both unit classes; 1.0 when missing) |
| `<Buildings>` | Per building: health, footprint, price, upkeep, sight; the Gun turret's `Damage`, `Range` and `AttackInterval` |
| `<Siege>` | Walls, bombs, Builders' build power, repair, digging, gem payout, Radar, Jammer |
| `<Match><Map>` | Map generation: start sites, expansions, lanes, chokepoints, gems |
| `<Lobby>` | What the host may pick (map sizes, starting resources, start options, pick time) |
| `<Ai><Difficulty>` | How well each level plays: thinking rate, actions per minute, reactions, Builders, expansions, upkeep discipline, counters, retreats, scouting, defences, `IncomePerProducer`, `AttackRatio`, diplomacy |
| `<Ai><Style>` | What a style builds and when it attacks: build order, army mix, producers, Miners, expansions, first attack, attack size and interval, defences |
| `<Ai><Play>` | Sizes and pacing shared by every AI (placement, queues, upkeep model, gathering, staging, retreats, scouting) |
| `<Ai><Counters>`, `<Concede>`, `<Diplomacy>`, `<Brain>` | What the AI builds against what it has seen; when it surrenders; peace and gifts; its memory |

The comments in the file say what each value means, and [architecture.md](architecture.md) (AI managers) says how the AI uses them. Two things are code, not config: which building makes which unit (`SpawnerRules.Produces`) and the rules themselves (splash falling from 100% to 50% at the edge, for example).

`Config.ConfigParser` reads the file strictly, with a range for every number. An invalid file refuses to host. In the bench, every match then ends as an error "GameConfig.xml is invalid", the brief prints that message, and the match's log (kept in the results directory) names the value. The tuner stops at the first run whose matches all fail.

## The loop

1. **Look.** Run `node tools/balance-sheet.js` and read its Flags. To try a change, edit a copy and pass it: `node tools/balance-sheet.js my.xml`. Whatever the sheet flags is a lead, not a verdict (see [the model](#the-balance-sheet)).
2. **Measure.** Play a small slice with the change: `AI_BENCH_CONFIG=my.xml AI_BENCH_SIZES=512 AI_BENCH_PAIRINGS=Hard-Normal AI_BENCH_STYLES=Balanced tools/ai-bench.sh --brief 12`. Compare it with the same slice of the unchanged config (same seeds, so the same maps).
3. **Tune.** When the right value isn't obvious, list a few and let `tools/ai-tune.js` try them overnight. Then `node tools/ai-tune.js apply <dir>` writes the winner into `GameConfig.xml`.
4. **Confirm.** Run the full bench: `tools/ai-bench.sh 12` (all sizes, pairings and styles; it rewrites [perf/v0.14/ai-bench.md](perf/v0.14/ai-bench.md)). The v0.14 exit criteria are every match finishing and Hard beating Normal and Normal beating Easy in at least 80% on every map size. Tuning one slice can break another, so this run is the one that counts.
5. **Play.** The bench says who wins, not whether it is fun or fair to a human. Play the AI in a Private lobby (Single-player), or run a playtest ([playtesting.md](playtesting.md)).
6. **Land.**
   - Run `tools/unity-test.sh EditMode` and `PlayMode`. Most tests read the config, but a few pin a number (`RejoinTests` pins `ReconnectSeconds` 60), and the AI tests play on it (`AiSandbox`).
   - If the change makes the AI do more work per think (`ThinkSeconds`, `PlacementTriesPerThink`, `ActionsPerMinute`), run the perf gate's AI form (`WAR_PERF_AI=Hard tools/perf-run.sh 3`, `perf.part.ai` ≤ 2 ms p95). If it changes unit counts, ranges or sight in big fights, run the full gate.
   - Update [gameplay.md](gameplay.md): its unit, building and economy tables and its AI section repeat the config's numbers. Update [known-issues.md](known-issues.md) when an issue is fixed or found.
   - Put the tune's `summary.md` table (or the brief before and after) in the commit message, so the reason for a number stays with it.

## Reading results

**Small samples lie.** A win rate from a few games has a wide 95% interval (the brief prints it as `95% lo–hi`):

| Wins / games | Win rate | 95% interval |
| --- | --- | --- |
| 4 / 6 | 67% | 30–90% |
| 8 / 10 | 80% | 49–94% |
| 16 / 20 | 80% | 58–92% |
| 32 / 40 | 80% | 65–90% |
| 36 / 40 | 90% | 77–96% |
| 72 / 80 | 90% | 81–95% |

The six games per cell of the default bench can't tell 50% from 80%. Two results whose intervals overlap a lot are not different yet. To claim "at least 80%", you need the interval's lower end near 80%, which takes about 40 games at 90%.

**Change one thing at a time.**
- For a difficulty question, give both seats the same style (`AI_BENCH_STYLES=Balanced`), so only the difficulty differs.
- For a style question, use a mirror pairing (`Normal-Normal`), so only the style differs.
- The current report shows why this matters: the style matchup decides more games than the difficulty (Hard as a Rusher beat Normal 6 of 7 times; Hard as a Turtle won 0 of 4).

**Same seeds, same maps.** The bench draws each seat's style from the seed and alternates the seat order, so two runs with the same `AI_BENCH_SEED0` and seed count play the same maps and pairings. Check once whether a seed also replays to the same winner:

```bash
for d in a b; do AI_BENCH_DIR=PerfResults/det-$d AI_BENCH_SIZES=256 AI_BENCH_PAIRINGS=Hard-Normal tools/ai-bench.sh --brief 4; done
node -e 'const f=require("fs"),p=require("path"),[a,b]=process.argv.slice(1);for(const n of f.readdirSync(a).filter(x=>x.endsWith(".json"))){const r=(d)=>JSON.parse(f.readFileSync(p.join(d,n))),x=r(a),y=r(b);console.log(n,Math.round(x.matchSeconds),Math.round(y.matchSeconds),x.seats.map(s=>s.won)+""===y.seats.map(s=>s.won)+""?"same":"DIFFERENT")}' PerfResults/det-a PerfResults/det-b
```

If every match is "same", a before-and-after comparison on the same seeds is a paired test: a difference in even a few games means something. If not, the timing of the fast-forwarded run is part of the result, and only more games help.

**Look past the win rate.** The brief also gives:
- the median match length (pacing);
- unfinished matches (caps at `AI_BENCH_CAP` minutes are failures, usually an army stuck at a turret line or a base never found);
- the stronger side's wins by its style;
- the mean highest balance per difficulty (money it never spent).

A rising balance with flat win rates points at spending (producers, queues, `IncomePerProducer`), not fighting.

## Recipes

**Hard against Normal (or Normal against Easy).** [tools/ai-tune.example.json](../tools/ai-tune.example.json) is a starting spec. It plays Hard against Normal on 512 with both seats Balanced and tries Hard's `IncomePerProducer` and `AttackRatio`. Other `Difficulty.Hard.*` knobs worth trying: `ReactionSeconds`, `RetreatRatio`, `Builders`, `MaxExpansions`, `ExpandAfterSeconds`, `EarlyTurrets`, `FocusFire` (0 at every difficulty since the v0.14 tuning). A gap that closes with Balanced seats but not with every style is a style problem: confirm with the full bench.

**One style too weak or too strong.** Use `"pairings": "Normal-Normal"`, all four styles, and `"objective": { "kind": "style", "style": "Turtle" }` (or `"*"` for whichever style is furthest from 50%). The knobs are `Style.<name>.*`: `FirstAttackSeconds`, `AttackArmySize`, `AttackRatioScale`, `AttackIntervalSeconds`, `Expansions`, `ExpandTiming`, `Miners`, `Turrets`, `Walls`, `BuildOrder`, `ArmyMix`, `Producers` (strings work too, e.g. `"Style.Turtle.ArmyMix": ["Tank:4 Heavy:3 Artillery:2", "Tank:6 Heavy:2 Artillery:1"]`). Turtles lose the most games and cause the most caps (see [known-issues.md](known-issues.md)).

**A unit's numbers.**
1. Start in the balance sheet with a copy of the config. It shows at once what the change does to every equal-cost fight and to the turret checks.
2. Then play the copy in the bench (`AI_BENCH_CONFIG`) to check that matches still finish and the difficulty gap holds.

The AI builds units by its style's `ArmyMix` and its `<Counters>`, so the bench sees a unit only as far as the AIs build it. A tune spec can take `Unit.*` and `DamageTable.*` knobs, but its objectives measure the AI, not whether a unit is worth its price: use them as a check, not as the judge.

**Pacing and economy.** Starting resources, income (`Resources.MiningRate`, `Resources.PassiveGenerationRate`), prices and upkeep set how long a match runs and how big armies get. Watch the brief's median length and highest balance, and the sheet's "per Miner" column (how many of a unit one Miner's income carries).

## The balance sheet

`node tools/balance-sheet.js [config] [--budget 3000] [--packing 0.2] [--horizon 300] [--out <file>]` works out a one-page sheet from the config alone, with no Unity or build:

- **Units:** for each unit:
  - the producer (from a copy of `SpawnerRules.Produces` in the script: keep the two in step), armour and health;
  - damage per second against light units, heavy units, buildings and walls (the damage table applied);
  - range and minimum range, speed, price, upkeep, cost over `--horizon` seconds;
  - how many one Miner's income carries, and build time;
  - seconds for `--budget` of it to destroy an HQ, and seconds one unit takes per wall segment.
- **Economy:** income, producer and defence prices and upkeep in Miners, and the most a producer can spend (cost ÷ build time).
- **Equal-cost fights:** every pair of army units, `--budget` resources a side. `+62` means the row wins with 62% of its health left, `-40` that the column wins with 40% left.
- **Against a Gun turret:** the fewest units that kill one, and what they lose.
- **Suicide bomber:** what one blast reaches in a Tank clump.
- **Flags:** the list to read first. It flags:
  - a unit that wins or loses every fight;
  - a fight that splash decides (it comes out differently with `--packing 0`);
  - a unit that is no worse than another in every number;
  - a unit that out-ranges the turret, or that the turret holds off at 3× its cost;
  - an AI `IncomePerProducer` above what a Barracks or Heavy factory can spend.

The fights are a model, not the game:
- open ground, with the shorter-ranged side walking in under fire;
- every unit then fires per shot at the enemy's front unit, and the excess of a killing shot is wasted;
- no fire inside `MinRange`;
- splash hits the units behind the target, 100% falling to 50% at the edge, as many as `--packing` puts in the radius (the share of the ground an army's bodies cover).

There is no formation depth, kiting, retreat or focus fire, so fights between armies of one unit type undervalue support units such as Snipers and Artillery. Use it to find what to look at, then confirm in the bench.

## The brief bench

`tools/ai-bench.sh --brief [seeds] [jobs] [player]` is the bench ([debugging.md](debugging.md#the-ai-bench-v014)) with an eight-line summary instead of the full report:
- per pairing, the stronger side's win rate with its interval (per map size when there are several) and the unfinished matches;
- the stronger side's wins by its style, and mirror wins by style;
- the mean highest balance and unpaid seconds per difficulty;
- the error messages, if any matches errored.

Its exit code is the full report's (0 when the exit criteria are met, 2 when not). The full report goes to `report.md` in the results directory, so a quick run never overwrites [perf/v0.14/ai-bench.md](perf/v0.14/ai-bench.md) (set `AI_BENCH_OUT` to choose). `tools/ai-bench.sh brief <dir>...` prints the brief of finished runs, merged.

## The tuner

`node tools/ai-tune.js <spec.json> [--out <dir>] [--dry-run]` tunes config values unattended:
- **Candidates:** each is a copy of the base config with the spec's knobs set. `tools/lib/config-xml.js` changes only those values and keeps the comments and layout.
- **Scoring:** each candidate plays a brief bench run with `AI_BENCH_CONFIG` (no rebuild) and gets a score.
- **Search:** each pass tries every listed value of one knob, with the other knobs at their best values so far. It stops early when a pass changes nothing.
- **Confirm stage:** it replays the best few candidates and the base config on fresh seeds, because the best of many noisy screens is usually lucky.

It prints where it plays, one line per run as each finishes (on a terminal, a line below counts the current run's finished matches) and a short ranked table. Under `PerfResults/ai-tune-<date-time>` (or `--out`) it writes:
- `<id>.xml`, the candidate config;
- `<id>/`, its bench results and full report;
- `tune.jsonl`, one line per run;
- `summary.md`.

Other ways to run it:
- Re-run with the same `--out` to skip the runs already played, so an interrupted tune resumes.
- `--dry-run` checks every knob path and the objective against the pairings and styles, and prints how many matches it will play at most, without playing.
- `node tools/ai-tune.js apply <dir> [id]` writes the best confirmed candidate's values (or run `id`'s) into the base config.

The spec is JSON; the header of `tools/ai-tune.js` lists every field and default.

- `knobs`: `{ "<path>": [values] }`. A path is dot-separated element names and key attribute values, and must name exactly one value (an error lists the matches otherwise). The base value is always tried too. Examples:
  - `Difficulty.Hard.AttackRatio`, `Style.Turtle.FirstAttackSeconds`, `Play.StageShare`;
  - `Unit.Tank.Damage`, `Building.GunTurret.Range`;
  - `DamageTable.Tank.Wall` (the `<Entry attacker="Tank" target="Wall">`), `Counter.Heavy`.
- `bench`: the screen runs' `sizes`, `pairings`, `styles`, `seeds`, `jobs`, `cap`, `scale` and `seed0` (the `AI_BENCH_*` variables). Defaults: `512`, `Hard-Normal Normal-Easy`, the four styles, 8, 4, 40, 32, 101.
- `objective`:
  - `{ "kind": "stronger" }` (default) scores the stronger side's win rate over the non-mirror pairings.
  - `{ "kind": "style", "style": "Turtle", "target": 0.5 }` scores how close a style's win rate against other styles, in the mirror pairings (same difficulty), gets to `target`. With `"style": "*"` it scores the worst style.
  - Both subtract `failurePenalty` (default 0.5) times the share of matches that didn't finish.
- `passes` (default 2), `minGain` (how much a score must beat the best to replace it, default 0) and `confirm` (`top`, `seeds`, `seed0`: default 3, 16, 1001; `top` 0 skips the confirm stage). Screen runs tied with the last pick are confirmed too (up to twice `top`), and the summary says when the top of the screen tied: a screen that saturates (several candidates at 100%) can't rank them, so use more seeds or a harder slice next time.

**Cost:** a run's matches are seeds × sizes × pairings, so a screen run is cheap at 8 seeds, one size and one pairing. `--dry-run` prints the most matches a spec can play (152 for the example), and each result line ends with that run's wall time.

## Tuning with Claude

Claude can't run the game in a cloud session, so give it summaries, not raw output: the brief, or a tune's `summary.md`. Never paste match logs or the full report unless it asks for a specific part. It can run the balance sheet and `--dry-run` itself, write a spec, and read `tune.jsonl` if you commit or paste it. `CLAUDE.md` tells it the same.

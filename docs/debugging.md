# Debugging and developer tools (v0.10)

For Claude and the owner. Everything here is dev-only: it is compiled out of nothing, but gated at run time, and a release build never opens the API.

## Turning the control API on

- **Editor:** it listens on `127.0.0.1:7790` whenever play mode runs.
- **Development build:** `WAR-2D > Build > Linux (Dev)` (to `Builds/Dev/`), then start it with `-debugApi [port]`.
- It binds loopback only. Each process writes `<persistentDataPath>/debugapi/<port>.json` (`port`, `pid`, `token`, `role`); every request carries that token. The file is deleted on exit.
- Commands run on the main thread inside a `DevApi.Scope()`, so the dev APIs (free units/buildings, bots) work during a command and at no other time.

## The CLI

```bash
tools/war2d list                       # running instances
tools/war2d help                       # every command and its role
tools/war2d --port 7790 units owner=1  # one-shot; key=value, values are JSON when they parse
tools/war2d shell                      # interactive
tools/war2d launch --clients 1         # host + joiner from Builds/Dev, ports 7790, 7791
tools/war2d --json state               # raw reply
```

## Commands

| Group | Commands |
| --- | --- |
| Session | `ping`, `info`, `host`, `join route=…`, `leave`, `start [size seed mode options pick team]` (`pick=-1` is Unlimited; `team=0` puts every player on one team, Teams mode only), `quit` |
| Read | `state`, `players`, `units`, `unit id=…`, `buildings`, `tiles box=[x0,y0,x1,y1]`, `fog owner box`, `orders`, `perf`, `log`, `loglevel` |
| Scenario | `ai difficulty [style] [team] [colour]` (a lobby AI seat), `aistate [owner]` (each AI's style, plans, counts, actions and unpaid seconds), `timescale [n]` (run the match n times faster, 0.1–32; the tick catches up several ticks per frame; dev only), `bots n` (Scripted AI seats, owner ids from `AiOwnerIds`), `spawn owner type tile count`, `build owner type tile`, `resources`, `order`, `diplomacy`, `kill ids`, `pause`, `resume`, `step`, `wait` |
| View | `camera`, `select`, `screenshot`, `overlay layer on` |
| Replays | `record`, `replays`, `replay do=open/seek/speed/play/pause/stop/status` |

Server-role commands refuse on a client. A client answers `units`/`buildings` only from what it was sent, which is how you check for leaks across two processes.

## Overlays

`overlay layer=Fog on=true owner=1`, or hotkeys: Flow F1, Routes F2, Targets F3, Fog F4, Sectors F5, Hash F6, Footprints F7, Interest F8, Terrain F9. Layers that read server data (all but Routes and, on a client, Fog) draw only in the host process. With every layer off nothing is captured or drawn. Interest (`owner=…`) reads the replication service's real per-client sets, snapshot pacing included, so it shows what that owner's client was actually sent; it draws nothing when no client is replicated for the owner.

## Logs

`WarLog` has categories (Sim, Net, Replication, Steam, Lobby, Match, UI, Audio, Art, Replay, Dev, Unity) and levels. Unity's own log is captured into `Unity`. A per-session file goes to `<persistentDataPath>/logs/` (10 kept). Read it live with `war2d log max=50 category=Net level=Warn`.

## Replays

The host records every match from a fog-free virtual spectator (`MatchRecorder`) into `<persistentDataPath>/Replays/*.war2drec` (GZip: header JSON, then tick-stamped records). Only the host saves a replay; joined players get none (the old host-to-player transfer was removed on 2026-10-27, since its 16 KiB chunks exceeded Mirror's Direct batch limit). The host keeps the newest `<KeepCount>` recordings. The recorder stops replicating once its file is finished. Nothing is sent before *GameOver*. `ReplayPlayer` plays a file offline with speed and seeking (`war2d replay do=open path=…`). Config: `<Replays>` in `GameConfig.xml`.

Players watch them from the main menu's **Replays** screen (Watch, Delete, and a viewer with play/pause, speed, a seek bar and the players' numbers); see `docs/ux/v0.10-figma.md`. Hosted PlayMode tests switch recording off (`PlayModeMatch.Configure`) so they don't fill the real folder.

## Example: watch two AIs play

```bash
tools/war2d host
tools/war2d ai difficulty=Normal style=Balanced
tools/war2d ai difficulty=Easy style=Rusher
tools/war2d start size=256 seed=21 pick=15   # the host's HQ is placed at random when the pick time ends
tools/war2d wait state=Playing && tools/war2d timescale n=8
tools/war2d aistate                           # repeat; GameOver when one side is left
```

## The AI bench (v0.14)

`tools/ai-bench.sh [seeds=6] [jobs=4] [player=Builds/Linux/WAR-2D.x86_64]` plays AI-vs-AI 1v1 matches and writes [perf/v0.14/ai-bench.md](perf/v0.14/ai-bench.md): per pairing (Hard-Normal, Normal-Easy and the mirrors) and map size (256, 512, 1024), the stronger side's win rate, draws, matches still running at the time cap (40 match minutes: a failure), errors and lengths, then results by style, how each difficulty played (first attack, attacks, retreats, peak army, highest balance, unpaid seconds, concessions), the failures and the upsets. Each match is its own headless player (`-batchmode -nographics -perf -noSteam -aiBench <result.json>`, `Dev/AiBench.cs`): it hosts, adds the two AIs, starts a Free-for-all with the host sitting out (it places no HQ, so the win/loss rules drop it), fast-forwards (`AI_BENCH_SCALE`, 32) and writes the result with a minute-by-minute timeline per AI (balance, estimated income and upkeep, army, buildings, Miners, producers, turrets, army mode). `jobs` run at once, each on its own KCP port (`-port n`, dev only; the WebSocket listener moves to n + 1000). Styles are drawn per seat from a seeded generator, the same pair for a seed in every pairing; the seat order alternates by seed. Environment: `AI_BENCH_SIZES`, `AI_BENCH_PAIRINGS`, `AI_BENCH_STYLES`, `AI_BENCH_CAP`, `AI_BENCH_SCALE`, `AI_BENCH_SEED0`, `AI_BENCH_OUT` (`-` prints only), `AI_BENCH_DIR` (the results directory, default `PerfResults/ai-bench-<date-time>`) and `AI_BENCH_CONFIG` (a `GameConfig.xml` to play instead of the built one, through the dev-only `-configFile <path>`: tuning `<Ai>` or any balance number without a rebuild). `tools/ai-bench.sh report <dir>...` rewrites the report from finished runs (`PerfResults/ai-bench-*`). Logs are kept only for failed matches.

**Brief mode.** `tools/ai-bench.sh --brief [seeds] [jobs] [player]` prints one line to start and about eight at the end instead of a line per match and the full report: per pairing the stronger side's win rate with its 95% Wilson interval (per map size when there are several) and the unfinished matches, the stronger side's wins by its style, mirror wins by style, and the mean highest balance and unpaid seconds per difficulty. The exit code is the full report's (0 when the exit criteria are met, 2 when not). The per-match lines go to `matches.txt` and the full report to `report.md` in the results directory, so a quick run never overwrites `perf/v0.14/ai-bench.md` (set `AI_BENCH_OUT` to choose). `tools/ai-bench.sh brief <dir>...` prints the brief of finished runs, merged (`node tools/ai-bench-brief.js <dir>... [--json]` directly). It needs `node`. With six games per cell a result is ±30 points (4 of 6 is 30–90%), so read the interval before trusting a change: screen on one map size and one pairing, and keep the other dimensions fixed (`AI_BENCH_STYLES=Balanced` for both seats, so only the difficulty differs, or a mirror pairing, so only the style does).

## Tuning with the bench (`tools/ai-tune.js`)

`node tools/ai-tune.js <spec.json> [--out <dir>] [--dry-run]` tunes `GameConfig.xml` numbers unattended. Each candidate is a copy of the base config with the spec's knobs set (`tools/lib/config-xml.js` edits only those values, keeping the comments and layout), played with `tools/ai-bench.sh --brief` and `AI_BENCH_CONFIG` (no rebuild) and scored. It runs a coordinate search: each pass tries every listed value of one knob with the others at their best so far, and stops early when a pass changes nothing. Then a confirm stage replays the best few and the base config on fresh seeds, because the best of many noisy screens is usually lucky. It prints one line per run and a short ranked table, and writes under `PerfResults/ai-tune-<date-time>` (or `--out`): `<id>.xml` (the candidate config), `<id>/` (its bench results and full report), `tune.jsonl` (one line per run) and `summary.md`. Re-running with the same `--out` skips the runs already played, so an interrupted overnight tune resumes. `--dry-run` checks every knob path and the objective against the pairings and styles and prints how many matches it will play at most, without playing. `node tools/ai-tune.js apply <dir> [id]` writes the best confirmed candidate's values (or run `id`'s) into the base config.

The spec is JSON ([tools/ai-tune.example.json](../tools/ai-tune.example.json); the header of `tools/ai-tune.js` lists every field and default):

- `knobs`: `{ "<path>": [values] }`. A path is dot-separated element names and key attribute values: `Difficulty.Hard.AttackRatio`, `Style.Turtle.FirstAttackSeconds`, `Play.StageShare`, `Unit.Tank.Damage`, `Building.GunTurret.Range`, `DamageTable.Tank.Wall` (the `<Entry attacker="Tank" target="Wall">`), `Counter.Heavy`. It must name exactly one value (an error lists the matches otherwise). The base value is always tried too.
- `bench`: the screen runs' `sizes`, `pairings`, `styles`, `seeds`, `jobs`, `cap`, `scale` and `seed0` (the `AI_BENCH_*` variables; defaults `512`, `Hard-Normal Normal-Easy`, the four styles, 8, 4, 40, 32, 101).
- `objective`: `{ "kind": "stronger" }` (default) scores the stronger side's win rate over the non-mirror pairings. `{ "kind": "style", "style": "Turtle", "target": 0.5 }` scores how close a style's win rate against other styles, in the mirror pairings (same difficulty), gets to `target`. With `"style": "*"` it scores the worst style. Both subtract `failurePenalty` (default 0.5) times the share of matches that didn't finish.
- `passes` (default 2), `minGain` (how much a score must beat the best to replace it, default 0) and `confirm` (`top`, `seeds`, `seed0`: default 3, 16, 1001; `top` 0 skips it).

Matches per run are seeds × sizes × pairings, so the screen is cheap at 8 seeds, one size and one pairing. Check one thing first: whether the same seed replays to the same result. If it does, every candidate is compared on the same maps and small differences mean something. If it doesn't, use more seeds.

## The balance sheet (`tools/balance-sheet.js`)

`node tools/balance-sheet.js [config] [--budget 3000] [--packing 0.2] [--horizon 300] [--out <file>]` works out a one-page sheet from `GameConfig.xml` alone, in under a second, with no Unity or build:

- **Units:** the producer (from a copy of `SpawnerRules.Produces` in the script: keep the two in step), armour, health, damage per second against light units, heavy units, buildings and walls (the damage table applied), range and minimum range, speed, price, upkeep, cost over `--horizon` seconds, how many one Miner's income carries, build time, seconds for `--budget` of it to destroy an HQ and seconds one unit takes per wall segment.
- **Economy:** income, producer and defence prices and upkeep in Miners, and the most a producer can spend (cost ÷ build time).
- **Equal-cost fights:** every pair of army units, `--budget` resources a side.
- **Against a Gun turret:** the fewest units that kill one, and what they lose.
- **Suicide bomber:** what one blast reaches in a Tank clump.
- **Flags:** a unit that wins or loses every fight, a fight that splash decides (it comes out differently with `--packing 0`), a unit that is no worse than another in every number, a unit that out-ranges the turret or that the turret holds off at 3× its cost, and an AI `IncomePerProducer` above what a Barracks or Heavy factory can spend. Read the flags first.

The fights are a model, not the game: open ground, the shorter-ranged side walking in under fire, every unit then firing per shot at the enemy's front unit (the excess of a killing shot is wasted), no fire inside `MinRange`, and splash hitting the units behind the target (100% falling to 50% at the edge, as many as `--packing` puts in the radius: the share of the ground an army's bodies cover). There is no formation depth, kiting, retreat or focus fire, so single-type fights undervalue support units such as Snipers and Artillery. Use it to find what to look at, then confirm a change in the bench (with `AI_BENCH_CONFIG`, or a `Unit.*` knob in a tune spec).

## Perf runs (v0.14 additions)

`WAR_PERF_AI=Hard tools/perf-run.sh 3` runs the gate's AI form (seven lobby AIs on the 1024 map, the host sitting out, `perf.part.ai` ≤ 2 ms p95; `WAR_PERF_AI_WARMUP` minutes of fast-forwarded play first, default 12). `WAR_PERF_HOST_ARGS="…"` adds host arguments for experiments, e.g. `-configFile <path>` to try a config without a rebuild. Results: [perf/v0.14](perf/v0.14/README.md).

## Example: reproduce a pathing bug

```bash
tools/war2d host && tools/war2d start size=256 seed=5
tools/war2d wait state=PlacingHQ
tools/war2d build owner=<id> type=Base tile=[x,y]
tools/war2d spawn owner=<id> type=Tank tile=[60,60] count=20
tools/war2d order owner=<id> box=[55,55,65,65] kind=Move goal=[120,120]
tools/war2d overlay layer=Flow on=true
tools/war2d screenshot
```

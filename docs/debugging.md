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

`tools/ai-bench.sh [seeds=6] [jobs=4] [player=Builds/Linux/WAR-2D.x86_64]` plays AI-vs-AI 1v1 matches and writes [perf/v0.14/ai-bench.md](perf/v0.14/ai-bench.md): per pairing (Hard-Normal, Normal-Easy and the mirrors) and map size (256, 512, 1024), the stronger side's win rate, draws, matches still running at the time cap (40 match minutes: a failure), errors and lengths, then results by style, how each difficulty played (first attack, attacks, retreats, peak army, highest balance, unpaid seconds, concessions), the failures and the upsets. Each match is its own headless player (`-batchmode -nographics -perf -noSteam -aiBench <result.json>`, `Dev/AiBench.cs`): it hosts, adds the two AIs, starts a Free-for-all with the host sitting out (it places no HQ, so the win/loss rules drop it), fast-forwards (`AI_BENCH_SCALE`, 32) and writes the result with a minute-by-minute timeline per AI (balance, estimated income and upkeep, army, buildings, Miners, producers, turrets, army mode). `jobs` run at once, each on its own KCP port (`-port n`, dev only; the WebSocket listener moves to n + 1000). Styles are drawn per seat from a seeded generator, the same pair for a seed in every pairing; the seat order alternates by seed. Environment: `AI_BENCH_SIZES`, `AI_BENCH_PAIRINGS`, `AI_BENCH_STYLES`, `AI_BENCH_CAP`, `AI_BENCH_SCALE`, `AI_BENCH_SEED0`, `AI_BENCH_OUT` (`-` prints only) and `AI_BENCH_CONFIG` (a `GameConfig.xml` to play instead of the built one, through the dev-only `-configFile <path>`: `<Ai>` tuning without a rebuild). `tools/ai-bench.sh report <dir>...` rewrites the report from finished runs (`PerfResults/ai-bench-*`). Logs are kept only for failed matches.

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

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
| Session | `ping`, `info`, `host`, `join route=…`, `leave`, `start [size seed mode]`, `quit` |
| Read | `state`, `players`, `units`, `unit id=…`, `buildings`, `tiles box=[x0,y0,x1,y1]`, `fog owner box`, `orders`, `perf`, `log`, `loglevel` |
| Scenario | `bots n`, `spawn owner type tile count`, `build owner type tile`, `resources`, `order`, `diplomacy`, `kill ids`, `pause`, `resume`, `step`, `wait` |
| View | `camera`, `select`, `screenshot`, `overlay layer on` |
| Replays | `record`, `replays`, `replay do=open/seek/speed/play/pause/stop/status` |

Server-role commands refuse on a client. A client answers `units`/`buildings` only from what it was sent, which is how you check for leaks across two processes.

## Overlays

`overlay layer=Fog on=true owner=1`, or hotkeys: Flow F1, Routes F2, Targets F3, Fog F4, Sectors F5, Hash F6, Footprints F7, Interest F8, Terrain F9. Layers that read server data (all but Routes and, on a client, Fog) draw only in the host process. With every layer off nothing is captured or drawn. Interest (`owner=…`) reads the replication service's real per-client sets, snapshot pacing included, so it shows what that owner's client was actually sent; it draws nothing when no client is replicated for the owner.

## Logs

`WarLog` has categories (Sim, Net, Replication, Steam, Lobby, Match, UI, Audio, Art, Replay, Dev, Unity) and levels. Unity's own log is captured into `Unity`. A per-session file goes to `<persistentDataPath>/logs/` (10 kept). Read it live with `war2d log max=50 category=Net level=Warn`.

## Replays

The host records every match from a fog-free virtual spectator (`MatchRecorder`) into `<persistentDataPath>/Replays/*.war2drec` (GZip: header JSON, then tick-stamped records). After *GameOver* it offers the file to each player (`ReplayOffer`, chunked `ReplayChunk`s, hash-checked); players with *Save replays* on keep it (offers larger than `<MaxMegabytes>` are refused, a received file must open as a replay, and each client keeps its newest `<KeepCount>` received replays, so a host can never push out the player's own recordings). The recorder stops replicating once its file is finished. Nothing is sent before *GameOver*. `ReplayPlayer` plays a file offline with speed and seeking (`war2d replay do=open path=…`). Config: `<Replays>` in `GameConfig.xml`.

Players watch them from the main menu's **Replays** screen (Watch, Delete, and a viewer with play/pause, speed, a seek bar and the players' numbers); see `docs/ux/v0.10-figma.md`. Hosted PlayMode tests switch recording off (`PlayModeMatch.Configure`) so they don't fill the real folder.

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

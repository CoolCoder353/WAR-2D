# v0.10 performance gate (recorder on)

`tools/perf-run.sh` (now with `-perfRecord`) against a Linux release build, 2026-10-16: 8 owners, ~80,000 units, 7 real KCP clients. **Measured with the Unity editor open** (it could not be closed from the session), which costs about 4 ms of tick time against the v0.7 baseline of 20.9 ms.

| Run | tick.main p95 | tick.wait p95 | replication p95 | fps | bw avg / peak | record KB/s |
|---|---|---|---|---|---|---|
| Recorder off (1 run) | 24.9 ms | 15.0 ms | 17.1 ms | 93 | 185 / 283 KB/s | — |
| Recorder on (median of 3) | 30.3 ms | 26.3 ms | 20.8 ms | 91 | 183 / 284 KB/s | 661 |
| Recorder on, writer thread below normal priority (1 run) | 28.2 ms | 21.7 ms | 20.0 ms | 93 | — | 658 |
| **Recorder on, deferred lane (median of 3)** | **23.9 ms** | 4.3 ms | 17.1 ms | 138 | 183 / 251 KB/s | 654 |

Budget: tick.main p95 ≤ 25 ms. **Passes with the recorder on** (23.9 ms, editor open). `perf-run1..3.csv` are the passing runs.

The recorder's cost was its replication client: it sees all 80,000 units, so its interest and encoder jobs were the slowest client's, and the boundary waited on them. The fix (`DeferredLane`): an all-seeing client copies the settled unit arrays, routes and attacks at the boundary and encodes them during the next tick, collected at the following boundary. A recording's units are therefore a tick (50 ms) behind its buildings and terrain, which playback doesn't notice. Bandwidth to players is unchanged.

**File size.** The recorder takes ~660 KB/s of uncompressed payload at 80,000 units (gzip shrinks it further on disk). The 512 MB cap counts uncompressed bytes, so a perf-scale battle stops recording after about 13 minutes; a 30-minute, 8-player match at that size would be ~1.2 GB uncompressed. Normal matches (hundreds to a few thousand units) are a small fraction of this.

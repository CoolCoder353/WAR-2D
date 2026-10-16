# v0.7 performance gate (2026-10-16)

Linux release build, 8 owners x 10,000 tanks on a 1024² map (seed 1), 7 real headless clients over KCP, 3 runs, medians. Each owner also carries the siege load: 200 wall tiles, 5 Radars, 20 bombs, 50 Builders on the walls and 20 Diggers on the rock (`Dev/PerfMatch.cs`).

| Metric | Median | Budget | Result |
|---|---|---|---|
| Tick main thread, p95 | 20.9 ms | 25 ms | PASS |
| Frame rate, mean | 155 fps | 60 fps | PASS |
| Bandwidth per client, mean | 187 KB/s | 256 KB/s | PASS |
| Bandwidth per client, peak 1 s | 253 KB/s | 768 KB/s | PASS |

Replication p95 14.1 ms, units 79,988. Raw data: `runN.csv` (host) and `runN-clients/` (one CSV per client).

## Harness fixes found while running

- Headless clients threw in `MapView.Show` (no GPU buffers), skipping terrain init; `MapView.Show` now returns on a null graphics device.
- Since v0.9 joins use the direct transport, so the metered KCP transport was never used and bandwidth read 0; `GameManager.SetDirectTransport` lets `PerfClient` install it.

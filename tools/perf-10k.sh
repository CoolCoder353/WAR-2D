#!/usr/bin/env bash
# The 10,000-unit benchmark: one match of 10,000 active units (two owners x 5,000 on one front, the gate's battle script,
# siege load, defences and reinforcements, the 1024 map, seed 1), hosted by a headless server (the host's own army is one
# side), with one real player joined over KCP who renders at 1920x1080 windowed (default settings, VSync off, no frame cap)
# and zooms out, as a player can, until every unit it knows is on screen (PerfClient -perfView). Both sides share vision
# (-perfShareVision), so the player knows and draws all 10,000 units, not only the enemies its own army sees. Each run's
# client1/frames.csv holds every measured frame.
# Usage: tools/perf-10k.sh [runs=3] [player=Builds/Linux/WAR-2D.x86_64]
# Budgets: the client's mean fps and 1 % low >= 120, the server's tick.main p95 <= 25 ms, no logged errors, no dropped
# connections, Unity memory (native and managed reserved) growing <= 64 MB over the sampled minute, bandwidth as the gate. Extra host arguments go in
# WAR_PERF_HOST_ARGS (e.g. "-perfSampleTicks 12000" for a ten-minute soak).
set -euo pipefail
cd "$(dirname "$0")/.."
WAR_PERF_HEADLESS_HOST=1 WAR_PERF_VIEW=1 WAR_PERF_HOST_ARGS="-perfOwners 2 -perfUnitsPerOwner 5000 -perfShareVision ${WAR_PERF_HOST_ARGS:-}" \
  exec tools/perf-run.sh "${1:-3}" "${2:-Builds/Linux/WAR-2D.x86_64}" 1

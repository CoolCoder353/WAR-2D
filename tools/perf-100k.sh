#!/usr/bin/env bash
# The 100,000-unit client benchmark: the gate's match at 8 owners x 12,500 units (the unit cap) on the 1024 map, seed 1, four
# fronts, the battle script, siege load, defences and reinforcements, hosted by a headless server, with one real player joined
# over KCP who renders at 1920x1080 fullscreen (default settings, VSync off, no frame cap). Every owner shares its vision
# (-perfShareVision), so the player knows and draws all 100,000 units; it zooms out, as a player can, until they all show
# (PerfClient -perfView). Each run's client1/frames.csv holds every measured frame.
# Usage: tools/perf-100k.sh [runs=5] [player=Builds/Linux/WAR-2D.x86_64]
# Budgets: the client's mean fps and 1 % low >= 140, the server's tick.main p95 <= 28 ms, no logged errors, no dropped
# connections. Extra host arguments go in WAR_PERF_HOST_ARGS.
set -euo pipefail
cd "$(dirname "$0")/.."
# Pinned (host and client on separate halves of the CPUs), the client measures once it knows 90 % of the units, against 140 fps.
WAR_PERF_PIN="${WAR_PERF_PIN:-1}" WAR_PERF_CLIENT_ARGS="-perfExpectUnits 100000 -perfFpsBudget 140 ${WAR_PERF_CLIENT_ARGS:-}" \
  WAR_PERF_HEADLESS_HOST=1 WAR_PERF_VIEW=1 WAR_PERF_HOST_ARGS="-perfOwners 8 -perfUnitsPerOwner 12500 -perfShareVision ${WAR_PERF_HOST_ARGS:-}" \
  exec tools/perf-run.sh "${1:-5}" "${2:-Builds/Linux/WAR-2D.x86_64}" 1

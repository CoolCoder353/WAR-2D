#!/usr/bin/env bash
# Plays the 100,000-unit benchmark by hand: the same match as tools/perf-100k.sh (8 owners x 12,500 units, the 1024 map, seed 1, four
# fronts, siege load, reinforcements), hosted by a headless server, with you joined over KCP as owner 1 in a normal window: your own
# camera, selection and orders, your army not scripted (the other seven armies still fight). Close the game window to end it.
# Usage: tools/perf-play.sh [player=Builds/Linux/WAR-2D.x86_64]   (WAR_PERF_HOST_ARGS and WAR_PERF_CLIENT_ARGS add arguments)
set -euo pipefail
cd "$(dirname "$0")/.."
player="${1:-Builds/Linux/WAR-2D.x86_64}"
[[ -x "$player" ]] || { echo "No player at $player - make a Linux release build first."; exit 1; }
dir="$(mktemp -d)"
read -r -a hostExtra <<< "${WAR_PERF_HOST_ARGS:-}"
read -r -a clientExtra <<< "${WAR_PERF_CLIENT_ARGS:-}"
"$player" -batchmode -nographics -perf -perfClients 1 -perfInteractive -perfOwners 8 -perfUnitsPerOwner 12500 -perfShareVision \
  -perfSampleTicks 1200000 -perfOut "$dir/host" -logFile "$dir/host.log" "${hostExtra[@]}" &
host=$!
trap 'kill "$host" 2>/dev/null || true' EXIT
sleep 5
"$player" -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -perf -perfClient 127.0.0.1 -perfInteractive \
  -perfOut "$dir/client" -logFile "$dir/client.log" "${clientExtra[@]}"
echo "Logs in $dir"

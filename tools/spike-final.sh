#!/usr/bin/env bash
# The v0.3 protocol runs that close the results table (each configuration 3 times).
# Usage: tools/spike-final.sh   (build the Linux spike player first)
set -uo pipefail
cd "$(dirname "$0")/.."
bin=Builds/Spike/WAR2D-Spike.x86_64
[[ -x "$bin" ]] || { echo "Build the spike player first (Spike > Build Linux Player)." >&2; exit 2; }
root="$(pwd)"
stamp=$(date +%Y%m%d-%H%M%S)
# The bandwidth configuration chosen from the ladder exploration: 5 Hz checks, 1/8-tile delta
# corrections with the unit's speed and a projected resume waypoint, and the 64-tile camera view tier.
BW=(-corri 4 -cq 8 -cproj -cspeed -view 64)

group() { out="SpikeResults/$stamp-$1"; mkdir -p "$out"; echo "[final] $1 -> $out"; }
headless() { local tag="$1"; shift; "$bin" -batchmode -nographics -logFile "$root/$out/$tag.log" -out "$out" -tag "$tag" -quit "$@" >/dev/null 2>&1 || echo "[final] $tag failed ($?)"; }
windowed() { local tag="$1"; shift; "$bin" -screen-fullscreen 0 -screen-width 1920 -screen-height 1080 -logFile "$root/$out/$tag.log" -out "$out" -tag "$tag" -quit "$@" >/dev/null 2>&1 || echo "[final] $tag failed ($?)"; }

group sim
for r in 1 2 3; do for m in 512 1024; do
  headless sim-fronts-m$m-r$r            -spike sim -map $m -fronts
  headless sim-fronts-l1-m$m-r$r         -spike sim -map $m -fronts -slice 8
  headless sim-fronts-l2-m$m-r$r         -spike sim -map $m -fronts -slice 8 -sep 2
  headless sim-fronts-l3-m$m-r$r         -spike sim -map $m -fronts -slice 8 -sep 2 -halves
  headless sim-clump-l2-m$m-r$r          -spike sim -map $m -clump -slice 8 -sep 2
  headless sim-async-m$m-r$r             -spike sim -map $m -async
  headless sim-async-fronts-l2-m$m-r$r   -spike sim -map $m -fronts -slice 8 -sep 2 -async
done; done

group flow
for r in 1 2 3; do for m in 512 1024; do
  headless flow-hier2-rb1-m$m-r$r -spike flow -map $m -design hier2 -rebuild 1
done; done

group bandwidth
for r in 1 2 3; do for m in 512 1024; do for t in 8 2; do
  headless bw-plan-m$m-t$t-v8-r$r -spike bandwidth -map $m -teams $t -vision 8
  for v in 8 16 32; do headless bw-m$m-t$t-v$v-r$r -spike bandwidth -map $m -teams $t -vision $v "${BW[@]}"; done
done; done; done

group soak
for r in 1 2 3; do for t in 8 2; do headless soak-t$t-r$r -spike soak -teams $t "${BW[@]}"; done; done

group combined
for r in 1 2 3; do for t in 8 2; do
  windowed combined-t$t-r$r       -spike combined -teams $t "${BW[@]}"
  windowed combined-async-t$t-r$r -spike combined -teams $t -async "${BW[@]}"
done; done
echo "[final] done $stamp"

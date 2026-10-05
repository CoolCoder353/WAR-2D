#!/usr/bin/env bash
# Usage: tools/spike-run.sh <group> [extra args...]
# Runs a v0.3 spike benchmark group 3 times against the Linux spike player.
# Groups: sim flow hash fog render bandwidth soak combined. See docs/superpowers/plans/2026-10-05-v0.3-scale-spike.md.
set -euo pipefail
cd "$(dirname "$0")/.."
bin=Builds/Spike/WAR2D-Spike.x86_64
[[ -x "$bin" ]] || { echo "Build the spike player first (Spike > Build Linux Player)." >&2; exit 2; }
group="$1"; shift
out="SpikeResults/$(date +%Y%m%d-%H%M%S)-$group"
mkdir -p "$out"

# Unity resolves a relative -logFile against the player's own folder, not the working
# directory, so give it an absolute path to keep the logs next to results.csv.
logroot="$(pwd)"
headless() { local tag="$1"; shift; "$bin" -batchmode -nographics -logFile "$logroot/$out/$tag.log" -out "$out" -tag "$tag" -quit "$@"; }
windowed() { local tag="$1"; shift; "$bin" -screen-fullscreen 0 -screen-width 1920 -screen-height 1080 -logFile "$logroot/$out/$tag.log" -out "$out" -tag "$tag" -quit "$@"; }

for run in 1 2 3; do
  case "$group" in
    sim)       for m in 512 1024; do
                 for u in 10000 20000 40000 80000; do headless "sim-m$m-$u-r$run" -spike sim -map "$m" -units "$u" "$@"; done
                 for l in 0 30; do headless "sim-m$m-large$l-r$run" -spike sim -map "$m" -large "$l" "$@"; done
               done ;;
    flow)      for m in 256 512 1024; do headless "flow-$m-r$run" -spike flow -map "$m" "$@"; done ;;
    hash)      for m in 512 1024; do for c in 4 5 8; do headless "hash-m$m-c$c-r$run" -spike hash -map "$m" -cell "$c" "$@"; done; done ;;
    fog)       for m in 512 1024; do for t in 8 2; do
                 for v in 6 8 12 16 24 32; do headless "fog-m$m-t$t-v$v-r$run" -spike fog -map "$m" -teams "$t" -vision "$v" "$@"; done
                 headless "fog-m$m-t$t-bv20-r$run" -spike fog -map "$m" -teams "$t" -bvision 20 "$@"
               done; done ;;
    render)    windowed "render-r$run" -spike render "$@"; windowed "render-baseline-r$run" -spike render-baseline "$@" ;;
    bandwidth) for m in 512 1024; do for t in 8 2; do for v in 8 16 32; do
                 headless "bw-m$m-t$t-v$v-r$run" -spike bandwidth -map "$m" -teams "$t" -vision "$v" "$@"
               done; done; done ;;
    soak)      headless "soak-r$run" -spike soak "$@" ;;
    combined)  windowed "combined-r$run" -spike combined "$@"; windowed "combined-async-r$run" -spike combined -async "$@" ;;
    *) echo "Unknown group $group" >&2; exit 2 ;;
  esac
done
echo "Results in $out/results.csv"

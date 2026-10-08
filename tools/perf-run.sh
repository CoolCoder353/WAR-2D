#!/usr/bin/env bash
# Runs the v0.4 performance gate (spec §4.1) against an existing Linux player build.
# Usage: tools/perf-run.sh [runs=3] [player=Builds/Linux/WAR-2D.x86_64]
# Build first (non-development, Linux profile) and close the editor. Uses the `performance` CPU
# governor when cpupower is available. Prints, per metric, the run with the median value of its gated
# statistic, against the budget.
set -euo pipefail
cd "$(dirname "$0")/.."
runs="${1:-3}"
player="${2:-Builds/Linux/WAR-2D.x86_64}"
[[ -x "$player" ]] || { echo "No player at $player - make a Linux release build first."; exit 1; }
stamp="$(date +%Y%m%d-%H%M%S)"
out="PerfResults/$stamp"
mkdir -p "$out"
if command -v cpupower >/dev/null 2>&1; then sudo -n cpupower frequency-set -g performance >/dev/null 2>&1 || true; fi

for run in $(seq 1 "$runs"); do
  dir="$(pwd)/$out/run$run"
  mkdir -p "$dir"
  echo "Run $run/$runs..."
  "$player" -perf -perfOut "$dir" -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -logFile "$dir/player.log" || true
  [[ -f "$dir/perf.csv" ]] || { echo "Run $run produced no perf.csv (see $dir/player.log)"; exit 1; }
done

python3 - "$out" "$runs" <<'PY'
import csv, statistics, sys, os
out, runs = sys.argv[1], int(sys.argv[2])
tables = []
for r in range(1, runs + 1):
    with open(os.path.join(out, f"run{r}", "perf.csv")) as f:
        tables.append({row["metric"]: row for row in csv.DictReader(f)})
stat_col = {"mean": "mean", "p95": "p95", "max": "max"}
print(f"{'metric':22} {'stat':5} {'median':>12} {'budget':>12}  result")
failed = False
for metric, row in tables[0].items():
    stat = row["budget_stat"] or "p95"
    values = sorted(float(t[metric][stat_col[stat]]) for t in tables if t.get(metric) and t[metric][stat_col[stat]])
    if not values: continue
    median = values[len(values) // 2]
    budget = row["budget"]
    result = ""
    if budget:
        upper = metric != "perf.fps"
        ok = median <= float(budget) if upper else median >= float(budget)
        result = "PASS" if ok else "FAIL"
        failed |= not ok
    print(f"{metric:22} {stat:5} {median:12.3f} {budget:>12}  {result}")
sys.exit(1 if failed else 0)
PY

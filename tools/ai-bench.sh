#!/usr/bin/env bash
# The v0.14 AI-vs-AI bench (spec §12): plays N seeds per map size per pairing, 1v1, each match in its own headless
# player process (Dev/AiBench.cs: -perf -aiBench, the host sits out, the match fast-forwarded), several at once, and
# writes the report (winners, lengths, failures, styles) to docs/perf/v0.14/ai-bench.md.
#
# Usage: tools/ai-bench.sh [--brief] [seeds=6] [jobs=4] [player=Builds/Linux/WAR-2D.x86_64]
#        tools/ai-bench.sh report <results-dir>...   (rewrite the report from finished runs, merged)
#        tools/ai-bench.sh brief <results-dir>...    (the short summary of finished runs, merged)
# --brief prints one line to start and about eight at the end (tools/ai-bench-brief.js, needs node: win rates with a
# 95% interval, unfinished matches, wins by style, mean highest balance) instead of a line per match and the full
# report, and writes the full report into the results directory rather than over docs/perf/v0.14/ai-bench.md (unless
# AI_BENCH_OUT is set). Use it for quick tuning runs; tools/ai-tune.js runs it for every candidate config.
# Environment:
#   AI_BENCH_SIZES      map sizes (default "256 512 1024")
#   AI_BENCH_PAIRINGS   difficulty pairings (default "Hard-Normal Normal-Easy Hard-Hard Normal-Normal Easy-Easy")
#   AI_BENCH_STYLES     styles drawn per seat (default "Rusher Turtle Expander Balanced"; one name fixes it)
#   AI_BENCH_CAP        match minutes before a match counts as a draw at the cap, a failure (default 40)
#   AI_BENCH_SCALE      fast-forward (default 32)
#   AI_BENCH_SEED0      first map seed (default 101)
#   AI_BENCH_CONFIG     a GameConfig.xml to play with instead of the built one (-configFile; tuning without a rebuild)
#   AI_BENCH_OUT        the report (default docs/perf/v0.14/ai-bench.md, with --brief <results>/report.md; "-" prints it only)
#   AI_BENCH_DIR        the results directory (default PerfResults/ai-bench-<date-time>)
# Styles: each seat's style is drawn from a seeded generator per (size, seed), so a seed's style pair is the same in
# every pairing (comparable) and the bench is reproducible; the report splits results by style. The seat order
# (which side is the first owner) alternates by seed. The player must be a build with Dev/AiBench.cs (any build
# runs it with -perf); the bench runs beside the editor, on ports 7801 and up.
set -euo pipefail
cd "$(dirname "$0")/.."

report() {
  python3 - "$@" <<'PY'
import json, glob, os, sys, statistics, collections
dirs, out = sys.argv[1:-1], sys.argv[-1]
games = []
for d in dirs:
    for f in sorted(glob.glob(os.path.join(d, "*.json"))):
        try: g = json.load(open(f))
        except Exception: g = {"outcome": "error", "error": "unreadable result", "seats": []}
        g["file"] = os.path.basename(f)
        meta = os.path.basename(f)[:-5].split("_")  # size_pairing_seed
        g.setdefault("size", int(meta[0]))
        g["pairing"] = meta[1]
        games.append(g)
for d in dirs:  # jobs that never wrote a result (crashed or hung)
    jl = os.path.join(d, "jobs.txt")
    if not os.path.exists(jl): continue
    have = {g["file"] for g in games}
    for line in open(jl):
        name = line.split()[0] + ".json"
        if name not in have:
            size, pairing, seed = name[:-5].split("_")
            games.append({"file": name, "size": int(size), "pairing": pairing, "seed": int(seed), "outcome": "error", "error": "no result (crashed or killed)", "seats": [], "matchSeconds": 0})
order = {"Easy": 0, "Normal": 1, "Hard": 2}
def strong_weak(g):
    a, b = g["seats"]
    return (a, b) if order[a["difficulty"]] > order[b["difficulty"]] else (b, a)
lines = ["# v0.14 AI bench", "",
         "`tools/ai-bench.sh` (spec §12): 1v1 Free-for-all, AI vs AI, one headless player per match (`Dev/AiBench.cs`, `-perf -aiBench`), the host sitting out, fast-forwarded. Styles are drawn per seat from a seeded generator (the same pair for a seed in every pairing); the seat order alternates by seed. Exit criteria: every match ends (a draw at the time cap is a failure) and Hard beats Normal, Normal beats Easy, in ≥ 80% on every map size.", ""]
sizes = sorted({g["size"] for g in games})
pairings = [p for p in ["Hard-Normal", "Normal-Easy", "Hard-Easy", "Hard-Hard", "Normal-Normal", "Easy-Easy"] if any(g["pairing"] == p for g in games)]
def mins(gs):
    xs = [g["matchSeconds"] / 60 for g in gs if g["outcome"] == "win"]
    return (f"{statistics.median(xs):.1f}", f"{max(xs):.1f}") if xs else ("–", "–")
lines += ["## Results", "", "| Pairing | Map | Games | Stronger wins | Win rate | Draws / caps / errors | Length median / max (min) |", "| --- | --- | --- | --- | --- | --- | --- |"]
ok = True
for p in pairings:
    mirror = p.split("-")[0] == p.split("-")[1]
    for s in sizes:
        gs = [g for g in games if g["pairing"] == p and g["size"] == s]
        if not gs: continue
        fails = [g for g in gs if g["outcome"] != "win"]
        draws = sum(g["outcome"] == "draw" for g in gs); caps = sum(g["outcome"] == "cap" for g in gs); errs = sum(g["outcome"] == "error" for g in gs)
        if fails: ok = False
        if mirror:
            wins, rate = "–", "–"
        else:
            w = sum(1 for g in gs if g["outcome"] == "win" and strong_weak(g)[0]["won"])
            r = w / len(gs)
            wins, rate = str(w), f"{r:.0%}"
            if r < 0.8: ok = False
        med, mx = mins(gs)
        lines.append(f"| {p} | {s} | {len(gs)} | {wins} | {rate} | {draws} / {caps} / {errs} | {med} / {mx} |")
lines += ["", f"**Exit criteria: {'met' if ok else 'NOT met'}.**", ""]

# Styles: for the pairings with a stronger side, its win rate by its style and by its opponent's; mirrors by style.
lines += ["## By style", "", "Stronger side's wins by its own style and by the weaker side's style (all sizes):", "",
          "| Pairing | Style | Stronger plays it: won / games | Weaker plays it: stronger won / games |", "| --- | --- | --- | --- |"]
styles = ["Rusher", "Turtle", "Expander", "Balanced"]
for p in pairings:
    if p.split("-")[0] == p.split("-")[1]: continue
    gs = [g for g in games if g["pairing"] == p and len(g["seats"]) == 2]
    for st in styles:
        a = [g for g in gs if strong_weak(g)[0].get("style") == st]
        b = [g for g in gs if strong_weak(g)[1].get("style") == st]
        aw = sum(1 for g in a if strong_weak(g)[0]["won"]); bw = sum(1 for g in b if strong_weak(g)[0]["won"])
        lines.append(f"| {p} | {st} | {aw} / {len(a)} | {bw} / {len(b)} |")
lines += ["", "Mirror matches, wins by style (all sizes):", "", "| Pairing | Rusher | Turtle | Expander | Balanced |", "| --- | --- | --- | --- | --- |"]
for p in pairings:
    if p.split("-")[0] != p.split("-")[1]: continue
    gs = [g for g in games if g["pairing"] == p and len(g["seats"]) == 2]
    cells = []
    for st in styles:
        seats = [x for g in gs for x in g["seats"] if x.get("style") == st]
        cells.append(f"{sum(x['won'] for x in seats)} / {len(seats)}")
    lines.append(f"| {p} | " + " | ".join(cells) + " |")

# How they played (means over all seats of a difficulty).
lines += ["", "## How they played", "", "Means per seat over every match (all pairings and sizes): first attack, attacks, retreats, peak army, highest balance, unpaid upkeep seconds, concessions.", "",
          "| Difficulty | Seats | First attack (min) | Attacks | Retreats | Peak army | Highest balance | Unpaid s | Conceded |", "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
for d in ["Hard", "Normal", "Easy"]:
    seats = [x for g in games for x in g["seats"] if x.get("difficulty") == d]
    if not seats: continue
    fa = [x["firstAttackSeconds"] / 60 for x in seats if x["firstAttackSeconds"] >= 0]
    m = lambda k: statistics.mean(x[k] for x in seats)
    lines.append(f"| {d} | {len(seats)} | {statistics.mean(fa):.1f} | {m('attacks'):.1f} | {m('retreats'):.1f} | {m('peakArmy'):.0f} | {m('peakResources'):.0f} | {m('unpaidSeconds'):.0f} | {sum(x['conceded'] for x in seats)} |" if fa else f"| {d} | {len(seats)} | – | | | | | | |")

fails = [g for g in games if g["outcome"] != "win"]
upsets = [g for g in games if g["outcome"] == "win" and len(g["seats"]) == 2 and g["pairing"].split("-")[0] != g["pairing"].split("-")[1] and not strong_weak(g)[0]["won"]]
lines += ["", "## Failures", ""]
lines += [f"- `{g['file']}`: {g['outcome']}" + (f" ({g['error']})" if g.get("error") else "") + (f", {g.get('matchSeconds', 0) / 60:.1f} min, " + " vs ".join(f"{x['difficulty']} {x.get('style')} army {x['finalArmy']} buildings {x['finalBuildings']}" for x in g["seats"]) if g["seats"] else "") for g in fails] or ["None."]
lines += ["", "## Upsets (the weaker side won)", ""]
lines += [f"- `{g['file']}`: {strong_weak(g)[1]['difficulty']} {strong_weak(g)[1]['style']} beat {strong_weak(g)[0]['difficulty']} {strong_weak(g)[0]['style']} in {g['matchSeconds'] / 60:.1f} min" for g in upsets] or ["None."]
text = "\n".join(lines) + "\n"
if out == "-": print(text)
else:
    os.makedirs(os.path.dirname(out), exist_ok=True)
    open(out, "w").write(text)
    print(text)
sys.exit(0 if ok else 2)
PY
}

out="${AI_BENCH_OUT:-docs/perf/v0.14/ai-bench.md}"
if [[ "${1:-}" == "report" ]]; then shift; report "$@" "$out"; exit $?; fi
if [[ "${1:-}" == "brief" ]]; then shift; exec node tools/ai-bench-brief.js "$@"; fi
brief=0
if [[ "${1:-}" == "--brief" ]]; then
  brief=1; shift
  command -v node >/dev/null || { echo "--brief needs node (tools/ai-bench-brief.js)."; exit 1; }
fi

seeds="${1:-6}"
jobs="${2:-4}"
player="${3:-Builds/Linux/WAR-2D.x86_64}"
[[ -x "$player" ]] || { echo "No player at $player - make a Linux build first."; exit 1; }
player="$(realpath "$player")"
sizes="${AI_BENCH_SIZES:-256 512 1024}"
pairings="${AI_BENCH_PAIRINGS:-Hard-Normal Normal-Easy Hard-Hard Normal-Normal Easy-Easy}"
styles="${AI_BENCH_STYLES:-Rusher Turtle Expander Balanced}"
cap="${AI_BENCH_CAP:-40}"
scale="${AI_BENCH_SCALE:-32}"
seed0="${AI_BENCH_SEED0:-101}"
dir="$(realpath -m "${AI_BENCH_DIR:-PerfResults/ai-bench-$(date +%Y%m%d-%H%M%S)}")"
mkdir -p "$dir"
if (( brief )) && [[ -z "${AI_BENCH_OUT:-}" ]]; then out="$dir/report.md"; fi
extra=()
if [[ -n "${AI_BENCH_CONFIG:-}" ]]; then cp "$AI_BENCH_CONFIG" "$dir/GameConfig.xml"; extra=(-configFile "$dir/GameConfig.xml"); fi

# The job list: name, size, seed, seat A, seat B (Difficulty:Style).
python3 - "$seeds" "$sizes" "$pairings" "$styles" "$seed0" > "$dir/jobs.txt" <<'PY'
import random, sys
seeds, sizes, pairings, styles, seed0 = int(sys.argv[1]), sys.argv[2].split(), sys.argv[3].split(), sys.argv[4].split(), int(sys.argv[5])
for k in range(seeds):
    for size in sizes:
        rng = random.Random(f"{size}-{k}")
        sa, sb = rng.choice(styles), rng.choice(styles)
        seed = seed0 + k
        for p in pairings:
            a, b = p.split("-")
            if k % 2: a, b = b, a  # alternate the seat order
            print(f"{size}_{p}_{seed} {size} {seed} {a}:{sa} {b}:{sb}")
PY
total=$(wc -l < "$dir/jobs.txt")
echo "AI bench: $total matches, $jobs at a time, cap $cap min, ${scale}x; results in $dir"

# One match: its own process and KCP port (7801 + its line in the job list), so several run at once.
run_job() {
  local n=$1 name=$2 size=$3 seed=$4 a=$5 b=$6
  local port=$((7800 + n))
  timeout 4500 "$player" -batchmode -nographics -perf -noSteam -port "$port" $extraArgs \
    -aiBench "$dir/$name.json" -benchSize "$size" -benchSeed "$seed" -benchA "$a" -benchB "$b" \
    -benchCapMinutes "$cap" -benchScale "$scale" -logFile "$dir/$name.log" >/dev/null 2>&1 || true
  if [[ -f "$dir/$name.json" ]]; then
    python3 -c 'import json,sys; g=json.load(open(sys.argv[1])); o=g["outcome"]; m=g["matchSeconds"]/60; w=g["wallSeconds"]; seats=" ".join(("*" if s["won"] else "") + s["difficulty"] + ":" + str(s.get("style")) for s in g["seats"]); print(f"  {sys.argv[2]}: {o} {m:.1f} min ({w:.0f} s) {seats}")' "$dir/$name.json" "$name"
    # Logs are kept only for failures.
    if python3 -c 'import json,sys; sys.exit(0 if json.load(open(sys.argv[1]))["outcome"]=="win" else 1)' "$dir/$name.json"; then rm -f "$dir/$name.log"; fi
  else
    echo "  $name: no result (see $dir/$name.log)"
  fi
}
export -f run_job
export player dir cap scale
export extraArgs="${extra[*]:-}"
trap 'kill 0' INT TERM
if (( brief )); then
  # A line per match goes to matches.txt only (on a terminal, a count of them shows meanwhile); the brief follows, its
  # exit code the full report's.
  if [[ -t 1 ]]; then
    nl -w1 -s' ' "$dir/jobs.txt" | xargs -P "$jobs" -L 1 bash -c 'run_job "$@"' _ | {
      n=0
      while IFS= read -r line; do echo "$line" >> "$dir/matches.txt"; n=$((n + 1)); printf '\r  %d/%d matches' "$n" "$total"; done
      printf '\r\033[K'
    }
  else
    nl -w1 -s' ' "$dir/jobs.txt" | xargs -P "$jobs" -L 1 bash -c 'run_job "$@"' _ > "$dir/matches.txt"
  fi
  status=0
  report "$dir" "$out" > /dev/null || status=$?
  node tools/ai-bench-brief.js "$dir" || true
  [[ "$out" == "-" ]] || echo "  Full report: $out"
  exit "$status"
fi
nl -w1 -s' ' "$dir/jobs.txt" | xargs -P "$jobs" -L 1 bash -c 'run_job "$@"' _

report "$dir" "$out"

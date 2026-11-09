#!/usr/bin/env bash
# Runs a suite several times in batch mode and tallies which tests failed in how many runs, to find timing-dependent
# (flaky) tests and to check that a fix for one holds.
# Usage: tools/flake-hunt.sh [runs=5] [EditMode|PlayMode=PlayMode] [name-filter]
# Close the Unity editor first. Writes TestResults/flakes-<time>/ with each run's XML and flakes.md, the tally.
set -uo pipefail
cd "$(dirname "$0")/.."
source tools/lib/unity-batch.sh
runs="${1:-5}"; mode="${2:-PlayMode}"; filter="${3:-}"
if editor_open; then echo "The Unity editor has this project open: close it first."; exit 2; fi
out="TestResults/flakes-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"
extra=(); [[ -n "$filter" ]] && extra=(-testFilter "$filter")
ok=0
for i in $(seq 1 "$runs"); do
  result="$(run_suite "$mode" "$out/run$i" "${extra[@]}")" && ok=$((ok + 1))
  echo "Run $i/$runs: $result"
done
python3 - "$out" "$runs" "$mode" "$(git rev-parse --short HEAD)" <<'PY'
import sys, os, glob, collections, xml.etree.ElementTree as ET
out, runs, mode, commit = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
fails, msgs, ran = collections.Counter(), {}, 0
for xml in sorted(glob.glob(os.path.join(out, "run*", mode + ".xml"))):
    ran += 1
    for t in ET.parse(xml).getroot().iter("test-case"):
        if t.get("result") == "Failed":
            fails[t.get("fullname")] += 1
            msgs.setdefault(t.get("fullname"), (t.findtext("failure/message") or "").strip().splitlines()[:1])
lines = [f"# {mode} flake hunt: {ran} of {runs} runs produced results (commit `{commit}`)", ""]
if not fails:
    lines.append("No test failed in any run.")
else:
    lines += ["| Test | Failed in | First message |", "|---|---|---|"]
    for name, n in fails.most_common():
        m = msgs[name][0][:140].replace("|", "/") if msgs[name] else ""
        lines.append(f"| `{name}` | {n} of {ran} | {m} |")
open(os.path.join(out, "flakes.md"), "w").write("\n".join(lines) + "\n")
print("\n".join(lines))
PY
echo "Record: $out/flakes.md"

#!/usr/bin/env bash
# Recompiles scripts in the open Unity editor and exits non-zero on compile errors.
# Requires the editor to be open on this project with com.unity.pipeline (check: unity status).
# The Pipeline port briefly disappears during a recompile's domain reload, so polls
# tolerate an unreachable editor and retry instead of crashing.
set -euo pipefail
cd "$(dirname "$0")/.."

# The CLI exits non-zero while the Pipeline port is down (domain reload); mask it so the
# polling loops retry instead of being aborted by `set -o pipefail`.
uc() { unity command "$@" --no-banner --format json || true; }

# Prints the value at a dotted path under data.result (e.g. "status"), or nothing
# when the editor is unreachable or the response has no result.
res() {
  python3 -c '
import json, sys
try:
    node = (json.load(sys.stdin).get("data") or {}).get("result") or {}
    for key in sys.argv[1].split("."):
        node = node[key]
except Exception:
    node = ""
print(node if node is not None else "")
' "$1" 2>/dev/null || true
}

# Prints the latest captured error messages, one per line; nothing when unreachable.
error_lines() {
  python3 -c '
import json, sys
try:
    entries = ((json.load(sys.stdin).get("data") or {}).get("result") or {}).get("entries") or []
    print("\n".join(str(e.get("message", e)) for e in entries))
except Exception:
    pass
' 2>/dev/null || true
}

uc recompile >/dev/null 2>&1 || true
sleep 3
settled=""
for _ in $(seq 1 180); do
  status=$(uc recompile_status | res status)
  compiling=$(uc console_status | res groundTruth.compiling)
  if [[ -n "$status" && "$status" != "triggered" && "$status" != "compiling" && "$compiling" == "False" ]]; then
    settled=1
    break
  fi
  sleep 2
done

if [[ -z "$settled" ]]; then
  echo "Compile check failed: the Unity editor never became reachable. Is it open with com.unity.pipeline ready?"
  exit 1
fi

failed=""
for _ in $(seq 1 15); do
  failed=$(uc console_status | res groundTruth.compilationFailed)
  [[ "$failed" == "True" || "$failed" == "False" ]] && break
  sleep 2
done
if [[ "$failed" != "True" && "$failed" != "False" ]]; then
  echo "Compile check failed: could not read compilation ground truth from the editor."
  exit 1
fi
if [[ "$failed" == "True" ]]; then
  echo "Compilation failed. Latest errors:"
  uc console --level error --tail 40 | error_lines
  exit 1
fi
echo "Compile OK"

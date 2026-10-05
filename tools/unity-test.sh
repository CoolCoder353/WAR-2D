#!/usr/bin/env bash
# Usage: tools/unity-test.sh [EditMode|PlayMode] [name-filter]
# Recompiles, runs tests in the open Unity editor, and fails if any test fails or none ran.
# EditMode runs synchronously. PlayMode is submitted with --async_tests and then polled with
# `unity command test_status`, because entering play mode triggers a domain reload that drops
# the synchronous HTTP request before it can return results.
set -euo pipefail
cd "$(dirname "$0")/.."
mode="${1:-EditMode}"
filter="${2:-}"
tools/unity-compile.sh
args=(run_tests --mode "$mode" --timeout 900)
if [[ -n "$filter" ]]; then args+=(--filter "$filter"); fi

if [[ "${mode,,}" != "playmode" ]]; then
  unity command "${args[@]}" --no-banner --format json | python3 tools/parse_test_results.py "$mode"
  exit
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# Prints one token for a test_status document: down | running | completed | <other status>.
status_of() {
  python3 -c '
import json, sys
try:
    result = (json.load(sys.stdin).get("data") or {}).get("result")
except Exception:
    result = None
if not isinstance(result, dict):
    print("down")
else:
    status = result.get("status") or result.get("result")
    if not isinstance(status, str):
        status = "completed" if isinstance(result.get("Summary"), dict) else "down"
    print(status)
' 2>/dev/null || echo down
}

# Prints ok when a run_tests --async_tests submission started a run.
submitted() {
  python3 -c '
import json, sys
try:
    doc = json.load(open(sys.argv[1]))
except Exception:
    print("bad")
else:
    result = (doc.get("data") or {}).get("result") or {}
    print("ok" if doc.get("success") and result.get("success") and result.get("result") == "running" else "bad")
' "$1" 2>/dev/null || echo bad
}

# test_status serves the previous run's document until the new run starts, so hold it as a
# baseline and never mistake a stale document for this run's result.
unity command test_status --no-banner --format json > "$tmp/baseline.json" 2>/dev/null || true

if ! unity command "${args[@]}" --async_tests --no-banner --format json > "$tmp/submit.json" 2>&1; then
  echo "The PlayMode run could not be submitted:" >&2
  cat "$tmp/submit.json" >&2
  exit 2
fi
if [[ "$(submitted "$tmp/submit.json")" != "ok" ]]; then
  echo "The PlayMode run could not be submitted:" >&2
  cat "$tmp/submit.json" >&2
  exit 2
fi

start=$SECONDS
deadline=$(( start + ${UNITY_TEST_POLL_TIMEOUT:-1000} ))
final=""
saw_running=""
verdict=down
while (( SECONDS < deadline )); do
  unity command test_status --no-banner --format json > "$tmp/status.json" 2>/dev/null || true
  verdict="$(status_of < "$tmp/status.json")"
  case "$verdict" in
    down) ;;  # the editor port drops briefly while the play-mode domain reloads
    running) saw_running=1 ;;
    *)
      if [[ -n "$saw_running" ]] || ! cmp -s "$tmp/status.json" "$tmp/baseline.json"; then
        final="$tmp/status.json"
        break
      fi
      ;;
  esac
  sleep 2
done

if [[ -z "$final" ]]; then
  echo "The PlayMode run did not finish within $(( SECONDS - start ))s (last status: $verdict)." >&2
  exit 2
fi

python3 tools/parse_test_results.py "$mode" < "$final"

#!/usr/bin/env bash
# The gate a change passes before it lands: compiles and runs EditMode and PlayMode in Unity batch mode, with Burst compiled
# before any job runs, and writes a record of the run.
# Usage: tools/check.sh [--fast] [--editmode] [--playmode]
#   --fast      skip the tests tagged [Category("Slow")] (about 40 s of EditMode instead of about 110 s); not for landing
#   --editmode  EditMode only;  --playmode  PlayMode only
# Close the Unity editor first: batch mode can't open a project the editor has open (tools/unity-test.sh runs the suites in
# the open editor instead). Each run writes TestResults/<time>-<commit>/ with the suites' XML, their logs and summary.md;
# TestResults/latest points at the newest. Exits 0 only when every suite ran and nothing failed.
set -uo pipefail
cd "$(dirname "$0")/.."
source tools/lib/unity-batch.sh

fast=""; modes=(EditMode PlayMode)
for arg in "$@"; do
  case "$arg" in
    --fast) fast=1 ;;
    --editmode) modes=(EditMode) ;;
    --playmode) modes=(PlayMode) ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $arg (see --help)"; exit 2 ;;
  esac
done

[[ -x "$UNITY_EDITOR" ]] || { echo "No Unity editor at $UNITY_EDITOR (set UNITY_EDITOR)."; exit 2; }
if editor_open; then
  echo "The Unity editor has this project open: close it, or run tools/unity-test.sh EditMode / PlayMode in the editor."
  exit 2
fi

commit="$(git rev-parse --short HEAD)"
dirty=""; [[ -n "$(git status --porcelain --untracked-files=no)" ]] && dirty=" (with uncommitted changes)"
out="TestResults/$(date +%Y%m%d-%H%M%S)-$commit"
mkdir -p "$out"
extra=()
[[ -n "$fast" ]] && extra=(-testCategory "!Slow")

summary="$out/summary.md"
{
  echo "# Test run $(date '+%Y-%m-%d %H:%M')"
  echo
  echo "- Commit: \`$commit\` on \`$(git branch --show-current)\`$dirty"
  echo "- Machine: $(uname -sr), $(nproc) threads; Unity $UNITY_VERSION, batch mode, Burst compiled before jobs run"
  [[ -n "$fast" ]] && echo "- **Fast run:** tests tagged Slow skipped. Not a landing gate."
  echo
  echo "| Suite | Total | Passed | Failed | Skipped | Seconds |"
  echo "|---|---|---|---|---|---|"
} > "$summary"

status=0
failures=""
for mode in "${modes[@]}"; do
  echo "Running $mode..."
  result="$(run_suite "$mode" "$out" "${extra[@]}")"; rc=$?
  if [[ $rc -ne 0 ]]; then
    echo "| $mode | no results: see $mode.log | | | | |" >> "$summary"
    echo "$mode produced no results (compile error, crash or a project lock): see $out/$mode.log"
    grep -E "error CS|Aborting batchmode|another Unity instance" "$out/$mode.log" 2>/dev/null | sort -u | head -5
    status=1
    continue
  fi
  read -r total passed failed skipped secs <<< "$result"
  echo "| $mode | $total | $passed | $failed | $skipped | $secs |" >> "$summary"
  echo "$mode: $passed of $total passed, $failed failed, $skipped skipped ($secs s)"
  if [[ "$failed" != "0" ]]; then
    status=1
    failures+="$(failed_tests "$out/$mode.xml")"$'\n'
  fi
  [[ "$total" == "0" ]] && { echo "$mode ran no tests"; status=1; }
done

if [[ -n "$failures" ]]; then
  { echo; echo "## Failed"; echo; printf '%s' "$failures" | sed '/^$/d; s/^/- /'; } >> "$summary"
  echo; echo "Failed:"; printf '%s' "$failures" | sed '/^$/d; s/^/  /'
fi
{ echo; echo "Result: $([[ $status -eq 0 ]] && echo PASS || echo FAIL)"; } >> "$summary"
ln -sfn "$(basename "$out")" TestResults/latest
echo "Record: $summary"
exit $status

#!/usr/bin/env bash
# Shared helpers for running the Unity test suites in batch mode (tools/check.sh, tools/flake-hunt.sh).
# Source it; it defines UNITY_EDITOR, editor_open, and run_suite.

UNITY_VERSION="$(sed -n 's/^m_EditorVersion: //p' ProjectSettings/ProjectVersion.txt 2>/dev/null)"
UNITY_EDITOR="${UNITY_EDITOR:-$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity}"

# True when a Unity editor has this project open. Batch mode can't run then: Unity refuses a second instance on the
# same project and exits at once with no test results and no message on the console.
editor_open() {
  local project
  project="$(pwd -P)"
  pgrep -fai "Editor/(Unity|unityhub-unity-editor).*-projectpath[ =]+$project( |$)" >/dev/null 2>&1 && [[ -e Temp/UnityLockfile ]]
}

# run_suite <EditMode|PlayMode> <out-dir> [extra Unity args...]
# Runs one suite in batch mode, Burst compiled before any job runs (as in a player), and writes <out-dir>/<mode>.xml
# and <mode>.log. Prints "total passed failed skipped seconds" on success; returns 2 when Unity produced no results.
# Not -nographics: EditMode segfaulted with it on Linux (2026-11-08).
run_suite() {
  local mode="$1" out="$2"
  shift 2
  mkdir -p "$out"
  rm -f "$out/$mode.xml"
  "$UNITY_EDITOR" -batchmode -projectPath "$(pwd -P)" -runTests -testPlatform "$mode" \
    -testResults "$out/$mode.xml" -logFile "$out/$mode.log" --burst-force-sync-compilation "$@" >/dev/null 2>&1
  if [[ ! -s "$out/$mode.xml" ]]; then
    echo "no results"
    return 2
  fi
  python3 - "$out/$mode.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
print(r.get("total"), r.get("passed"), r.get("failed"), r.get("skipped"), round(float(r.get("duration") or 0)))
PY
}

# failed_tests <xml>: the full names of the failed tests, one per line, with the first line of each message.
failed_tests() {
  python3 - "$1" <<'PY'
import sys, xml.etree.ElementTree as ET
for t in ET.parse(sys.argv[1]).getroot().iter("test-case"):
    if t.get("result") == "Failed":
        msg = (t.findtext("failure/message") or "").strip().splitlines()
        print(t.get("fullname") + (" :: " + msg[0][:160] if msg else ""))
PY
}

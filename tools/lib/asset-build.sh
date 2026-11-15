#!/usr/bin/env bash
# Shared body of tools/art-build.sh and tools/audio-build.sh. Source it after setting KIND (art, sfx or music).
# Runs WAR2D.EditorTools.AssetCli in the open editor when there is one, otherwise in batch mode, and prints its
# change report: every sprite, sound or track whose hash differs from Assets/Art/hashes.json.
set -uo pipefail
cd "$(dirname "$0")/.."
source tools/lib/unity-batch.sh

only="*"; check=""; show=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --only) only="${2:?--only needs a glob}"; shift 2 ;;
    --check) check=1; shift ;;
    --show) show="${2:?--show needs a glob}"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1 (see --help)"; exit 2 ;;
  esac
done
[[ "$only" =~ ^[A-Za-z0-9_*?]+$ ]] || { echo "--only takes a glob of letters, digits, _, * and ?"; exit 2; }
[[ -z "$show" || "$show" =~ ^[A-Za-z0-9_*?]+$ ]] || { echo "--show takes a glob of letters, digits, _, * and ?"; exit 2; }

out="TestResults/$(date +%Y%m%d-%H%M%S)-$(git rev-parse --short HEAD)/assets"
mkdir -p "$out"
report="$out/report.txt"
write=$([[ -n "$check" ]] && echo false || echo true)

if editor_open; then
  unity command eval --no-banner --timeout 600 --code "WAR2D.EditorTools.AssetCli.Run(\"$KIND\", \"$only\", $write, \"$out\", \"$show\");" >"$out/unity.log" 2>&1 || true
  # The editor answers within seconds but goes on building: wait for the report (a full art build takes about two minutes).
  for _ in $(seq 1 300); do [[ -s "$report" ]] && break; sleep 1; done
else
  [[ -x "$UNITY_EDITOR" ]] || { echo "No Unity editor at $UNITY_EDITOR (set UNITY_EDITOR)."; exit 2; }
  "$UNITY_EDITOR" -batchmode -quit -projectPath "$(pwd -P)" -executeMethod WAR2D.EditorTools.AssetCli.FromArgs \
    -assetKind "$KIND" -assetOnly "$only" -assetWrite "$write" -assetOut "$out" -assetShow "$show" -logFile "$out/unity.log" >/dev/null 2>&1
fi

if [[ ! -s "$report" ]]; then
  echo "No report: the build did not finish (see $out/unity.log, or the editor's console)."
  exit 1
fi
cat "$report"
grep -q '^FAILED' "$report" && exit 1
exit 0

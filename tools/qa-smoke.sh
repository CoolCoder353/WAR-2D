#!/bin/bash
# Two-process smoke test: builds a Linux player (Builds/QA) through the live editor, then runs a host
# and a client that joins it over loopback. Each writes its state, warnings/errors and screenshots
# (Dev/QaSmoke.cs) to the output directory.
# Usage: tools/qa-smoke.sh [out-dir=Builds/QA-out] [nobuild]
set -u
cd "$(dirname "$0")/.."
out="$(realpath -m "${1:-Builds/QA-out}")"
if [[ "${2:-}" != "nobuild" ]]; then
  tools/unity-compile.sh | grep -q "Compile OK" || { tools/unity-compile.sh | tail -5; exit 1; }
  rm -rf Builds/QA # a partial folder makes Unity skip copying the launcher
  # The build outlives the CLI's request timeout; wait for the player and for the editor to finish.
  timeout 20 unity command eval --code '
var scenes = new System.Collections.Generic.List<string>();
foreach (var s in UnityEditor.EditorBuildSettings.scenes) if (s.enabled) scenes.Add(s.path);
UnityEditor.BuildPipeline.BuildPlayer(new UnityEditor.BuildPlayerOptions { scenes = scenes.ToArray(), locationPathName = "Builds/QA/WAR-2D.x86_64", target = UnityEditor.BuildTarget.StandaloneLinux64 });
return "built";' >/dev/null 2>&1
  sleep 20
  for _ in $(seq 1 120); do
    [[ -x Builds/QA/WAR-2D.x86_64 ]] && timeout 10 unity command eval --code 'return UnityEditor.BuildPipeline.isBuildingPlayer;' 2>/dev/null | grep -q '"result":false' && break
    sleep 10
  done
  [[ -x Builds/QA/WAR-2D.x86_64 ]] || { echo "Build failed: no player at Builds/QA/WAR-2D.x86_64 (is a dialog open in the editor?)"; exit 1; }
fi
rm -rf "$out" && mkdir -p "$out"
args=(-qaOut "$out" -screen-width 1280 -screen-height 720 -screen-fullscreen 0)
Builds/QA/WAR-2D.x86_64 -qaHost "${args[@]}" -logFile "$out/host.log" &
host=$!
sleep 6
Builds/QA/WAR-2D.x86_64 -qaJoin 127.0.0.1 "${args[@]}" -logFile "$out/client.log" &
client=$!
for _ in $(seq 1 60); do
  grep -q done "$out/client.txt" 2>/dev/null && grep -q done "$out/host.txt" 2>/dev/null && break
  sleep 3
done
kill "$host" "$client" 2>/dev/null
for role in host client; do echo "== $role"; grep -v "^  identity" "$out/$role.txt" 2>/dev/null; done
echo "Screenshots and logs: $out"

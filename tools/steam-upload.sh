#!/usr/bin/env bash
# Usage: tools/steam-upload.sh [--no-build] [--login] <branch> [description]
# Builds fresh Linux and Windows Steam depots (headless Unity, so the editor must be CLOSED), then uploads
# them to the given Steam branch through steamcmd.
#   --no-build  upload the existing Builds/Steam/{linux,windows} without rebuilding
#   --login     one-time interactive login that caches the Steam session (password + Steam Guard code)
# Needs steam/steam.env (see steam/steam.env.example). Normal runs never prompt for the password or the
# Steam Guard code: they reuse the cached login and fail fast if it is missing or expired.
# This script never sees or stores the password.
set -euo pipefail
cd "$(dirname "$0")/.."
build=1; login=0
while [[ "${1:-}" == --* ]]; do
  case "$1" in
    --no-build) build=0 ;;
    --login) login=1 ;;
    *) echo "Unknown option $1"; exit 1 ;;
  esac
  shift
done
[[ -f steam/steam.env ]] || { echo "Create steam/steam.env from steam/steam.env.example"; exit 1; }
# shellcheck disable=SC1091
source steam/steam.env
for v in APP_ID DEPOT_LINUX DEPOT_WINDOWS STEAM_USER; do [[ -n "${!v:-}" ]] || { echo "$v is empty in steam/steam.env"; exit 1; }; done
STEAMCMD="${STEAMCMD:-steamcmd}"
# steamcmd keeps its cached login in <its install>/config/config.vdf. Under the default HOME it shares that
# file with the desktop Steam client (~/.steam/steam), which rewrites it and drops steamcmd's login, so every
# upload asked for the password again. A HOME of its own gives steamcmd a private install and login cache.
STEAMCMD_HOME="${STEAMCMD_HOME:-${XDG_DATA_HOME:-$HOME/.local/share}/war2d-steamcmd}"
mkdir -p "$STEAMCMD_HOME"
run_steamcmd() { HOME="$STEAMCMD_HOME" "$STEAMCMD" "$@"; }

if [[ $login == 1 ]]; then
  run_steamcmd +login "$STEAM_USER" +quit
  echo "Login cached in $STEAMCMD_HOME. Run the upload without --login from now on."
  exit
fi

branch="${1:?usage: tools/steam-upload.sh [--no-build] [--login] <branch> [description]}"
desc="${2:-v$(grep -m1 'bundleVersion:' ProjectSettings/ProjectSettings.asset | awk '{print $2}') $(git rev-parse --short HEAD)}"

# Check the cached login before spending minutes on the builds.
# steamcmd's exit code is unreliable, so also look for a failed login in its output.
check="$(run_steamcmd +@NoPromptForPassword 1 +@ShutdownOnFailedCommand 1 +login "$STEAM_USER" +quit 2>&1)" && ! grep -qE "FAILED|not found" <<<"$check" \
  || { echo "No cached Steam login (or it expired). Run: tools/steam-upload.sh --login"; grep -E "FAILED|not found" <<<"$check" | tail -3; exit 1; }

if [[ $build == 1 ]]; then
  UNITY="${UNITY_EDITOR:-$HOME/Unity/Hub/Editor/$(grep -m1 m_EditorVersion: ProjectSettings/ProjectVersion.txt | awk '{print $2}')/Editor/Unity}"
  [[ -x "$UNITY" ]] || { echo "Unity editor not found at $UNITY (set UNITY_EDITOR)"; exit 1; }
  # Hub launches the editor with a lowercase -projectpath; a batch build cannot open a project that is open.
  pgrep -if "Editor/Unity .*-projectpath +$PWD( |$)" >/dev/null 2>&1 \
    && { echo "Close the Unity editor first (batch builds cannot open an open project), or pass --no-build."; exit 1; }
  mkdir -p steam/output
  for target in Windows Linux; do # Linux last, so the project reopens on its usual target
    case $target in Linux) unity_target=Linux64 ;; Windows) unity_target=Win64 ;; esac
    rm -rf "Builds/Steam/${target,,}" # no stale files from an older build in the depot
    echo "Building $target (log: steam/output/build-$target.log)..."
    "$UNITY" -batchmode -nographics -quit -projectPath "$PWD" -buildTarget "$unity_target" \
      -executeMethod "WAR2D.EditorTools.BuildCommands.$target" -logFile "steam/output/build-$target.log" \
      || { echo "$target build failed:"; grep -E "error|Exception|Build failed" "steam/output/build-$target.log" | tail -15; exit 1; }
  done
fi

for d in Builds/Steam/linux Builds/Steam/windows; do [[ -d "$d" ]] || { echo "Missing $d: build it first (WAR-2D/Build menu)"; exit 1; }; done

# Valve does not let a script set the 'default' branch live:
# for 'default' this uploads the build and you set it live in the Steamworks dashboard (SteamPipe > Builds).
setlive=""
[[ "$branch" == "default" ]] || setlive="\"SetLive\" \"$branch\""

out="$(realpath -m steam/output)"; mkdir -p "$out"
cat > "$out/depot_linux.vdf" <<VDF
"DepotBuildConfig" { "DepotID" "$DEPOT_LINUX" "ContentRoot" "$(realpath Builds/Steam/linux)" "FileMapping" { "LocalPath" "*" "DepotPath" "." "recursive" "1" } "FileExclusion" "steam_appid.txt" "FileExclusion" "*_BurstDebugInformation_DoNotShip/*" }
VDF
cat > "$out/depot_windows.vdf" <<VDF
"DepotBuildConfig" { "DepotID" "$DEPOT_WINDOWS" "ContentRoot" "$(realpath Builds/Steam/windows)" "FileMapping" { "LocalPath" "*" "DepotPath" "." "recursive" "1" } "FileExclusion" "steam_appid.txt" "FileExclusion" "*_BurstDebugInformation_DoNotShip/*" }
VDF
cat > "$out/app_build.vdf" <<VDF
"AppBuild" { "AppID" "$APP_ID" "Desc" "$desc" "BuildOutput" "$out/logs" $setlive "Depots" { "$DEPOT_LINUX" "$out/depot_linux.vdf" "$DEPOT_WINDOWS" "$out/depot_windows.vdf" } }
VDF
# No password on the command line: steamcmd reuses the cached login (see --login). NoPromptForPassword
# makes it fail instead of asking for the password or a Steam Guard code.
run_steamcmd +@NoPromptForPassword 1 +@ShutdownOnFailedCommand 1 +login "$STEAM_USER" +run_app_build "$out/app_build.vdf" +quit \
  || { echo "Upload failed (see $out/logs). If the login expired, run: tools/steam-upload.sh --login"; exit 1; }
echo "Uploaded '$desc' to branch '$branch'."

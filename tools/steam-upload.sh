#!/usr/bin/env bash
# Usage: tools/steam-upload.sh <branch> [description]
# Uploads Builds/Steam/linux and Builds/Steam/windows to the given Steam branch through steamcmd.
# Needs steam/steam.env (see steam/steam.env.example). steamcmd asks for the password and Steam Guard
# code on first use and caches the login; this script never sees or stores them.
set -euo pipefail
cd "$(dirname "$0")/.."
branch="${1:?usage: tools/steam-upload.sh <branch> [description]}"
desc="${2:-v$(grep -m1 'bundleVersion:' ProjectSettings/ProjectSettings.asset | awk '{print $2}') $(git rev-parse --short HEAD)}"
[[ -f steam/steam.env ]] || { echo "Create steam/steam.env from steam/steam.env.example"; exit 1; }
# shellcheck disable=SC1091
source steam/steam.env
for v in APP_ID DEPOT_LINUX DEPOT_WINDOWS STEAM_USER; do [[ -n "${!v:-}" ]] || { echo "$v is empty in steam/steam.env"; exit 1; }; done
for d in Builds/Steam/linux Builds/Steam/windows; do [[ -d "$d" ]] || { echo "Missing $d: build it first (WAR-2D/Build menu)"; exit 1; }; done

out="$(realpath -m steam/output)"; mkdir -p "$out"
cat > "$out/depot_linux.vdf" <<VDF
"DepotBuildConfig" { "DepotID" "$DEPOT_LINUX" "ContentRoot" "$(realpath Builds/Steam/linux)" "FileMapping" { "LocalPath" "*" "DepotPath" "." "recursive" "1" } "FileExclusion" "steam_appid.txt" }
VDF
cat > "$out/depot_windows.vdf" <<VDF
"DepotBuildConfig" { "DepotID" "$DEPOT_WINDOWS" "ContentRoot" "$(realpath Builds/Steam/windows)" "FileMapping" { "LocalPath" "*" "DepotPath" "." "recursive" "1" } "FileExclusion" "steam_appid.txt" }
VDF
cat > "$out/app_build.vdf" <<VDF
"AppBuild" { "AppID" "$APP_ID" "Desc" "$desc" "BuildOutput" "$out/logs" "SetLive" "$branch" "Depots" { "$DEPOT_LINUX" "$out/depot_linux.vdf" "$DEPOT_WINDOWS" "$out/depot_windows.vdf" } }
VDF
"$STEAMCMD" +login "$STEAM_USER" +run_app_build "$out/app_build.vdf" +quit

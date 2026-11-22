#!/usr/bin/env bash
# Builds the sound effects or the music from the command line and reports what changed.
# Usage: tools/audio-build.sh [--music] [--only <glob>] [--check]
#   --music        build the tracks (WAR-2D/Audio/Build music) instead of the sound effects (WAR-2D/Audio/Build SFX)
#   --only <glob>  build only the sounds whose id matches (shot_*)
#   --check        write nothing under Assets: only report what would change, with before and after WAVs
# Works with the editor open (it runs there) or closed (batch mode). The report names each sound whose hash differs
# from Assets/Art/hashes.json; before and after WAVs go to TestResults/<time>-<commit>/assets/.
# A changed sound needs the owner's approval before it is built without --check (CLAUDE.md, Asset approval).
KIND=sfx
args=()
for a in "$@"; do [[ "$a" == "--music" ]] && KIND=music || args+=("$a"); done
set -- "${args[@]}"
source "$(dirname "$0")/lib/asset-build.sh"

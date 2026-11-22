#!/usr/bin/env bash
# Builds the art atlases (WAR-2D/Art/Build all) from the command line and reports what changed.
# Usage: tools/art-build.sh [--only <glob>] [--check]
#   --only <glob>  render only the sprites whose id matches (Tank*, Burst*); the rest are taken as they are from the
#                  built atlases, so a small change takes seconds, not two minutes
#   --check        write nothing under Assets: only report what would change, with before and after pictures
#   --finish2 <glob>  with --check: draw the matching sprites with finish 2 (candidates for the owner's review)
#   --show <glob>  also write the pictures of the sprites whose id matches, changed or not (tools/asset-review.py)
# Works with the editor open (it runs there) or closed (batch mode). The report names each sprite whose hash differs
# from Assets/Art/hashes.json; before and after sheets at 4x go to TestResults/<time>-<commit>/assets/.
# A changed sprite needs the owner's approval before it is built without --check (CLAUDE.md, Asset approval).
KIND=art
source "$(dirname "$0")/lib/asset-build.sh"

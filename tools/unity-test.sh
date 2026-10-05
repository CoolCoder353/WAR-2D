#!/usr/bin/env bash
# Usage: tools/unity-test.sh [EditMode|PlayMode] [name-filter]
# Recompiles, runs tests in the open Unity editor, and fails if any test fails or none ran.
set -euo pipefail
cd "$(dirname "$0")/.."
mode="${1:-EditMode}"
filter="${2:-}"
tools/unity-compile.sh
args=(run_tests --mode "$mode" --timeout 900)
if [[ -n "$filter" ]]; then args+=(--filter "$filter"); fi
unity command "${args[@]}" --no-banner --format json | python3 tools/parse_test_results.py

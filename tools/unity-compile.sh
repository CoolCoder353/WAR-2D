#!/usr/bin/env bash
# Recompiles scripts in the open Unity editor and exits non-zero on compile errors.
# Requires the editor to be open on this project with com.unity.pipeline (check: unity status).
set -euo pipefail
cd "$(dirname "$0")/.."

uc() { unity command "$@" --no-banner --format json; }
res() { python3 -c "import sys,json; d=json.load(sys.stdin)['data']['result']; print($1)"; }

uc recompile >/dev/null
sleep 3
for _ in $(seq 1 180); do
  status=$(uc recompile_status | res 'd["status"]')
  compiling=$(uc console_status | res 'd["groundTruth"]["compiling"]')
  if [[ "$status" != "triggered" && "$status" != "compiling" && "$compiling" == "False" ]]; then
    break
  fi
  sleep 2
done

failed=$(uc console_status | res 'd["groundTruth"]["compilationFailed"]')
if [[ "$failed" == "True" ]]; then
  echo "Compilation failed. Latest errors:"
  uc console --level error --tail 40 | res '"\n".join(str(e.get("message", e)) for e in d["entries"])'
  exit 1
fi
echo "Compile OK"

#!/usr/bin/env python3
"""Reads Unity test-run output on stdin, prints a summary, sets the exit code.

Accepts both shapes Unity's pipeline produces under data.result:
  * synchronous  (`run_tests`):        Summary (Total/Passed/Failed/Skipped) + Results
  * asynchronous (`test_status`):      status ("completed") + summary (lowercase) + results
An optional argument, or UNITY_TEST_MODE, supplies the mode label for documents that do not
carry one of their own (asynchronous status documents never do).
"""
import json
import os
import sys

mode_hint = (sys.argv[1] if len(sys.argv) > 1 else os.environ.get("UNITY_TEST_MODE", "")) or ""


def die(message):
    print(message)
    sys.exit(2)


try:
    doc = json.load(sys.stdin)
except Exception:
    die("Could not read test output: stdin is not JSON.")

if not isinstance(doc, dict):
    die("Could not read test output: unexpected document.")

if not doc.get("success", False):
    print("run_tests failed:", json.dumps(doc.get("errors", doc), indent=2))
    sys.exit(2)

result = (doc.get("data") or {}).get("result")
if not isinstance(result, dict):
    die("Could not read test output: the document has no result.")

status = result.get("status")
if isinstance(status, str) and status.lower() != "completed":
    if status.lower() == "running":
        die("The test run is still running; poll again for its result.")
    die(f"Unexpected test status '{status}': {json.dumps(result.get('summary', {}))}")

summary = result.get("Summary") or result.get("summary") or {}
if not isinstance(summary, dict):
    die("Could not read test output: the summary is not an object.")


def count(sync_key, async_key):
    value = summary.get(sync_key, summary.get(async_key))
    return value if isinstance(value, int) else None


total = count("Total", "total")
passed = count("Passed", "passed")
failed = count("Failed", "failed")
skipped = count("Skipped", "skipped")
if None in (total, passed, failed, skipped):
    die("Could not read test output: the summary is missing counts.")

print(f"{result.get('Mode') or result.get('mode') or mode_hint or 'Unknown'}: "
      f"total={total} passed={passed} failed={failed} skipped={skipped}")

results = result.get("Results") or result.get("results") or []
if not isinstance(results, list):
    results = []


def is_failure(r):
    if not isinstance(r, dict):
        return False
    for key in ("Status", "Result", "Outcome", "ResultState"):
        outcome = str(r.get(key, "")).lower()
        if outcome.startswith("fail") or outcome.startswith("error"):
            return True
    return False


failures = [r for r in results if is_failure(r)]
for r in failures:
    print("FAIL:", r.get("FullName", r.get("Name")))
    print("    ", r.get("Message", ""))
if total == 0:
    print("No tests ran.")
sys.exit(1 if failed or failures or total == 0 else 0)

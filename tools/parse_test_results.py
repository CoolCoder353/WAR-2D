#!/usr/bin/env python3
"""Reads `unity command run_tests --format json` output on stdin, prints a summary, sets the exit code."""
import json
import sys

doc = json.load(sys.stdin)
if not doc.get("success", False):
    print("run_tests failed:", json.dumps(doc.get("errors", doc), indent=2))
    sys.exit(2)
result = doc["data"]["result"]
summary = result["Summary"]
print(f"{result.get('Mode')}: total={summary['Total']} passed={summary['Passed']} "
      f"failed={summary['Failed']} skipped={summary['Skipped']}")


def is_failure(r):
    outcome = str(r.get("Result", r.get("Outcome", r.get("ResultState", "")))).lower()
    return outcome.startswith("fail") or outcome.startswith("error")


for r in result.get("Results", []):
    if is_failure(r):
        print("FAIL:", r.get("FullName", r.get("Name")))
        print("    ", r.get("Message", ""))
sys.exit(1 if summary["Failed"] or summary["Total"] == 0 else 0)

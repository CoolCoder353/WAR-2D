#!/usr/bin/env python3
"""The owner's approvals of built assets (asset-generation spec §3.6).

docs/art/asset-approvals.json holds, per asset, the hash the owner approved, the date and the review page.
Assets/Art/hashes.json holds the hash of what is built now. This tool compares the two.

  tools/asset-approvals.py                 a count per state
  tools/asset-approvals.py --pending       every asset whose built hash is not its approved one, and those with no record
  tools/asset-approvals.py --record <kind> <id>... --date YYYY-MM-DD --page <url or folder>
                                           record the owner's approval of the hashes built now (kind: sprites, sounds, tracks)

Only record what the owner approved, asset by asset, in their own words (CLAUDE.md, Asset approval). An asset that is
built but has no record is "no record": nothing is approved by assumption.
"""
import argparse, json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HASHES = ROOT / "Assets/Art/hashes.json"
APPROVALS = ROOT / "docs/art/asset-approvals.json"
KINDS = ("sprites", "sounds", "tracks")


def load(path):
    return json.loads(path.read_text()) if path.exists() else {}


def states(hashes, approvals):
    """(kind, id, state) for every built asset: approved, changed (differs from its approval) or no record."""
    for kind in KINDS:
        for asset, built in sorted(hashes.get(kind, {}).items()):
            row = approvals.get(kind, {}).get(asset)
            yield kind, asset, "no record" if row is None else "approved" if row["hash"] == built else "changed"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pending", action="store_true")
    ap.add_argument("--record", nargs="+", metavar=("KIND", "ID"))
    ap.add_argument("--date")
    ap.add_argument("--page")
    args = ap.parse_args()
    hashes, approvals = load(HASHES), load(APPROVALS)

    if args.record:
        kind, ids = args.record[0], args.record[1:]
        if kind not in KINDS or not ids or not args.date or not args.page:
            sys.exit("--record needs a kind (sprites, sounds, tracks), at least one id, --date and --page")
        missing = [i for i in ids if i not in hashes.get(kind, {})]
        if missing:
            sys.exit(f"not built ({kind}): {', '.join(missing)}")
        for i in ids:
            approvals.setdefault(kind, {})[i] = {"hash": hashes[kind][i], "date": args.date, "page": args.page}
        ordered = {k: dict(sorted(approvals.get(k, {}).items())) for k in KINDS}
        APPROVALS.write_text(json.dumps(ordered, indent=2) + "\n")
        print(f"recorded {len(ids)} {kind}")
        return

    rows = list(states(hashes, approvals))
    for state in ("approved", "changed", "no record"):
        print(f"{state}: {sum(1 for r in rows if r[2] == state)}")
    if args.pending:
        for kind, asset, state in rows:
            if state != "approved":
                print(f"  {state:9}  {kind[:-1]:6}  {asset}")
    sys.exit(1 if any(r[2] == "changed" for r in rows) else 0)


if __name__ == "__main__":
    main()

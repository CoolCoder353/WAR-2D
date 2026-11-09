#!/usr/bin/env python3
"""Compares two sets of perf runs (A against B) and says which metrics moved by more than the runs' own noise.

Usage:
  tools/perf-compare.py A B                    A and B are PerfResults/<stamp> folders (each run<N>/perf.csv, run<N>/client*/perf.csv),
                                               or several folders each joined with commas: PerfResults/x,PerfResults/y
  tools/perf-compare.py --save NAME A          keeps A's per-run numbers as PerfResults/baselines/NAME.json
  tools/perf-compare.py --baseline NAME B      compares B against a kept baseline

Each metric uses its gated statistic (the budget column's: mean for fps, p95 for tick times, otherwise p95). A side's value is
the median over its runs and its noise is the spread (max - min) over them. A change counts only when the medians differ by
more than both sides' spread and by more than 3 %: runs of one build on one machine moved fps by a third and bandwidth twofold
when unpinned (2026-11-08), so a single-run comparison says nothing. Take A and B side by side, with the same harness
(tools/perf-100k.sh pins host and client; five runs each). Exits 1 when a gated metric got worse past the noise.
"""
import csv, glob, json, os, statistics, sys

HIGHER_IS_BETTER = ("perf.fps", "perf.client.fps", "perf.client.low1")
KEY = ("perf.tick.main", "perf.tick.wait", "perf.client.fps", "perf.client.low1", "perf.client.frame.ms", "perf.bw.avg",
       "perf.bw.peak1s", "perf.server.cpu", "perf.client.cpu", "perf.server.memMB", "perf.client.memMB", "perf.units",
       "perf.client.unitsKnown", "perf.server.errors", "perf.server.drops")
MIN_CHANGE = 0.03
# The load, not a result: a run that kept fewer units did less work, so its numbers don't compare (the 100k match settled at
# about 91,000 or about 100,000 units from run to run before the harness ran its orders on every tick, 2026-11-08).
LOAD = ("perf.units", "perf.client.unitsKnown", "perf.client.unitsOnScreen")
MAX_LOAD_DIFFERENCE = 0.05
MIN_RUNS = 3


def runs_of(spec):
    """Per run, metric -> (value of its gated statistic, budget, stat). Client rows are merged: means averaged, tails the worst."""
    runs = []
    for folder in spec.split(","):
        for run in sorted(glob.glob(os.path.join(folder, "run*"))):
            table = {}
            host = os.path.join(run, "perf.csv")
            if not os.path.exists(host):
                continue
            rows = {r["metric"]: r for r in csv.DictReader(open(host))}
            client_rows = {}
            for c in sorted(glob.glob(os.path.join(run, "client*", "perf.csv"))):
                for r in csv.DictReader(open(c)):
                    client_rows.setdefault(r["metric"], []).append(r)
            for metric, rs in client_rows.items():
                merged = dict(rs[0])
                means = [float(x["mean"]) for x in rs if x["mean"]]
                merged["mean"] = str(statistics.mean(means)) if means else ""
                for col in ("p95", "p99", "max"):
                    vals = [float(x[col]) for x in rs if x[col]]
                    merged[col] = str(max(vals)) if vals else ""
                rows[metric] = merged
            for metric, r in rows.items():
                stat = r.get("budget_stat") or ("mean" if metric in HIGHER_IS_BETTER else "p95")
                if r.get(stat):
                    table[metric] = {"value": float(r[stat]), "stat": stat, "budget": r.get("budget") or ""}
            runs.append(table)
    if not runs:
        sys.exit(f"No runs with a perf.csv under {spec}")
    return runs


def summarise(runs):
    out = {}
    for metric in sorted({m for t in runs for m in t}):
        vals = [t[metric]["value"] for t in runs if metric in t]
        any_row = next(t[metric] for t in runs if metric in t)
        out[metric] = {"median": statistics.median(vals), "min": min(vals), "max": max(vals), "n": len(vals),
                       "stat": any_row["stat"], "budget": any_row["budget"], "values": vals}
    return out


def verdict(metric, a, b):
    if metric in LOAD:
        return "load", (b["median"] - a["median"]) / abs(a["median"]) if a["median"] else 0.0
    if min(a["n"], b["n"]) < MIN_RUNS:
        return "too few runs", (b["median"] - a["median"]) / abs(a["median"]) if a["median"] else 0.0
    if a["median"] == b["median"]:
        return "same", 0.0
    change = (b["median"] - a["median"]) / abs(a["median"]) if a["median"] else float("inf")
    noise = max(a["max"] - a["min"], b["max"] - b["min"])
    if abs(b["median"] - a["median"]) <= noise or abs(change) < MIN_CHANGE:
        return "within noise", change
    better = (change > 0) == (metric in HIGHER_IS_BETTER)
    return ("better" if better else "WORSE"), change


def main(argv):
    if len(argv) >= 3 and argv[0] == "--save":
        summary = summarise(runs_of(argv[2]))
        os.makedirs("PerfResults/baselines", exist_ok=True)
        path = os.path.join("PerfResults/baselines", argv[1] + ".json")
        json.dump({"source": argv[2], "metrics": summary}, open(path, "w"), indent=1)
        print(f"Saved {len(summary)} metrics from {summary[next(iter(summary))]['n']} runs as {path}")
        return 0
    if len(argv) >= 3 and argv[0] == "--baseline":
        saved = json.load(open(os.path.join("PerfResults/baselines", argv[1] + ".json")))
        a, a_name = saved["metrics"], f"baseline {argv[1]}"
        b, b_name = summarise(runs_of(argv[2])), argv[2]
    elif len(argv) == 2:
        a, a_name = summarise(runs_of(argv[0])), argv[0]
        b, b_name = summarise(runs_of(argv[1])), argv[1]
    else:
        print(__doc__)
        return 2

    print(f"A: {a_name} ({next(iter(a.values()))['n']} runs)   B: {b_name} ({next(iter(b.values()))['n']} runs)")
    print(f"{'metric':26} {'stat':5} {'A median':>12} {'A range':>23} {'B median':>12} {'B range':>23} {'change':>8}  verdict")
    worse = []
    shown = [m for m in KEY if m in a and m in b] + [m for m in sorted(a) if m in b and m not in KEY and not m.startswith("perf.split.")]
    for metric in shown:
        va, vb = a[metric], b[metric]
        word, change = verdict(metric, va, vb)
        budget = f"  (budget {va['budget']})" if va["budget"] else ""
        rng = lambda v: f"{v['min']:.1f}..{v['max']:.1f}"
        print(f"{metric:26} {va['stat']:5} {va['median']:12.2f} {rng(va):>23} {vb['median']:12.2f} {rng(vb):>23} {change:+8.1%}  {word}{budget}")
        if word == "WORSE" and (va["budget"] or metric in KEY):
            worse.append(metric)
    print()
    load_a, load_b = a.get("perf.units", {}).get("median"), b.get("perf.units", {}).get("median")
    if load_a and load_b and abs(load_b - load_a) / load_a > MAX_LOAD_DIFFERENCE:
        print(f"NOT COMPARABLE: A ran {load_a:,.0f} units and B {load_b:,.0f} (more than {MAX_LOAD_DIFFERENCE:.0%} apart): the loads differ.")
        return 2
    if min(next(iter(a.values()))["n"], next(iter(b.values()))["n"]) < MIN_RUNS:
        print(f"Too few runs for a verdict: take at least {MIN_RUNS} a side (five is better).")
        return 2
    if worse:
        print("Worse past the noise: " + ", ".join(worse))
        return 1
    print("Nothing gated got worse past the noise.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

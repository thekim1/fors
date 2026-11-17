#!/usr/bin/env python3
"""Several runs of run.sh side by side: per platform, each key figure for every run, its mean and its spread (max - min),
so it shows whether a result holds up or was one run's luck. Reads <dir>/run-*/ (the files run.sh writes in results/).

    python3 summarize_runs.py results-150 > results-150/summary.md
"""

import json
import pathlib
import statistics
import sys

ROOT = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "results-runs")
RUNS = sorted(p for p in ROOT.glob("run-*") if p.is_dir())
NAMES = {"eneo": "eneo v2.3.1", "rag": "RAG platform + gateway"}
if not RUNS:
    sys.exit(f"no run-* folders in {ROOT}")


def load(path: pathlib.Path):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError):
        return None


def cpu(run: pathlib.Path, platform: str) -> tuple[float, float]:
    """Mean and peak CPU of the whole platform (vCPUs), from docker stats every 2 s."""
    totals, current, seen = [], 0.0, set()
    path = run / f"{platform}-stats.txt"
    if not path.exists():
        return 0.0, 0.0
    for line in path.read_text().splitlines():
        parts = line.split(",", 2)
        if len(parts) < 2:
            continue
        if parts[0] in seen:
            totals.append(current)
            current, seen = 0.0, set()
        seen.add(parts[0])
        current += float(parts[1].rstrip("%") or 0) / 100
    if seen:
        totals.append(current)
    return (statistics.mean(totals), max(totals)) if totals else (0.0, 0.0)


def figures(run: pathlib.Path, platform: str) -> dict[str, float] | None:
    result = load(run / f"{platform}.json")
    if result is None:
        return None
    h = result["hold"]
    llm = load(run / f"{platform}-llm.json") or []
    chat = next((x for x in llm if x["kind"] == "chat"), {})
    mean_cpu, peak_cpu = cpu(run, platform)
    return {
        "Answers per minute": h["completed_per_min"],
        "Errors": h["errors"],
        "First token p50 (s)": h["ttft_ms"].get("p50", 0) / 1000,
        "First token p95 (s)": h["ttft_ms"].get("p95", 0) / 1000,
        "First token p99 (s)": h["ttft_ms"].get("p99", 0) / 1000,
        "Response starts p95 (s)": h["headers_ms"].get("p95", 0) / 1000,
        "Longest pause p95 (s)": h["max_gap_ms_per_stream"].get("p95", 0) / 1000,
        "Whole answer p95 (s)": h["stream_ms"].get("p95", 0) / 1000,
        "Prompt tokens p50": chat.get("tokens_p50", 0),
        "CPU mean (vCPU of 4)": mean_cpu,
        "CPU peak (vCPU of 4)": peak_cpu,
    }


first = load(RUNS[0] / "eneo.json") or load(RUNS[0] / "rag.json") or {}
print(f"# {len(RUNS)} runs, {first.get('vus')} users, {first.get('hold_s')} s measured each\n")
for platform in ("eneo", "rag"):
    rows = [(run.name, figures(run, platform)) for run in RUNS]
    rows = [(name, f) for name, f in rows if f]
    if not rows:
        continue
    print(f"## {NAMES[platform]}\n")
    header = ["Figure", *[name for name, _ in rows], "Mean", "Spread"]
    print("| " + " | ".join(header) + " |")
    print("|" + "|".join("---" for _ in header) + "|")
    for key in rows[0][1]:
        values = [f[key] for _, f in rows]
        fmt = (lambda v: f"{v:,.0f}") if key in ("Answers per minute", "Errors", "Prompt tokens p50") else (lambda v: f"{v:.2f}")
        print("| " + " | ".join([key, *map(fmt, values), fmt(statistics.mean(values)), fmt(max(values) - min(values))]) + " |")
    print()

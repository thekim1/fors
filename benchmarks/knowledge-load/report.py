#!/usr/bin/env python3
"""The knowledge load test's report (run.sh): results/<platform>.json (the load client), <platform>-llm.json (what the
fake LLM received), <platform>-stats.txt (docker stats every 2 s), <platform>-pg.txt (Postgres connections),
<platform>-chunks.txt and <platform>-pg-errors.txt, as one Markdown table per topic. Standard library only."""

import json
import os
import pathlib
import sys

RESULTS = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "results")
NAMES = {"eneo": "eneo v2.3.1", "rag": "RAG platform + gateway"}
platforms = [p for p in ("eneo", "rag") if (RESULTS / f"{p}.json").exists()]
if not platforms:
    sys.exit(f"no results in {RESULTS}")


def load(path: pathlib.Path, default=None):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError):
        return default


def ms(stats: dict, key: str) -> str:
    value = stats.get(key) if stats else None
    if value is None:
        return "–"
    return f"{value / 1000:.2f} s" if value >= 1000 else f"{value:.0f} ms"


def table(header: list[str], rows: list[list[str]]) -> str:
    lines = ["| " + " | ".join(header) + " |", "|" + "|".join("---" for _ in header) + "|"]
    lines += ["| " + " | ".join(r) + " |" for r in rows]
    return "\n".join(lines)


def cpu_mem(platform: str):
    """Per container: mean and peak CPU (in vCPUs) and peak memory (GiB); the platform's total per sample."""
    per: dict[str, dict[str, list[float]]] = {}
    totals_cpu: list[float] = []
    totals_mem: list[float] = []
    current_cpu = current_mem = 0.0
    seen: set[str] = set()
    path = RESULTS / f"{platform}-stats.txt"
    if not path.exists():
        return per, totals_cpu, totals_mem
    for line in path.read_text().splitlines():
        try:
            name, cpu, mem = line.split(",", 2)
        except ValueError:
            continue
        name = name.removeprefix("knowledge-load-").removesuffix("-1")
        if name in seen:  # a new sample begins
            totals_cpu.append(current_cpu)
            totals_mem.append(current_mem)
            current_cpu = current_mem = 0.0
            seen.clear()
        seen.add(name)
        vcpu = float(cpu.rstrip("%") or 0) / 100
        used = mem.split("/")[0].strip()
        gib = float(used[:-3]) / (1024 if used.endswith("MiB") else 1) if used[-3:] in ("MiB", "GiB") else 0.0
        if used.endswith("KiB"):
            gib = float(used[:-3]) / 1024 / 1024
        per.setdefault(name, {"cpu": [], "mem": []})
        per[name]["cpu"].append(vcpu)
        per[name]["mem"].append(gib)
        current_cpu += vcpu
        current_mem += gib
    if seen:
        totals_cpu.append(current_cpu)
        totals_mem.append(current_mem)
    return per, totals_cpu, totals_mem


def pg(platform: str):
    rows = []
    path = RESULTS / f"{platform}-pg.txt"
    if path.exists():
        for line in path.read_text().splitlines():
            parts = line.split("|")
            if len(parts) == 2 and all(p.strip().isdigit() for p in parts):
                rows.append((int(parts[0]), int(parts[1])))
    return rows


out = ["# Knowledge load test: eneo vs. the RAG platform + gateway", ""]
first = load(RESULTS / f"{platforms[0]}.json", {})
out.append(
    f"{first.get('vus')} staff chatting back to back (no time to read), {first.get('ramp_s')} s ramp, then "
    f"{first.get('hold_s')} s measured; {round(first.get('followup_share', 0) * 100)} % follow-ups in the same conversation. "
    "Requests started in the hold are measured to the end of their stream. Fake LLM as glm-5.3-flash (see README.md); "
    "the platform under test on 4 vCPUs and 18 GB."
)
out.append("")

# What the user experiences.
rows = []
for p in platforms:
    h = load(RESULTS / f"{p}.json", {}).get("hold", {})
    rows.append([
        NAMES[p], str(h.get("requests", 0)), f"{h.get('completed_per_min', 0):.0f}", str(h.get("errors", 0)),
        ms(h.get("ttft_ms"), "p50"), ms(h.get("ttft_ms"), "p95"), ms(h.get("ttft_ms"), "p99"),
        ms(h.get("gap_ms"), "p50"), ms(h.get("gap_ms"), "p99"), ms(h.get("max_gap_ms_per_stream"), "p95"),
        ms(h.get("stream_ms"), "p50"), ms(h.get("stream_ms"), "p95"),
    ])
out += ["## What staff experience", "", table(
    ["Platform", "Requests", "Answers/min", "Errors", "First token p50", "p95", "p99", "Gap p50", "Gap p99",
     "Longest stall p95", "Whole answer p50", "p95"], rows), ""]

errors = [(p, load(RESULTS / f"{p}.json", {}).get("hold", {})) for p in platforms]
if any(h.get("errors") for _, h in errors):
    out += ["Errors:", ""]
    for p, h in errors:
        if h.get("errors"):
            out.append(f"- {NAMES[p]}: {json.dumps(h.get('error_kinds'), ensure_ascii=False)}; first: {h.get('first_errors')}")
    out.append("")

# New conversations vs follow-ups.
rows = []
for p in platforms:
    h = load(RESULTS / f"{p}.json", {}).get("hold", {})
    for kind, label in (("new_conversations", "new"), ("follow_ups", "follow-up")):
        part = h.get(kind, {})
        rows.append([NAMES[p], label, str(part.get("requests", 0)), ms(part.get("ttft_ms"), "p50"), ms(part.get("ttft_ms"), "p95"),
                     ms(part.get("stream_ms"), "p95")])
out += ["## New conversations and follow-ups", "", table(["Platform", "Message", "Requests", "First token p50", "p95", "Whole answer p95"], rows), ""]

# What the model received: the prompt decides how long a real model reads before the first token.
rows = []
prefill = float(os.environ.get("GLM_PREFILL_MS_PER_1K", "60"))
ttft = float(os.environ.get("GLM_TTFT_MS", "400"))
for p in platforms:
    llm = load(RESULTS / f"{p}-llm.json", [])
    chat = next((x for x in llm if x["kind"] == "chat"), {})
    emb = next((x for x in llm if x["kind"] == "embeddings"), {})
    h = load(RESULTS / f"{p}.json", {}).get("hold", {})
    chunks = (RESULTS / f"{p}-chunks.txt").read_text().strip() if (RESULTS / f"{p}-chunks.txt").exists() else "?"
    model_ms = ttft + prefill * chat.get("tokens_p50", 0) / 1000
    overhead = (h.get("ttft_ms") or {}).get("p50")
    rows.append([
        NAMES[p], chunks, str(chat.get("calls", 0)), f"{chat.get('tokens_p50', 0):,}", f"{chat.get('tokens_p95', 0):,}",
        f"{chat.get('tokens_max', 0):,}", str(emb.get("calls", 0)), f"{model_ms:.0f} ms",
        f"{overhead - model_ms:.0f} ms" if overhead is not None else "–",
    ])
out += ["## What the model received", "", table(
    ["Platform", "Chunks in the collection", "Model calls", "Prompt tokens p50", "p95", "max", "Embedding calls",
     "Model's own first token (p50 prompt)", "Platform's share of first token p50"], rows), "",
    f"The fake LLM waits {ttft:.0f} ms plus {prefill:.0f} ms per 1 000 prompt tokens before the first token, as a real "
    "model reads its prompt; the platform's share is the measured first token minus that.", ""]

# Resources of the 4-vCPU, 18 GB server.
for p in platforms:
    per, cpu_totals, mem_totals = cpu_mem(p)
    connections = pg(p)
    rows = [[name, f"{sum(v['cpu']) / len(v['cpu']):.2f}", f"{max(v['cpu']):.2f}", f"{max(v['mem']):.2f}"]
            for name, v in sorted(per.items()) if v["cpu"]]
    if cpu_totals:
        rows.append(["**total**", f"**{sum(cpu_totals) / len(cpu_totals):.2f}**", f"**{max(cpu_totals):.2f}**",
                     f"**{max(mem_totals):.2f}**"])
    out += [f"## Resources: {NAMES[p]} (of 4 vCPU, 18 GB)", "", table(["Container", "CPU mean (vCPU)", "CPU peak", "Memory peak (GiB)"], rows), ""]
    if connections:
        out.append(f"Postgres connections: peak {max(c[0] for c in connections)}, of them active at most {max(c[1] for c in connections)}.")
    errs = (RESULTS / f"{p}-pg-errors.txt").read_text().split("\n") if (RESULTS / f"{p}-pg-errors.txt").exists() else []
    bad = [e for e in errs if e and not e.endswith(" 0")]
    out.append("Connection-pool or database refusals in the logs: " + (", ".join(bad) if bad else "none") + ".")
    out.append("")

print("\n".join(out))

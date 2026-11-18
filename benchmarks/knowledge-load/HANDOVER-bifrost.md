# Handover: benchmark Bifrost against the Ume LLM gateway

**Goal:** find out how well [Bifrost](https://github.com/maximhq/bifrost) performs **as an LLM gateway compared with the
Ume LLM gateway**. The RAG platform stays connected to the Ume gateway and **isn't** part of this work.

**How:** reuse the gateway comparison harness that's already in the repo, `benchmarks/compare/` on `main`. Today it
compares the Ume gateway with Eneo against the same fake LLM. Replace Eneo with Bifrost there, so it compares three
targets:
- `ume`: the Ume gateway;
- `bifrost`;
- `fake`: requests straight to the fake LLM, as the floor.

Work on a new branch from `main` (e.g. `bench/bifrost-vs-gateway`), never on `main` itself. Push the branch when
results exist and ask before opening a PR.

## What's in the harness (read `benchmarks/compare/README.md` first)

| Script | Measures | Run for Bifrost? |
|---|---|---|
| `run.sh` | Added latency and throughput at 1, 16 and 64 clients, streamed and not, CPU and DB/Redis calls per message | **Yes** |
| `scale.sh` | Throughput as load grows and as vCPUs go 1 to 6 (streaming) | **Yes** |
| `latency.sh` | Streaming with real-provider timing (first token, gaps between tokens, stalls) via the stream probe | **Yes** |
| `sizing/run.sh` | Is a small host enough (fixed request rates, own compose) | **Yes** |
| `scale-out.sh` | Several instances on shared state | Optional, if Bifrost supports several instances sharing its stores |
| `gc.sh`, `cores.sh`, `profile.sh` | .NET internals of the Ume gateway | No |

Supporting files:
- `lib.sh`: starts and seeds each platform, the CPU layout, CPU and query sampling.
- `compose.yaml`: has profiles `ume` and `eneo`.
- `k6/chat.js`: `TARGET=ume|eneo|fake`.
- `report.py`, `scale_report.py`, `latency_report.py`.

The Ume gateway's own results and how to read them are in `docs/performance.md`:
- *Results (baseline 2026-11-02)*;
- the `compare/run.sh`, `scale.sh`, `latency.sh` and `sizing` sections;
- *eneo v2.2.3*, which is the section to mirror for Bifrost.

CPU layout (`lib.sh`):
- fake LLM on threads 0–3;
- k6 and the stream probe on 4–7;
- the platform on 8–11 (`APP_CPUS`);
- Postgres on 12–13;
- Redis on 14–15.

## What to change

1. **Bifrost image:** the official one, **pinned to a release tag** (never `latest`). Record the version in every
   report.
2. **compose.yaml:** a `bifrost` profile, on `APP_CPUS` like the other platforms. If Bifrost uses Postgres or Redis
   for its config, log or governance store, run them on the Postgres and Redis cpusets, as the Ume gateway does.
   Set `GOMAXPROCS` to match the vCPUs given; `scale.sh` varies them.
3. **lib.sh:** `bifrost` cases in the start/seed function, `db_of`, `redis_of`, the container lists and `stop_all`.
   - Seed Bifrost with an OpenAI-compatible provider whose base URL is `http://fake-llm:8080/v1`.
   - Add a model the k6 script asks for.
   - Add one virtual key if governance is on.
4. **k6/chat.js (and sizing/k6/mix.js):** `TARGET=bifrost`. Keep the same body as `ume`: the same Swedish question,
   same streaming switch, auth header as Bifrost expects.
5. **The stream probe** (`StreamProbe.cs`, used by `latency.sh`) and the report scripts: add the Bifrost target.
6. **Keep the comparison fair.** The Ume gateway does key auth, rate limits, key/team/department budgets, routing and
   usage records on every request. Run Bifrost **with its equivalents on** (virtual keys/governance, rate limits or
   budgets, request logging). Optionally do one extra run with them off to show the bare proxy. State in every report
   which Bifrost features and stores were on.
7. **Logging:** production defaults on both (the Ume gateway logs at Information).
8. **Remove Eneo from the defaults** (`PLATFORMS="fake ume bifrost"`) but keep its profile working, so the earlier Eneo
   results can still be reproduced.

## Rules

- Every claim about Bifrost (features, defaults, how it stores state) must be **confirmed in its code at the pinned
  tag**, never only in docs or on its website. Name the file in the report.
- Don't change the Ume gateway's code to make it look better or worse. If a harness change is needed for Bifrost, keep the
  `ume` path identical, and rerun `ume` in the same session on the same machine so the numbers are comparable.
- Each report ends with the harness check (fake LLM, Postgres, Redis, k6 not at their limit). Keep that for Bifrost.

## Deliverables

1. Bifrost wired into `benchmarks/compare` as a third target.
2. Runs: `run.sh`, `scale.sh`, `latency.sh` and `sizing/run.sh` for `fake ume bifrost`, on the same machine and in the
   same session.
3. A report comparing the two gateways:
   - added latency over the fake floor (p50/p95/p99);
   - throughput per vCPU;
   - CPU and memory per message;
   - the scaling curve;
   - streaming gaps and stalls;
   - small-host results;
   - errors;
   - the features that were on;
   - caveats.
4. A shareable HTML summary, like `benchmarks/knowledge-load/summary-2026-11-17.html` on branch
   `bench/eneo-vs-rag-knowledge-load`. Push it to the new branch, and offer to publish it.

## Background (the earlier round, for reference only)

The knowledge load test (Eneo vs. RAG platform + Ume gateway) is on branch `bench/eneo-vs-rag-knowledge-load` in
`benchmarks/knowledge-load/`. At 1 000 users, the Ume gateway used 1.33 vCPU on average and peaked at 0.33 GiB, with
0 errors. That test isn't repeated here.

Practical gotchas from earlier runs on this machine:
- WSL2 has about 15.4 GB for Docker.
- Reach containers through Docker's published ports, not background localhost servers.
- The fake LLM's `/stats` endpoint shows what reached the model.

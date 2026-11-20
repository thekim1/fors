# Bifrost v2.5.1 vs. Fors (2026-11-19)

How well [Bifrost](https://github.com/maximhq/bifrost) performs **as an LLM gateway** compared with Fors,
on the same fake LLM, the same machine and in the same session. Harness: this folder (`run.sh`, `scale.sh`, `latency.sh`,
`sizing/run.sh`), with Bifrost as the third target next to `fors` and the fake-LLM floor (see [README](README.md#bifrost)). A one-page
summary with charts: [summary-bifrost-2026-11-19.html](summary-bifrost-2026-11-19.html).

## Summary

| Chat message, 4 vCPUs each | Fors | Bifrost v2.5.1 | Ratio |
|---|---:|---:|---:|
| Added latency, 1 client, non-streaming (median) | **0.46 ms** | 0.62 ms | 1.3× |
| Added latency, 1 client, streaming, whole stream (median) | **0.68 ms** | 1.06 ms | 1.6× |
| Added latency, 64 clients, streaming (median) | **3.7 ms** | 27.9 ms | 7.6× |
| p99 at 64 clients, streaming | **9.9 ms** | 85.4 ms | 8.6× |
| Peak throughput, streaming (`scale.sh`) | **14 369 msg/s** | 2 077 msg/s | 6.9× |
| Peak throughput, non-streaming (64 clients, `run.sh`) | **17 714 msg/s** | 4 316 msg/s | 4.1× |
| Throughput per vCPU, streaming (1 vCPU peak) | **5 443 msg/s** | 663 msg/s | 8.2× |
| CPU per message, streaming (64 clients) | **0.30 ms** | 2.07 ms | 6.9× |
| CPU per message, non-streaming (64 clients) | **0.20 ms** | 0.93 ms | 4.7× |
| Its database (+ Redis) CPU per message, streaming (64 clients) | **0.06 ms** | 0.41 ms | 6.8× |
| Memory, platform process (`run.sh`) | **108–198 MB** | 224–510 MB | ~2.5× |
| Per-message record in Postgres | **553 bytes** (usage record) | 4 978 bytes (request log with content) | 9× |
| Messages recorded, non-streaming under load | **100 %** | 60–67 % (the rest dropped) | |
| Errors (all runs, all loads) | 0 | 0 (except the small host, below) | |
| Small host (0.5 vCPU, 512 MB), 100 msg/s of realistic streams | **0 failed of 12 999**, ≤ 200 MB | OOM-killed, 88.5 % failed | |

- **Fors does the same governance work at a fraction of the cost.** With its virtual key, rate limits,
  key/team/department budgets, routing with fallback and usage records on every request, it served 4–7× Bifrost's
  throughput on the same four vCPUs with its equivalents on, at 5–7× less CPU per message.
- **Bifrost's limit is its own process**, not its stores or the harness: at its plateau it used 4.0 of 4 vCPUs while its
  Postgres used under 1 of 2 threads and the fake LLM under 1 of 4.
- **Most of the difference is Bifrost's core proxy path.** Even as a bare proxy (no stores, no key, no request log)
  Bifrost needs 2.7× (non-streaming) and 4.7× (streaming) the CPU per message of Fors *with* all its
  features on. Governance and logging cost Bifrost a further ~40 % of its throughput; logging message content costs
  almost nothing extra.
- **Bifrost loses request logs under load.** Its log writer drops an entry when its 10 000-entry queue is full, without
  slowing the request. At 16 and 64 non-streamed clients it dropped 34 % and 39 % of entries (its own counter), so
  cost and usage reporting from its logs is incomplete exactly when traffic is high. Fors recorded every
  message in every run.
- **With a real model's timing nobody notices either gateway.** At 1–256 users with 400 ms to the first token and
  ~5.6 s streams, both stayed within the model's own jitter, with no errors and no stalls. The difference then is
  hardware: 8.0 against 14.9 ms of CPU per 200-word stream at 256 users.
- **Bifrost needs a bigger small host.** On the gateway's small-host budget (0.5 vCPU, 512 MB) Bifrost managed 10 and
  50 msg/s, but at 50 it was throttled in 93 % of CPU periods and reached 482 MB; at 100 msg/s it was killed for running
  out of memory. Fors handled 100 msg/s on the same budget at 0.47 vCPU and ≤ 200 MB with no failed checks.
  Given 1 vCPU and 1 GB, Bifrost served 100 msg/s without failures, using 0.94 vCPU and up to 616 MB: about twice the
  CPU and three times the memory Fors needs for the same traffic.

## Set-up

- **Machine:** Intel Core i7-13700H laptop (6 performance cores with two threads each + 8 efficiency cores; WSL2 shows
  20 threads as 10 × 2), 15 GB for Docker, Docker 29.8.2, kernel 6.6.87.2-microsoft-standard-WSL2. Two idle containers
  of another project (Azurite, SQL Server, unpinned, about 2 % of one core) ran throughout.
- **CPU layout** (`lib.sh`, unchanged): platform 8–11 (4 vCPUs; `scale.sh` 1–6), Postgres 12–13, Redis 14–15, fake LLM
  0–3, k6 and stream probe 4–7. On a hybrid CPU under Hyper-V the thread pairs are virtual, so the absolute numbers are
  this machine's: Fors peaks at 14 369 streamed msg/s here against 21 097 on the 8-core machine of the
  2026-11-02 baseline, and scales less linearly (66 % efficiency at 4 vCPUs here, 95 % there). Both gateways ran on the
  same layout in the same session, so the comparison holds; compare ratios, not absolute figures, with the baseline.
- **Versions:** Fors at `d128f57` (release 0.11.0), .NET 10, image built with `dotnet publish
  /t:PublishContainer`. Bifrost `maximhq/bifrost:v2.5.1` (`sha256:5ec9873acaf1687556b6e4dd854b92b539d9debf87a5a683b66338648b84d3c8`),
  source tag `transports/v2.5.1` (`7cb75fb2`). Postgres 17-alpine, Redis 7.4-alpine, k6 1.3.0. Harness commit
  `6314ff5` for the main session; the bare-proxy rerun and the follow-ups used the later commits on this branch, which
  change only the bare variant's key and add the drop-counter token and the sizing overrides.
- **Order:** `run.sh` (fake, fors, bifrost), `run.sh` for two Bifrost variants, `scale.sh`, `latency.sh`, `sizing/run.sh`,
  then follow-ups (bare-proxy rerun, log drop counter, Bifrost sizing at 1 vCPU / 1 GB), 2026-11-18 23:08 to
  2026-11-19 01:00 UTC. Raw results: `benchmarks/compare/results/2026-11-19/` on the machine that ran them.

### What was on

| | Fors | Bifrost v2.5.1 (`bifrost/config.json`) |
|---|---|---|
| Authentication | Virtual key (hashed, cached) | Virtual key `sk-bf-…` (mandatory by default: `EnforceAuthOnInference: true`, `transports/bifrost-http/lib/config.go`) |
| Rate limit | Requests and tokens per minute (Redis) | Requests and tokens per minute (in process, `plugins/governance/store.go`) |
| Budgets | Key, team, department, monthly | Key, team, customer, monthly (in process, charged after the response, `plugins/governance/main.go`, `tracker.go`) |
| Routing | Alias → primary, fallback | Key's weighted providers: `openai` weight 1, `fallback` weight 0 as fallback (`plugins/governance/main.go`, `LoadBalanceProvider`) |
| Prices | $2.50 / $10 per M tokens | Same, as a pricing override (budgets are only charged when a request has a cost, `plugins/governance/tracker.go`) |
| Per-message record | Usage record (metadata) in Postgres, batched every 50 ms | Request log row **with message content** (the default, `client.disable_content_logging` false) in Postgres, batched up to 1 000 rows / 5 s (`plugins/logging/writer.go`) |
| Counters persisted | Redis (shared by all instances) | Config store every 10 s (`plugins/governance/tracker.go`); not shared between instances |
| Telemetry | OpenTelemetry | Prometheus telemetry plugin (on by default, `transports/bifrost-http/server/plugins.go`) |
| Logging | Information, one line per request | `info`, JSON, one access line per request (the image's default) |
| Stores | Postgres 17, Redis 7.4 | Postgres 17 (config and logs); no Redis |
| Threads | .NET thread pool over the CPU set | `GOMAXPROCS` = vCPUs (set explicitly: automaxprocs v1.6.0 honours it, `maxprocs/maxprocs.go`) |
| Not on | PII detection and content guard (sizing test only) | No counterpart in the open-source build (`guardrails_config` not read by `transports/bifrost-http`) |

Extra Bifrost runs (`run.sh`): **no content** (`config-no-content.json`: as above, request log without message content)
and **bare proxy** (`config-bare.json`: no stores, no request log, no virtual key).

## Added latency and throughput (`run.sh`, 4 vCPUs, 30 s per scenario)

Overhead = median minus the fake LLM's own median at the same load. 0 failed checks in every scenario.

| | 1 client | 16 clients | 64 clients |
|---|---:|---:|---:|
| Non-streaming, msg/s: Fors / Bifrost | 1 390 / 1 094 | 9 665 / 4 365 | **17 714** / 4 316 |
| Non-streaming, overhead (median): Fors / Bifrost | **0.46** / 0.62 ms | **1.29** / 2.80 ms | **2.45** / 11.8 ms |
| Non-streaming, p95 / p99: Fors | 0.78 / 1.29 ms | 2.46 / 3.55 ms | 5.79 / 7.96 ms |
| Non-streaming, p95 / p99: Bifrost | 1.08 / 1.51 ms | 7.61 / 12.2 ms | 34.2 / 45.3 ms |
| Streaming, msg/s: Fors / Bifrost | 1 023 / 694 | 6 674 / 2 033 | **11 520** / 1 925 |
| Streaming, overhead (median, whole stream): Fors / Bifrost | **0.68** / 1.06 ms | **1.82** / 6.51 ms | **3.66** / 27.9 ms |
| Streaming, p95 / p99: Fors | 1.20 / 1.53 ms | 3.34 / 5.34 ms | 8.06 / 9.91 ms |
| Streaming, p95 / p99: Bifrost | 1.73 / 2.54 ms | 15.3 / 24.3 ms | 64.6 / 85.4 ms |
| Streaming, time to first byte (median): Fors / Bifrost | 0.64 / 0.78 ms | 1.62 / 5.99 ms | 3.75 / 27.9 ms |
| Platform CPU per message, non-streaming: Fors / Bifrost | 1.01 / 1.07 ms¹ | **0.31** / 0.92 ms | **0.20** / 0.93 ms |
| Platform CPU per message, streaming: Fors / Bifrost | 1.48 / 2.26 ms¹ | **0.44** / 1.96 ms | **0.30** / 2.07 ms |
| Database (+ Redis) CPU per message, streaming: Fors / Bifrost | 0.19 / 0.65 ms¹ | **0.08** / 0.33 ms | **0.06** / 0.41 ms |
| Records stored per message, non-streaming: Fors / Bifrost | 1.00 / 1.00 | 1.00 / **0.67** | 1.00 / **0.59** |
| Records stored per message, streaming: Fors / Bifrost | 1.00 / 1.00 | 1.00 / 1.00 | 1.00 / 1.00 |
| Platform memory: Fors / Bifrost | 108–122 / 224–316 MB | 122–141 / 402–406 MB | 175–198 / 509–510 MB |

¹ With one client, idle background work is spread over few messages; 16 and 64 clients show the marginal cost.

Postgres statements per message are below 0.1 for both (both batch their writes). Fors sends 21 Redis
commands per message (including those inside Lua scripts); Bifrost has no Redis and keeps its counters in memory.

**Bifrost variants**, same harness, msg/s and platform CPU per message at 64 clients:

| Bifrost configuration | Non-streaming msg/s | CPU ms/msg | Streaming msg/s | CPU ms/msg | Records/msg (non-streaming, 16 / 64) |
|---|---:|---:|---:|---:|---:|
| Governance + request log with content (default; the comparison) | 4 316 | 0.93 | 1 925 | 2.07 | 0.67 / 0.59 |
| Governance + request log without content | 4 383 | 0.91 | 2 001 | 1.99 | 0.83 / 0.78 |
| Bare proxy (no stores, no key, no log) | 7 384 | 0.54 | 2 813 | 1.41 | – |
| *Fors, all features on* | *17 714* | *0.20* | *11 520* | *0.30* | *1.00 / 1.00* |

### Dropped request logs

Bifrost queues each request log for an asynchronous writer. When the queue (10 000 entries) is full, the entry is
dropped and a warning written (`log write queue full, dropping log entry …`, `plugins/logging/writer.go`), and the
request is not slowed down. A separate run with Bifrost's admin API open (`BIFROST_SETUP_TOKEN`, `GET /api/logs/dropped`),
non-streaming, 30 s after a 10 s warm-up:

| Clients | Requests | Log rows written | Dropped (Bifrost's counter) | Dropped |
|---:|---:|---:|---:|---:|
| 16 | 129 637 | 86 000 | 43 637 | 34 % |
| 64 | 128 912 | 78 000 | 50 912 | 39 % |

Written plus dropped is exactly the number of requests. Streamed requests were all logged in these runs (at lower
request rates); turning content logging off lowers the loss but does not remove it. Bifrost's budgets and rate limits
are charged separately from the log (in memory), so enforcement does not depend on the log; usage and cost reporting
from the log does. Fors's usage records are written in batches that wait rather than drop, 1.00 per message
in every run.

## Scaling (`scale.sh`, streaming, 20 s per point)

| Concurrent clients (4 vCPUs) | 1 | 2 | 4 | 8 | 16 | 32 | 64 | 128 | 256 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Fors, msg/s | 1 034 | 1 859 | 3 100 | 4 594 | 6 786 | 9 318 | 11 423 | 13 727 | **14 653** |
| Fors, median / p99 ms | 0.85 / 1.6 | 0.94 / 1.8 | 1.1 / 2.3 | 1.6 / 3.2 | 2.2 / 4.0 | 3.2 / 6.0 | 5.3 / 10.3 | 8.8 / 18.0 | 16.5 / 33.7 |
| Bifrost, msg/s | 714 | 1 259 | 1 786 | 1 982 | 2 022 | **2 066** | 2 012 | 1 972 | 1 896 |
| Bifrost, median / p99 ms | 1.2 / 2.7 | 1.3 / 3.2 | 1.9 / 4.8 | 3.4 / 13.6 | 6.9 / 24.4 | 13.7 / 40.8 | 28.5 / 81.9 | 58.2 / 162 | 120 / 374 |
| Fake LLM directly, msg/s (harness ceiling) | 4 154 | 7 686 | 15 040 | 25 449 | 29 855 | 31 765 | 31 851 | 30 868 | 31 694 |

No errors for either at any load. Bifrost is at its limit from 8 clients: its throughput stays flat and the extra
clients queue, so its median grows linearly with load (120 ms at 256 clients). Fors keeps gaining up to 256.

| vCPUs (peak, fresh stack each) | 1 | 2 | 4 | 6 |
|---|---:|---:|---:|---:|
| Fors, peak msg/s | 5 443 | 9 899 | 14 369 | **15 135** |
| Fors, speed-up (efficiency) | 1.00× | 1.82× (91 %) | 2.64× (66 %) | 2.78× (46 %) |
| Fors, CPU per message | 0.18 ms | 0.19 ms | 0.26 ms | 0.34 ms |
| Bifrost, peak msg/s | 663 | 1 223 | 2 077 | 2 594 |
| Bifrost, speed-up (efficiency) | 1.00× | 1.84× (92 %) | 3.13× (78 %) | 3.91× (65 %) |
| Bifrost, CPU per message | 1.51 ms | 1.63 ms | 1.91 ms | 2.31 ms |
| Fors ÷ Bifrost | 8.2× | 8.1× | 6.9× | 5.8× |

Bifrost scales somewhat more linearly than Fors on this machine, but from a base eight times lower; Fors
gateway on one vCPU (5 443 msg/s) serves more than Bifrost on six (2 594). Both gateways' CPU per message rises with
more vCPUs on this hybrid CPU (the 2026-11-02 baseline machine kept Fors at 0.17–0.22 ms).

## Streaming with real-provider timing (`latency.sh`)

400 ms to the first token, 200 words at 25 ms ±30 % (about 5.6 s per answer), measured with the stream probe, 30 s per
load. TTFT = time to first answer text; "added" = platform median minus the fake LLM's own median at the same load.

| Clients | Streams/s | TTFT p50 / p95: direct | Fors | Bifrost | Gap p99: direct / Fors / Bifrost | Whole stream p50: direct / Fors / Bifrost | CPU per stream: Fors / Bifrost | Errors |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.2 | 397 / 540 ms | 323 / 470 ms | 492 / 524 ms | 36.6 / 36.0 / 35.1 ms | 5.61 / 5.60 / 5.65 s | –¹ | 0 |
| 16 | 2.8 | 411 / 517 ms | 427 / 518 ms | 399 / 519 ms | 35.9 / 34.0 / 34.5 ms | 5.64 / 5.61 / 5.58 s | 67 / 62 ms¹ | 0 |
| 64 | 11.2 | 415 / 520 ms | 424 / 520 ms | 417 / 515 ms | 33.1 / 33.1 / 33.0 ms | 5.60 / 5.60 / 5.60 s | 19.1 / 25.5 ms | 0 |
| 256 | 44.7 | 420 / 600 ms | 415 / 537 ms | 416 / 537 ms | 33.1 / 33.1 / 33.1 ms | 5.60 / 5.60 / 5.60 s | **8.0** / 14.9 ms | 0 |

¹ At low rates the CPU per stream is mostly idle background work spread over few streams.

The first token's ±30 % jitter is larger than anything either gateway adds (the differences go both ways); no gap of
250 ms or more for either gateway at any load, and the pace and length of the stream are unchanged. At this rate
neither gateway is near its limit, so the difference is only CPU: Bifrost needs about 1.9× Fors's CPU per
realistic stream at 256 users.

## A small host (`sizing/run.sh`)

The gateway's small-host budget for both: **0.5 vCPU and 512 MB** for the platform, Postgres 1 vCPU and 1 GB on a
disk volume (Fors: Redis 0.25 vCPU, 256 MB). Real-provider timing as above. Mixed traffic at a fixed arrival rate: 80 %
streamed, 25 % with personal data, 5 % with a prompt injection, 10 % long conversations; 2 minutes per rate. Fors
gateway's key has PII *Allow* and content guard *Record* on; Bifrost has no counterpart and runs governance and its
request log only, so it does less work per message here.

| | Idle | 10 msg/s | 50 msg/s | 100 msg/s |
|---|---:|---:|---:|---:|
| Fors, CPU (average / highest sample) | 0.01 | 0.21 / 0.35 | 0.31 / 0.36 | 0.47 / 0.52 |
| Bifrost, CPU (average / highest sample) | 0.00 | 0.19 / 0.22 | 0.48 / 0.53 | killed |
| Fors, CPU periods throttled | | 8 % | 0.3 % | 48 % |
| Bifrost, CPU periods throttled | | 0.1 % | **93 %** | killed |
| Fors, memory | 73–80 MB | 85–148 MB | 147–168 MB | 166–200 MB |
| Bifrost, memory | 145 MB | 117–298 MB | 292–482 MB | 408–512 MB, then **OOM-killed** |
| Fors, whole message p50 / p95 / p99 | | 5.59 / 6.36 / 6.86 s | 5.58 / 6.23 / 6.81 s | 5.58 / 6.18 / 6.82 s |
| Bifrost, whole message p50 / p95 / p99 | | 5.61 / 6.10 / 6.76 s | 5.62 / 6.22 / 6.89 s | 88.5 % failed |
| Failed checks: Fors / Bifrost | | 0 / 0 | 0 / 0 | **0** / 7 973 of 9 004 |
| Its Postgres memory: Fors / Bifrost | 36–39 / 161–167 MB | 40–44 / 169–192 MB | 42–47 / 193–263 MB | 48–55 / – |

Straight to the fake LLM at 50/s the same mix takes 5.58–5.59 / 6.22–6.26 / 6.83 s, so at 10 and 50 msg/s neither
gateway added anything measurable. At 100 msg/s (about 560 streams open at once) Bifrost's memory reached its 512 MB
limit and the kernel killed it (`OOMKilled=true`, exit 137); before that, requests were taking 9–15 s because 0.5 vCPU
was not enough. Its request log also lost the entries still queued in memory: 7 798 rows for about 8 800 successful
messages.

**Per-message record:** Fors 553 bytes per usage record including indexes (20 797 rows); Bifrost 4 978 bytes per request
log row including indexes (7 798 rows, content logging on, full-text indexes over the content,
`framework/logstore/migrations.go`): 9× the database growth per message.

**What Bifrost needs for 100 msg/s** (follow-up, Bifrost only, 1 vCPU and 1 GB, same Postgres and traffic):

| Bifrost at 100 msg/s | 0.5 vCPU, 512 MB | 1 vCPU, 1 GB |
|---|---:|---:|
| Failed checks | 7 973 of 9 004 (OOM-killed) | **0 of 12 999** |
| CPU (average / highest sample) | – | 0.94 / 1.01 |
| CPU periods throttled | – | 69 % |
| Memory | 408–512 MB, killed | 132–616 MB |
| Whole message p50 / p95 / p99 | – | 5.63 / 6.20 / 6.86 s |
| Log rows | 7 798 (queued rows lost) | 12 999 (4 976 bytes each) |

So for 100 msg/s of realistic streams Bifrost needs about 1 vCPU and well over 512 MB (its whole vCPU, throttled in
two thirds of the periods, so with no headroom), against 0.5 vCPU and 200 MB for Fors with PII detection
and content guard on as well.

## Several instances

Not run for Bifrost. Its open-source build keeps rate-limit and budget counters in each process and writes them to the
config store every 10 s (`plugins/governance/store.go`, `tracker.go`); cluster mode, which would share them, is in the
configuration schema but not read by the open-source server at this tag. Several Bifrost instances behind a load
balancer would each enforce the full limits on their own share of the traffic. Fors's counters are in Redis
and shared (see *Several instances* in `docs/performance.md`).

## Harness check

The fake LLM, Postgres and Redis were never the limit (hardware threads busy, against each container's CPU set):

| Run | Msg/s | Platform | Fake LLM (of 4) | Postgres (of 2) | Redis (of 1) |
|---|---:|---:|---:|---:|---:|
| Fors, `run.sh` 64 clients, non-streaming | 17 714 | 3.48 of 4 | 1.69 | 0.20 | 0.74 |
| Fors, `run.sh` 64 clients, streaming | 11 520 | 3.45 of 4 | 2.06 | 0.14 | 0.57 |
| Fors, `scale.sh` 4 vCPU peak (256 clients) | 14 369 | 3.78 of 4 | 2.55 | 0.24 | 0.64 |
| Fors, `scale.sh` 6 vCPU peak (384 clients) | 15 135 | 5.14 of 6 | 2.42 of 3 | 0.27 of 1 | 0.68 |
| Bifrost, `run.sh` 64 clients, non-streaming | 4 316 | 4.01 of 4 | 1.21 | 0.96 | – |
| Bifrost, `run.sh` 64 clients, streaming | 1 925 | 3.99 of 4 | 0.98 | 0.80 | – |
| Bifrost, `scale.sh` 4 vCPU peak (32 clients) | 2 077 | 3.98 of 4 | 0.89 | 0.62 | – |
| Bifrost, `scale.sh` 6 vCPU peak (48 clients) | 2 594 | 6.01 of 6 | 1.23 of 3 | 0.83 of 1 | – |

Straight to the fake LLM, k6 reached about 51 000 non-streamed and 31 000–34 000 streamed msg/s, 2–3× Fors's
best and 12–16× Bifrost's. Bifrost used all of its vCPUs at its plateau while its Postgres had at least half its
threads free, so Bifrost's own process is the limit. Fors's busiest component outside itself was the fake
LLM at 2.6 of 4 threads.

## Caveats

- **One machine, one session, single 20–30 s samples.** Run-to-run noise on this harness is about ±3 % for the gateway
  and ±10 % for slower platforms; the differences here are far larger.
- **Laptop, hybrid CPU, WSL2.** Absolute figures are lower than the 2026-11-02 baseline's; use the ratios.
- **Fake LLM.** Answers are 45 tokens and instant except in `latency.sh` and the sizing test. Real traffic is dominated
  by the model's time; at realistic rates both gateways are invisible to users and only the hardware they need differs.
- **Not quite the same features.** Bifrost stores message content by default (Fors stores metadata only);
  the *no content* variant removes that difference and changes Bifrost's figures by less than 4 %. Bifrost has a
  Prometheus telemetry plugin on by default and no PII detection or content guard. Bifrost settles 11 s after each
  scenario (the gateway 3 s) so its 5 s log batches and 10 s counter dumps are counted; that idle time is inside its
  CPU window and is negligible at these rates.
- **Configuration.** Bifrost ran with its defaults apart from what the comparison needs (stores in Postgres, a key,
  limits, budgets, prices, private network access to the fake LLM, `GOMAXPROCS`). Its provider concurrency (1 000
  workers, a 5 000-deep queue, `core/schemas/provider.go`) and the log writer's queue and batch sizes can be tuned;
  that was not tried. A larger log queue would drop fewer entries at the cost of memory.
- **Model names.** Bifrost was sent `fake-chat` with no provider prefix so that its key's weighted providers route it,
  the counterpart of the gateway's `fors/chat` alias.
- **Observation, not on the measured path:** a request without a key to Bifrost was refused (401) but logged twice,
  once per provider in the key-less fallback chain.

## Reproducing

```bash
# from benchmarks/compare, on the Linux file system (see README)
docker pull maximhq/bifrost:v2.5.1
bash run.sh                                                    # fake fors bifrost
PLATFORMS=bifrost BIFROST_CONFIG=config-no-content.json bash run.sh
PLATFORMS=bifrost BIFROST_CONFIG=config-bare.json bash run.sh
bash scale.sh
bash latency.sh
bash sizing/run.sh                                             # fors, then bifrost
BIFROST_SIZING_CPUS=1 BIFROST_SIZING_MEM=1g PLATFORMS=bifrost RATES=100 bash sizing/run.sh
```

The drop counter: start Bifrost with `BIFROST_SETUP_TOKEN` set in the shell and read
`GET http://127.0.0.1:18100/api/logs/dropped` with the header `X-Bifrost-Setup-Token`.

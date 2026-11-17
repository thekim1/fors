<!-- One run, 2026-11-17: RAG platform + gateway only, 1000 users, 30 s ramp, 90 s measured, glm-5.3-flash at Z.ai API speed per Artificial Analysis (3.05 s first token, 17 ms/token; prefill 60 ms per 1 000 prompt tokens assumed). WSL2, 20 hardware threads; platform on 4 vCPUs. Harness check: fake LLM 1.13 vCPU mean (1.55 peak) of its 4 threads, load client 0.51 vCPU on average of its 4: neither was the limit. -->

# Knowledge load test: eneo vs. the RAG platform + gateway

1000 staff chatting back to back (no time to read), 30 s ramp, then 90 s measured; 30 % follow-ups in the same conversation. Requests started in the hold are measured to the end of their stream. Fake LLM as glm-5.3-flash (see README.md); the platform under test on 4 vCPUs and 18 GB.

## What staff experience

| Platform | Requests | Answers/min | Errors | First token p50 | p95 | p99 | Gap p50 | Gap p99 | Longest stall p95 | Whole answer p50 | p95 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| RAG platform + gateway | 4751 | 3167 | 0 | 3.34 s | 4.19 s | 4.31 s | 18 ms | 48 ms | 114 ms | 18.73 s | 19.61 s |

## New conversations and follow-ups

| Platform | Message | Requests | First token p50 | p95 | Whole answer p95 |
|---|---|---|---|---|---|
| RAG platform + gateway | new | 3301 | 3.31 s | 4.17 s | 19.56 s |
| RAG platform + gateway | follow-up | 1450 | 3.42 s | 4.27 s | 19.68 s |

## What the model received

| Platform | Chunks in the collection | Model calls | Prompt tokens p50 | p95 | max | Embedding calls | Model's own first token (p50 prompt) | Platform's share of first token p50 |
|---|---|---|---|---|---|---|---|---|
| RAG platform + gateway | 248 | 6136 | 3,026 | 4,507 | 4,521 | 38 | 3232 ms | 109 ms |

The fake LLM waits 3050 ms plus 60 ms per 1 000 prompt tokens before the first token, as a real model reads its prompt; the platform's share is the measured first token minus that.

## Resources: RAG platform + gateway (of 4 vCPU, 18 GB)

| Container | CPU mean (vCPU) | CPU peak | Memory peak (GiB) |
|---|---|---|---|
| gateway | 1.33 | 1.77 | 0.33 |
| gateway-db | 0.02 | 0.05 | 0.09 |
| gateway-redis | 0.02 | 0.02 | 0.00 |
| rag-api | 1.41 | 1.87 | 0.51 |
| rag-db | 0.29 | 0.48 | 0.31 |
| rag-parser | 0.13 | 0.83 | 0.98 |
| rag-worker | 0.00 | 0.01 | 0.34 |
| **total** | **3.20** | **4.09** | **2.52** |

Postgres connections: peak 24, of them active at most 3.
Connection-pool or database refusals in the logs: none.


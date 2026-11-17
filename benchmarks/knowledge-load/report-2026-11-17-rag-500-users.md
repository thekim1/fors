<!-- One run, 2026-11-17: RAG platform + gateway only, 500 users, 20 s ramp, 90 s measured, glm-5.3-flash at Z.ai API speed per Artificial Analysis (3.05 s first token, 17 ms/token; prefill 60 ms per 1 000 prompt tokens assumed). WSL2, 20 hardware threads; platform on 4 vCPUs. Harness check: fake LLM 0.50 vCPU mean (0.73 peak) of its 4 threads, load client 0.34 vCPU on average of its 4: neither was the limit. -->

# Knowledge load test: eneo vs. the RAG platform + gateway

500 staff chatting back to back (no time to read), 20 s ramp, then 90 s measured; 30 % follow-ups in the same conversation. Requests started in the hold are measured to the end of their stream. Fake LLM as glm-5.3-flash (see README.md); the platform under test on 4 vCPUs and 18 GB.

## What staff experience

| Platform | Requests | Answers/min | Errors | First token p50 | p95 | p99 | Gap p50 | Gap p99 | Longest stall p95 | Whole answer p50 | p95 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| RAG platform + gateway | 2405 | 1603 | 0 | 3.23 s | 4.13 s | 4.26 s | 18 ms | 27 ms | 58 ms | 18.55 s | 19.46 s |

## New conversations and follow-ups

| Platform | Message | Requests | First token p50 | p95 | Whole answer p95 |
|---|---|---|---|---|---|
| RAG platform + gateway | new | 1692 | 3.20 s | 4.11 s | 19.43 s |
| RAG platform + gateway | follow-up | 713 | 3.30 s | 4.19 s | 19.52 s |

## What the model received

| Platform | Chunks in the collection | Model calls | Prompt tokens p50 | p95 | max | Embedding calls | Model's own first token (p50 prompt) | Platform's share of first token p50 |
|---|---|---|---|---|---|---|---|---|
| RAG platform + gateway | 248 | 2937 | 3,106 | 4,818 | 4,832 | 38 | 3236 ms | -8 ms |

The fake LLM waits 3050 ms plus 60 ms per 1 000 prompt tokens before the first token, as a real model reads its prompt; the platform's share is the measured first token minus that.

## Resources: RAG platform + gateway (of 4 vCPU, 18 GB)

| Container | CPU mean (vCPU) | CPU peak | Memory peak (GiB) |
|---|---|---|---|
| gateway | 0.71 | 0.90 | 0.30 |
| gateway-db | 0.02 | 0.07 | 0.11 |
| gateway-redis | 0.01 | 0.02 | 0.00 |
| rag-api | 0.79 | 1.04 | 0.37 |
| rag-db | 0.15 | 0.25 | 0.29 |
| rag-parser | 0.11 | 0.71 | 1.67 |
| rag-worker | 0.00 | 0.00 | 0.32 |
| **total** | **1.80** | **2.86** | **3.01** |

Postgres connections: peak 21, of them active at most 3.
Connection-pool or database refusals in the logs: none.


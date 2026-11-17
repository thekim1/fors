<!-- Three runs, 2026-11-17: 150 users, 90 s each, glm-5.3-flash at Z.ai API speed per Artificial Analysis (3.05 s first token, 17 ms/token; prefill 60 ms per 1 000 prompt tokens assumed). WSL2, 20 hardware threads, 15.4 GB for Docker; platform on 4 vCPUs. eneo v2.3.1, rag-b47 at 0ddbf1b1. Each run from fresh stacks. -->

# 3 runs, 150 users, 90 s measured each

## eneo v2.3.1

| Figure | run-1 | run-2 | run-3 | Mean | Spread |
|---|---|---|---|---|---|
| Answers per minute | 113 | 101 | 116 | 110 | 15 |
| Errors | 1 | 16 | 1 | 6 | 15 |
| First token p50 (s) | 9.25 | 11.41 | 8.47 | 9.71 | 2.93 |
| First token p95 (s) | 65.06 | 58.31 | 54.81 | 59.39 | 10.25 |
| First token p99 (s) | 66.51 | 60.77 | 57.59 | 61.62 | 8.91 |
| Response starts p95 (s) | 60.34 | 53.84 | 51.11 | 55.10 | 9.23 |
| Longest pause p95 (s) | 1.07 | 1.21 | 1.14 | 1.14 | 0.14 |
| Whole answer p95 (s) | 82.07 | 79.25 | 75.73 | 79.02 | 6.34 |
| Prompt tokens p50 | 20,368 | 20,368 | 20,368 | 20,368 | 0 |
| CPU mean (vCPU of 4) | 3.42 | 3.61 | 3.52 | 3.52 | 0.19 |
| CPU peak (vCPU of 4) | 4.32 | 4.20 | 4.13 | 4.22 | 0.19 |

## RAG platform + gateway

| Figure | run-1 | run-2 | run-3 | Mean | Spread |
|---|---|---|---|---|---|
| Answers per minute | 473 | 472 | 475 | 473 | 3 |
| Errors | 0 | 0 | 0 | 0 | 0 |
| First token p50 (s) | 3.28 | 3.24 | 3.22 | 3.25 | 0.05 |
| First token p95 (s) | 4.12 | 4.12 | 4.12 | 4.12 | 0.01 |
| First token p99 (s) | 4.24 | 4.26 | 4.25 | 4.25 | 0.02 |
| Response starts p95 (s) | 0.01 | 0.01 | 0.01 | 0.01 | 0.00 |
| Longest pause p95 (s) | 0.03 | 0.03 | 0.03 | 0.03 | 0.00 |
| Whole answer p95 (s) | 19.46 | 19.51 | 19.55 | 19.51 | 0.09 |
| Prompt tokens p50 | 3,130 | 3,106 | 3,108 | 3,115 | 24 |
| CPU mean (vCPU of 4) | 0.71 | 0.69 | 0.67 | 0.69 | 0.04 |
| CPU peak (vCPU of 4) | 1.36 | 1.14 | 1.13 | 1.21 | 0.23 |


## Errors

- eneo run 1: 1 connection closed by the server.
- eneo run 2: 13 HTTP 500 (eneo's generic "Internal server error"), 1 HTTP 403, 2 connections closed.
- eneo run 3: 1 connection closed.
- eneo's backend log had 5, 16 and 21 lines matching database pool or timeout patterns (QueuePool, TimeoutError, too many clients) in runs 1-3; the lines were not kept (run.sh keeps them from now on, results/<platform>-errors.log). Postgres peaked at 131-136 connections, 86-94 active.
- RAG platform + gateway: no errors in any run; Postgres at most 19 connections.

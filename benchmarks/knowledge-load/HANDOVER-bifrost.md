# Handover: run the knowledge load test against the Bifrost gateway

**Task for the next chat:** run the same benchmarks as on 2026-11-17 with **Bifrost** (https://github.com/maximhq/bifrost)
as the LLM gateway in front of the Ume RAG platform, and compare it with the Ume LLM gateway (and Eneo, from the
existing results). Work on the branch `bench/eneo-vs-rag-knowledge-load` in `repos/llmgateway` (not main). Push the
results to that branch. Don't change code in the Eneo repo.

## What exists

Everything lives in `benchmarks/knowledge-load/` on that branch (last commit `9ea9e0c`):

| File | What it is |
|---|---|
| `README.md` | Setup, how to run, how to read the report. **Read it first.** |
| `compose.yaml` | Eneo v2.3.1, the RAG platform (rag-b47 images) + Ume gateway, fake LLM, PDF site. Platform pinned to threads 8–11, memory limits sum to 18 GB; fake LLM on 0–3, load client on 4–7. |
| `run.sh` | One run: env `PLATFORMS` (`eneo rag`), `VUS`, `RAMP`, `HOLD`, `FAKE_*`, `KEEP_UP`. Seeds, ingests the 36 PDFs, runs the load, collects docker stats, Postgres connections, error log lines, fake-LLM `/stats`, writes `results/report.md`. |
| `report.py`, `summarize_runs.py` | One run's report; several runs side by side (mean, spread). |
| `rag/setup.py`, `rag/seed_assistant.sql` | RAG setup by SQL (admin API needs OIDC); coverage floor lifted to 2.0 because fake embeddings mean nothing. |
| `eneo/*.py` | Eneo model seed and knowledge/assistant/key setup. |
| `report-2026-11-17*.md`, `summary-2026-11-17.html` | Results so far. The HTML summary is also published (private) at https://claude.ai/artifact/PxZ5Qsf1huiYq1e4pC5Lzz |
| `../Ume.LlmGateway.Benchmarks/KnowledgeLoad.cs` | The load client: `knowledge-load <eneo\|rag> <base> <vus> <ramp> <hold> <questions> <out>`. Calls the RAG API's `/v1/chat/completions` (SSE) the way the support chat POC does. |
| `src/Ume.LlmGateway.FakeLlm` | Fake OpenAI-compatible LLM: `FakeLatency` settings, `GET/DELETE /stats`, `FakeLlm:EchoPrompt=false` in compose (echoing the sources markup stalled rag-api). |

Corpus: `repos/onenotedownloader/pdf/Vanliga Fel` (36 PDFs, 248 chunks on the RAG side).

## The run matrix to repeat

Model faked as glm-5.3-flash: 1 000 000-token context, 850-token answers, first token 3050 ms, 17 ms/token ±30 %,
prefill 60 ms per 1 000 prompt tokens. 30 s ramp, 90 s measured, stress mode (ask again as soon as the answer ends).

| Run | Ume gateway result (RAG side) | Eneo |
|---|---|---|
| 100 users, 1 run | 315 answers/min, first token p95 4.1 s | 231/min, p95 8.3 s |
| 150 users, 3 runs | 473/min, p95 4.1 s, 0 errors | 110/min, p95 59 s, 18 errors |
| 500 users, 1 run | 1603/min, platform CPU 1.8 | not run |
| 1000 users, 1 run | 3167/min, p95 4.19 s, 0 errors, CPU mean 3.20 / peak 4.09 of 4 (gateway 1.33, rag-api 1.41) | not run |

For Bifrost: run 100, 150×3, 500 and 1000 with `PLATFORMS=rag` and the gateway swapped. Eneo needn't be rerun.
Watch the gateway container's CPU and memory, the extra first-token time it adds and errors. 1000 users was already near the
4-vCPU limit, so a lighter gateway may show up there.

## How to swap in Bifrost

1. **Image:** use the official image (`maximhq/bifrost`), pinned to a release tag, not `latest`. Record the version in
   the report.
2. **Compose:** add a `bifrost` service (or a compose profile/override, e.g. `GATEWAY=bifrost` in `run.sh`) on the
   same `PLATFORM_CPUS` cpuset. Give it the Ume gateway's budget: 2 GB, plus the 2 GB its Postgres + Redis had, if
   Bifrost needs no database. Set `GOMAXPROCS=4` explicitly.
3. **Point the RAG platform at it:** `Gateway__BaseUrl` in rag-api and rag-worker (compose lines ~18, 242, 269), and
   `Gateway__Key` to a Bifrost key.
4. **Model names:** the RAG platform asks for `ume/chat` (chat, reranker) and `ume/embed` (embedding profile, 1024
   dimensions, `EmbeddingProfile__GatewayModel`). Bifrost routes on `provider/model`, so one way is a custom
   OpenAI-compatible provider named `ume` with base URL `http://fake-llm:8080/v1`. The fake LLM then receives
   `chat`/`embed` as model names; check that it accepts them and that `/stats` reports them. Embeddings must pass
   `dimensions` through.
5. **Comparable features:** the Ume gateway authenticates a virtual key (Postgres), rate-limits (Redis) and tracks
   cost on every call. Turn on Bifrost's equivalents (governance/virtual keys, logging) for the main runs. Optionally
   run once with them off to show the bare proxy. State in the report which plugins were on.

## Known blocker: the residency headers

The RAG platform's `ResidencyCeilingHandler` (`rag-b47/src/Ume.Rag.Infrastructure/Gateway/ResidencyCeilingHandler.cs`)
sends `x-ume-max-residency` on every call. It **refuses any model response without an `x-ume-residency` header** at or
below that ceiling (`GatewayResidencyException`, event 5003). The response to `/v1/models` is the exception. Bifrost won't
send this header, so every chat and embedding call would fail. Options, in order of preference:

- A Bifrost setting or plugin that adds a fixed response header (`x-ume-residency: Eu`). Check Bifrost's docs and code
  for custom response headers or a lightweight plugin hook.
- A minimal header-adding proxy (e.g. nginx, `add_header x-ume-residency Eu always;`, buffering off for SSE), counted
  in the platform's cpuset and memory, and reported as overhead.
- Don't patch rag-api's code for the benchmark unless the user agrees.

Other `x-ume-*` request headers (`run-id`, `step`, `session`, `pii`, `on-behalf-of`) should be ignored harmlessly by
Bifrost. Verify that it doesn't reject unknown headers. The Ume gateway's `/v1/context` (`OrganisationSync`) is
admin-api only and isn't part of the run.

## Gotchas from the first round

- Eneo uploads need the shared `eneo-tmp` volume; rag-migrate needs `DOTNET_ENVIRONMENT=Staging`; the blob volume
  needs the `rag-blobs-init` chown to 1654; `Crawl__AllowedPrivateRanges` is a comma-separated string.
- `export PDF_DIR` before `run.sh`.
- WSL2 has ~15.4 GB for Docker, so the 18 GB of limits don't bind. Neither platform came near it.
- Use docker published ports, not background localhost servers, from WSL.
- Results folders (`results/`, `results-*/`) are git-ignored. Commit the reports (`report-<date>-<name>.md`) and
  update or add an HTML summary like `summary-2026-11-17.html`.

## Notes on Eneo, checked in its code (v2.3.1 = v2.3.2 backend)

- The POC and the load client call `POST /conversations/?version=2`: knowledge fills up to half the context window
  (~20 400 prompt tokens per question vs ~3 100 on RAG). Without `version`, Eneo uses at most 30 chunks with autocut.
- A default assistant-scoped key allows 1 000 requests/hour (space 5 000, organization 10 000). The test raised it.
- Every claim about Eneo must be confirmed in the code of a named version, not docs or websites.

## Deliverables

1. Bifrost wired in behind a switch, so both gateways stay runnable.
2. Runs: 100, 150×3, 500, 1000 users, RAG + Bifrost.
3. A report comparing the Ume gateway and Bifrost (and Eneo from the earlier runs): throughput, first-token p50/p95/p99,
   gateway CPU/memory, errors, the gateway's share of first-token time. List the caveats, including the residency workaround.
4. An updated HTML summary, pushed to the branch. Offer to update the published artifact.

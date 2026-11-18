# Knowledge load test: eneo vs. the Ume RAG platform + gateway

How the two candidate platforms for the IT support chat hold up when **60 support staff chat at once for a minute**,
answering from **the same knowledge** (the 36 "Vanliga fel" PDFs) with **the same model**: the fake LLM given
glm-5.3-flash's production settings and the support chat's longest answer.

- **eneo v2.3.1** (backend, worker, Postgres/pgvector, Redis), through a service API key, the way the support tool's POC
  calls it.
  - **Retrieval mode.** Both the POC and the load client call `POST /conversations/?version=2`, so Eneo fills up to
    half the context window with knowledge (`assistants/references.py`). A call without `version` defaults to
    `version=1`: at most 30 chunks, trimmed by autocut. `version=3` (Eneo's new web UI) retrieves like `version=2`.
    The relevance floor (`INJECT_KNOWLEDGE_MIN_SCORE`) is off by default and was off here.
  - **Rate limit.** By default an assistant-scoped key may make 1 000 requests per hour (space key 5 000, organization
    key 10 000; window `API_KEY_RATE_LIMIT_WINDOW_SECONDS` = 3600, `main/config.py`). The test raises the key's limit so
    it's never hit. At the throughput measured here (110–231 answers/min), a default assistant key would be refused
    after about 5–9 minutes, so production needs a raised limit or a broader key.
  - Checked in the code of v2.3.1 and v2.3.2: the backend source of the two tags is identical.
- **The Ume RAG platform** (`rag-api`, `rag-worker`, parser, Postgres/pgvector) **behind the Ume LLM gateway**
  (gateway, Postgres, Redis), through a RAG API key.

It's a stress test: every client asks again as soon as its answer has streamed (no time to read).

## The setup

**The platform under test is one server of 4 vCPUs and 18 GB.** Every container of the platform shares threads 8–11
(`PLATFORM_CPUS`), with memory limits that add up to 18 GB:

| eneo | | RAG platform + gateway | |
|---|---|---|---|
| backend (4 gunicorn workers) | 6 GB | rag-api | 3 GB |
| worker | 4 GB | rag-worker | 2 GB |
| Postgres | 6 GB | parser | 4 GB |
| Redis | 2 GB | Postgres | 5 GB |
| | | gateway | 2 GB |
| | | gateway Postgres + Redis | 2 GB |

The fake LLM (threads 0–3), the load client (4–7) and the PDF web server (12–15, ingestion only) stay off those
cores, so they're never the limit. Databases are in memory and every run starts from scratch.

On WSL the Docker host may have less memory than 18 GB in total (the limits then can't bind): give WSL more in
`%UserProfile%\.wslconfig` (`[wsl2]` `memory=24GB`) to make them meaningful. The report shows what each container used.

**The model: glm-5.3-flash, faked.** One fake LLM (`src/Ume.LlmGateway.FakeLlm`) serves both platforms:

| Setting | Value | Variable |
|---|---|---|
| Context window | 1 000 000 tokens (given to both platforms) | `CONTEXT_WINDOW` |
| Answer | 850 tokens, the support chat's longest | `FAKE_ANSWER_WORDS` |
| First token | 400 ms | `FAKE_TTFT_MS` |
| Reading the prompt | +60 ms per 1 000 prompt tokens | `FAKE_PREFILL_MS_PER_1K` |
| Pace | 25 ms per token, ±30 % | `FAKE_TOKEN_MS`, `FAKE_JITTER_PCT` |
| Embeddings | 1024 dimensions, instant | |

The prompt-reading time matters: the platforms send very different prompts (below), and a real model takes longer to
start answering a long one. The fake LLM records every prompt's size (`GET /stats`), so the report separates the model's
time from the platform's.

**The knowledge.** Both platforms ingest the same PDFs before the test (not measured), as their administrators would:

- **eneo**: models seeded in its database (`eneo/seed_models.py`: glm-5.3-flash with the 1 000 000-token window, a
  1024-dimension embedding model), then through its API (`eneo/setup_knowledge.py`): a space, a collection, the PDFs
  uploaded and embedded by the worker, an assistant on the collection with knowledge injected (eneo's default), a
  service key.
- **RAG platform**: the gateway seeded as `benchmarks/compare` does (Eu providers to the fake LLM, `ume/chat` and
  `ume/embed`); the RAG database by SQL (`rag/setup.py`): the repo's own `scripts/dev/seed-url-list.sql` (a public
  collection with the PDFs as a URL-list source, served by nginx) and `rag/seed_assistant.sql` (the published
  assistant, its model limits, a chat key). The admin API would do the same but needs an OIDC sign-in.

How much knowledge each puts in the prompt is the platforms' own design:

- **eneo** takes up to `context window / chunk size / 2` chunks (`assistants/references.py`): with 1 000 000 tokens
  that's 2 500 chunks, so a small collection goes into **every** prompt in full.
- **The RAG platform** takes its top 10 chunks within a fixed budget of at most 8 000 tokens.

**Fake vectors.** The fake LLM's embeddings are hashed words, not meaning, so no chunk is "close" to a question. eneo
doesn't care (it has no relevance floor); the RAG platform would answer "not found" without calling the model. Real
embeddings find the matching guide for these on-topic tickets, so `rag/setup.py` lifts the collection's coverage floor
(`coverageMaxDistance` 2.0): retrieval then returns its normal top-K and the model is called, as in production.

**The load** (`knowledge-load` in `benchmarks/Ume.LlmGateway.Benchmarks`, `KnowledgeLoad.cs`): `VUS` clients start over
`RAMP` seconds, then `HOLD` seconds count; requests started in the hold are measured to the end of their stream. Each
asks one of 42 support tickets (`questions.json`, one or two per PDF topic) and 30 % of the messages are follow-ups in
the same conversation (eneo continues its session; the RAG platform gets the history). The client reads the stream as
it arrives, which k6 can't: time to the first token, every gap between tokens, the whole answer, and every failure (HTTP
status, error event, stream cut off, no answer text, timeout after 5 minutes).

## Running

Needs Docker, the .NET SDK, python3, bash and `taskset` (Linux or WSL), and the `rag-b47` repository next to this one
(`RAG_REPO`). Build the images once:

```bash
# this repository
dotnet publish src/Ume.LlmGateway.FakeLlm -c Release /t:PublishContainer -p:ContainerImageTag=knowledge-load
dotnet publish src/Ume.LlmGateway.Gateway -c Release /t:PublishContainer -p:ContainerImageTag=knowledge-load

# rag-b47
for p in Api Worker MigrationService; do dotnet publish src/Ume.Rag.$p -c Release -t:PublishContainer -p:ContainerImageTag=knowledge-load; done
docker build -t ume-rag/parser:knowledge-load src/parser

# eneo v2.3.1, from a clone of https://github.com/eneo-ai/eneo (LF line endings)
git clone --depth 1 --branch v2.3.1 https://github.com/eneo-ai/eneo.git /tmp/eneo
docker build --build-arg OTEL_SERVICE_VERSION=v2.3.1 -t eneo-backend:v2.3.1 /tmp/eneo/backend
```

Then (about 15 minutes for both):

```bash
PDF_DIR="/path/to/Vanliga Fel" benchmarks/knowledge-load/run.sh
PLATFORMS=rag VUS=60 HOLD=60 FAKE_TOKEN_MS=10 PDF_DIR=... benchmarks/knowledge-load/run.sh   # one platform, a faster model
```

It writes `results/report.md`, and the raw data next to it: `<platform>.json` (load client), `<platform>-llm.json`
(prompt sizes), `<platform>-stats.txt` (CPU and memory every 2 s), `<platform>-pg.txt` (Postgres connections).

## Reading the report

- **What staff experience:** time to first token, the gaps between tokens (a stuttering stream), the longest stall per
  answer, the whole answer, answers per minute, errors. With 850 tokens at 25 ms, an answer streams for about 21 s
  however fast the platform is; the platform shows in the first token and the gaps.
- **What the model received:** prompt size per message. The model's own share of the first token is
  400 ms + 60 ms per 1 000 prompt tokens; the rest is the platform's.
- **Resources:** CPU in vCPUs (4 = the whole server) and peak memory per container, Postgres connections, and any
  connection-pool refusals in the logs.

One run of one minute is a snapshot: run it more than once before drawing conclusions, and look at the spread.

#!/usr/bin/env bash
# Knowledge load test: eneo vs. the Ume RAG platform behind the Ume LLM gateway (README.md).
#
# For each platform in PLATFORMS, on a fresh stack: start it with the fake LLM answering at once, ingest the PDFs of
# PDF_DIR, give the fake LLM glm-5.3-flash's timing, warm up, then VUS staff chat back to back (no time to read) for
# HOLD seconds after a RAMP, measured by the load client (time to first token, gaps, whole stream, errors). Alongside:
# CPU and memory of every container of the platform, Postgres connections, and the prompt sizes the fake LLM received.
# Writes results/<platform>.json, results/<platform>-*.txt and results/report.md.
#
#   PDF_DIR=".../Vanliga Fel" benchmarks/knowledge-load/run.sh
#   PLATFORMS=rag VUS=60 HOLD=60 FAKE_TOKEN_MS=25 benchmarks/knowledge-load/run.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
cd "$HERE"

: "${PDF_DIR:?Set PDF_DIR to the folder of PDFs}"
export PDF_DIR
export RAG_REPO="${RAG_REPO:-$(cd "$REPO/.." && pwd)/rag-b47}"
PLATFORMS="${PLATFORMS:-eneo rag}"
VUS="${VUS:-60}"
RAMP="${RAMP:-10}"
HOLD="${HOLD:-60}"
FOLLOWUP_SHARE="${FOLLOWUP_SHARE:-0.3}"
LOAD_CPUS="${LOAD_CPUS:-4-7}"
export CONTEXT_WINDOW="${CONTEXT_WINDOW:-1000000}"

# glm-5.3-flash as the support chat sees it. The answer is the longest support answer, 850 tokens; first token, prefill
# per 1 000 prompt tokens and pace per token are a real provider's (the gateway's latency.sh profile).
GLM_TTFT_MS="${FAKE_TTFT_MS:-400}"
GLM_PREFILL_MS_PER_1K="${FAKE_PREFILL_MS_PER_1K:-60}"
GLM_TOKEN_MS="${FAKE_TOKEN_MS:-25}"
GLM_JITTER_PCT="${FAKE_JITTER_PCT:-30}"
GLM_ANSWER_WORDS="${FAKE_ANSWER_WORDS:-850}"

GATEWAY_PEPPER=knowledge-load-pepper-0123456789abcdef0123456789abcdef
mkdir -p results rag/keys
[[ -s rag/keys/rag-blob-internal ]] || { head -c 32 /dev/urandom | base64 > rag/keys/rag-blob-internal; chmod 644 rag/keys/rag-blob-internal; }

log() { printf '[%s] %s\n' "$(date +%T)" "$*"; }
compose() { docker compose --profile eneo --profile rag "$@"; }

wait_healthy() { # container
  for _ in $(seq 1 150); do
    [[ "$(docker inspect -f '{{.State.Health.Status}}' "$1" 2>/dev/null)" == healthy ]] && return 0
    sleep 2
  done
  log "$1 did not become healthy"; docker logs --tail 30 "$1"; return 1
}

instant_llm() { FAKE_TTFT_MS=0 FAKE_PREFILL_MS_PER_1K=0 FAKE_TOKEN_MS=0 FAKE_JITTER_PCT=0 FAKE_ANSWER_WORDS=0 compose up -d --force-recreate fake-llm >/dev/null; }

glm_llm() {
  FAKE_TTFT_MS=$GLM_TTFT_MS FAKE_PREFILL_MS_PER_1K=$GLM_PREFILL_MS_PER_1K FAKE_TOKEN_MS=$GLM_TOKEN_MS \
    FAKE_JITTER_PCT=$GLM_JITTER_PCT FAKE_ANSWER_WORDS=$GLM_ANSWER_WORDS compose up -d --force-recreate fake-llm >/dev/null
  for _ in $(seq 1 30); do curl -sf http://127.0.0.1:18090/ >/dev/null && return 0; sleep 1; done
  log "fake LLM did not start"; return 1
}

setup_eneo() {
  compose up -d eneo-db eneo-redis eneo-backend eneo-worker >/dev/null
  wait_healthy knowledge-load-eneo-backend-1
  local out; out=$(compose exec -T eneo-backend python /bench/setup_knowledge.py | tee "results/eneo-setup.txt" | tail -1)
  read -r ASSISTANT KEY <<<"$out"
  CHUNKS=$(docker exec knowledge-load-eneo-db-1 psql -U postgres -d eneo -tAc "select count(*) from info_blob_chunks")
  BASE=http://127.0.0.1:18000/api/v1
  DB=knowledge-load-eneo-db-1 DBNAME=eneo
  SERVICES="eneo-backend eneo-worker eneo-db eneo-redis"
}

setup_rag() {
  compose up -d gateway-db gateway-redis >/dev/null
  wait_healthy knowledge-load-gateway-db-1
  # The gateway's database: migrations and the compare harness's seed (Eu providers to the fake LLM, aliases ume/chat
  # and ume/embed, one virtual key the RAG services use).
  export GATEWAY_KEY
  GATEWAY_KEY=$(cd "$REPO" && dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- compare-seed \
    "Host=127.0.0.1;Port=15432;Database=gatewaydb;Username=postgres;Password=postgres" "$GATEWAY_PEPPER" http://fake-llm:8080/v1 | tail -1)
  compose up -d gateway rag-db pdf-site rag-parser >/dev/null
  compose up rag-migrate rag-blobs-init >/dev/null
  compose up -d rag-worker rag-api >/dev/null
  wait_healthy knowledge-load-rag-parser-1
  local out; out=$(python3 rag/setup.py | tee "results/rag-setup.txt" | tail -1)
  read -r ASSISTANT KEY CHUNKS <<<"$out"
  compose stop pdf-site >/dev/null
  BASE=http://127.0.0.1:18100
  DB=knowledge-load-rag-db-1 DBNAME=ragdb
  SERVICES="rag-api rag-worker rag-parser rag-db gateway gateway-db gateway-redis"
}

# CPU and memory of the platform's containers every 2 s, and its Postgres connections, until the file "stop" appears.
sample() { # platform
  local names="" s
  for s in $SERVICES; do names+=" knowledge-load-$s-1"; done
  : >"results/$1-stats.txt"; : >"results/$1-pg.txt"
  while [[ ! -e results/.stop ]]; do
    docker stats --no-stream --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}}' $names >>"results/$1-stats.txt" 2>/dev/null || true
    docker exec "$DB" psql -U postgres -d "$DBNAME" -tAc \
      "select count(*), count(*) filter (where state = 'active') from pg_stat_activity where backend_type = 'client backend'" \
      >>"results/$1-pg.txt" 2>/dev/null || true
    sleep 2
  done
}

load() { # platform vus ramp hold out
  (cd "$REPO" && LOAD_KEY="$KEY" LOAD_ASSISTANT="$ASSISTANT" FOLLOWUP_SHARE="$FOLLOWUP_SHARE" \
    taskset -c "$LOAD_CPUS" dotnet run --no-build -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- \
    knowledge-load "$1" "$BASE" "$2" "$3" "$4" "$HERE/questions.json" "$5")
}

(cd "$REPO" && dotnet build -c Release benchmarks/Ume.LlmGateway.Benchmarks >/dev/null)

for platform in $PLATFORMS; do
  log "== $platform: fresh stack, ingesting $(ls "$PDF_DIR"/*.pdf | wc -l) PDFs (fake LLM instant)"
  compose down -v --remove-orphans >/dev/null 2>&1 || true
  instant_llm
  "setup_$platform"
  log "$platform: $CHUNKS chunks; fake LLM now glm-5.3-flash: ${GLM_TTFT_MS} ms + ${GLM_PREFILL_MS_PER_1K} ms/1k prompt tokens, ${GLM_TOKEN_MS} ms/token ±${GLM_JITTER_PCT} %, ${GLM_ANSWER_WORDS} tokens"
  glm_llm

  log "$platform: warm-up (8 clients, one answer each)"
  load "$platform" 8 2 3 "$HERE/results/$platform-warmup.json" >/dev/null

  curl -s -X DELETE http://127.0.0.1:18090/stats >/dev/null
  rm -f results/.stop
  sample "$platform" & sampler=$!
  load_started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  log "$platform: $VUS clients, ramp ${RAMP} s, hold ${HOLD} s"
  load "$platform" "$VUS" "$RAMP" "$HOLD" "$HERE/results/$platform.json"
  touch results/.stop; wait "$sampler"
  curl -s http://127.0.0.1:18090/stats >"results/$platform-llm.json"
  for s in $SERVICES; do
    docker logs "knowledge-load-$s-1" 2>&1 | grep -ciE 'too many clients|remaining connection slots|53300|QueuePool limit|TimeoutError' \
      | sed "s/^/$s /" >>"results/$platform-pg-errors.txt" || true
    # The lines themselves, and every error the platform logged during the load, to find out why requests failed.
    docker logs --since "$load_started" "knowledge-load-$s-1" 2>&1 \
      | grep -iE 'too many clients|remaining connection slots|53300|QueuePool|TimeoutError|Traceback|exception|error' \
      | head -200 | sed "s/^/$s: /" >>"results/$platform-errors.log" || true
  done
  echo "$CHUNKS" >"results/$platform-chunks.txt"
  log "$platform: done"
done

# KEEP_UP=1 leaves the last platform running, to look at it.
[[ "${KEEP_UP:-0}" == 1 ]] || compose down -v --remove-orphans >/dev/null 2>&1 || true
GLM_TTFT_MS=$GLM_TTFT_MS GLM_PREFILL_MS_PER_1K=$GLM_PREFILL_MS_PER_1K python3 report.py results >results/report.md
log "report: $HERE/results/report.md"

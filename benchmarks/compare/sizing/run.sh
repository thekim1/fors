#!/usr/bin/env bash
# Sizing test: is a small host enough? The gateway gets 0.5 vCPU and 512 MB, Postgres 1 vCPU and 1 GB (on a disk
# volume, not tmpfs), Redis 0.25 vCPU and 256 MB. The key has PII Allow and content guard Record; the fake LLM has a real
# provider's timing. Mixed traffic (k6/mix.js) at fixed rates, with CPU, memory, CPU throttling and the size of the usage
# table measured. Uses the images built for ../README.md. Results: ../results/sizing/.
#
# Bifrost (PLATFORMS includes bifrost) gets the same budget and stores its config and request logs in Postgres with
# db's limits; its open-source build has no PII detection or content guard. Reports: report.txt (gateway),
# report-bifrost.txt.
#
#   RATES="10 50 100" DURATION=2m benchmarks/compare/sizing/run.sh   (defaults; about 10 minutes per platform)
#   PLATFORMS="bifrost" benchmarks/compare/sizing/run.sh
set -euo pipefail
cd "$(dirname "$0")"
REPO="$(cd ../../.. && pwd)"
PEPPER="compare-only-pepper-0123456789abcdef0123456789abcdef"
RATES="${RATES:-10 50 100}" DURATION="${DURATION:-2m}" PLATFORMS="${PLATFORMS:-fors bifrost}"
BIFROST_KEY="sk-bf-compare-0123456789abcdef" # ../bifrost/config.json
OUT=../results/sizing
COMPOSE=(docker compose)
mkdir -p "$OUT/k6" && chmod 777 "$OUT"

APP=gateway DB=db DBNAME=gatewaydb DATA=(compare-sizing-gateway-1 compare-sizing-db-1 compare-sizing-redis-1)
psqlc() { "${COMPOSE[@]}" exec -T "$DB" psql -U postgres -d "$DBNAME" -tAc "$1"; }
stats() { docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' "${DATA[@]}"; }
cpu_stat() { # application cgroup: usage_usec nr_periods nr_throttled throttled_usec
  local id; id=$(docker inspect -f '{{.Id}}' "compare-sizing-$APP-1")
  for f in /sys/fs/cgroup/system.slice/docker-$id.scope/cpu.stat /sys/fs/cgroup/docker/$id/cpu.stat; do
    [[ -f $f ]] && { awk '/^(usage_usec|nr_periods|nr_throttled|throttled_usec) /{printf "%s=%s ", $1, $2}' "$f"; return; }
  done
  echo "(cgroup cpu.stat not readable)"
}
sample() { while :; do stats | sed "s/^/$(date +%s) $1 /"; sleep 4; done; }
k6run() { # rate duration url model
  "${COMPOSE[@]}" --profile tools run --rm -e KEY="$KEY" -e RATE="$1" -e DUR="$2" -e URL="$3" -e MODEL="$4" \
    k6 run -q /scripts/mix.js 2>&1 | grep -E 'checks_failed|http_reqs\.\.|http_req_duration\.' || true
}

for platform in $PLATFORMS; do
"${COMPOSE[@]}" --profile tools --profile bifrost down -v --remove-orphans >/dev/null 2>&1 || true
if [[ $platform == bifrost ]]; then
  # The whole stack is configured by ../bifrost/config.json on first start; the log rows are its per-message records.
  APP=bifrost DB=bifrost-db DBNAME=bifrost DATA=(compare-sizing-bifrost-1 compare-sizing-bifrost-db-1)
  URL=http://bifrost:8080/v1/chat/completions MODEL=fake-chat REPORT="$OUT/report-bifrost.txt" STATS="$OUT/stats-bifrost.txt"
  "${COMPOSE[@]}" --profile bifrost up -d --wait bifrost-db fake-llm >/dev/null
  "${COMPOSE[@]}" --profile bifrost up -d bifrost >/dev/null
  for _ in $(seq 60); do [[ $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:28100/health) == 200 ]] && break; sleep 1; done
  KEY="$BIFROST_KEY"
else
APP=gateway DB=db DBNAME=gatewaydb DATA=(compare-sizing-gateway-1 compare-sizing-db-1 compare-sizing-redis-1)
URL=http://gateway:8080/v1/chat/completions MODEL=fors/chat REPORT="$OUT/report.txt" STATS="$OUT/stats.txt"
"${COMPOSE[@]}" up -d --wait db redis fake-llm >/dev/null
KEY=""
for _ in 1 2 3 4 5; do # the host port forwards a moment after Postgres is healthy
  KEY=$(dotnet run -c Release --project "$REPO/benchmarks/Fors.Benchmarks" -- compare-seed \
    "Host=127.0.0.1;Port=25432;Database=gatewaydb;Username=postgres;Password=postgres" "$PEPPER" http://fake-llm:8080/v1 \
    2>"$OUT/seed-error.log" | tail -1) || true
  [[ "$KEY" == fors-sk-* ]] && break
  sleep 3
done
[[ "$KEY" == fors-sk-* ]] || { echo "Seeding failed:" >&2; tail -20 "$OUT/seed-error.log" >&2; exit 1; }
psqlc "UPDATE \"VirtualKeys\" SET \"PiiPolicy\" = 'Allow', \"ContentGuardPolicy\" = 'Record'" >/dev/null
"${COMPOSE[@]}" up -d gateway >/dev/null
for _ in $(seq 60); do [[ $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:28080/v1/models) == 401 ]] && break; sleep 1; done
fi

{
  echo "# Sizing run $(date -u +%FT%TZ): $platform"
  sample idle > "$STATS" & pid=$!; sleep 60; kill $pid
  echo "idle: $(stats | tr '\n' ';')"
  echo "## Fake LLM directly at 50/s (floor)"
  k6run 50 1m http://fake-llm:8080/v1/chat/completions fake-chat
  for rate in $RATES; do
    echo "## $APP at $rate/s"
    before=$(cpu_stat)
    sample "$rate" >> "$STATS" & pid=$!
    k6run "$rate" "$DURATION" "$URL" "$MODEL"
    kill $pid
    echo "cpu.stat before: $before"
    echo "cpu.stat after:  $(cpu_stat)"
  done
  sleep 12 # the usage writer (gateway) or log writer and counter dump (Bifrost: 5 s and 10 s) flush in the background
  if [[ $platform == bifrost ]]; then
    psqlc 'VACUUM ANALYZE logs' >/dev/null
    echo "## Log table (one row per message; content logging on, the default)"
    psqlc "SELECT count(*) AS rows, pg_total_relation_size('logs') AS bytes_with_indexes,
             pg_total_relation_size('logs') / greatest(count(*), 1) AS bytes_per_row,
             count(*) FILTER (WHERE status = 'success') AS ok FROM logs"
    echo "## Database size"
    psqlc "SELECT pg_database_size('bifrost')"
  else
  psqlc 'VACUUM ANALYZE "UsageRecords"' >/dev/null
  echo "## Usage table"
  psqlc "SELECT count(*) AS rows, pg_total_relation_size('\"UsageRecords\"') AS bytes_with_indexes,
           pg_total_relation_size('\"UsageRecords\"') / greatest(count(*), 1) AS bytes_per_row,
           count(*) FILTER (WHERE \"PiiCategories\" IS NOT NULL) AS with_pii, count(*) FILTER (WHERE \"GuardFlags\" IS NOT NULL) AS guard_flagged
         FROM \"UsageRecords\""
  fi
  echo "after: $(stats | tr '\n' ';')"
} | tee "$REPORT"
done

"${COMPOSE[@]}" --profile tools --profile bifrost down -v --remove-orphans >/dev/null

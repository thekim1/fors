#!/usr/bin/env bash
# Compares the Ume LLM gateway with Bifrost (and eneo, PLATFORMS=eneo) against the same fake LLM. See README.md in this folder.
#
#   ./run.sh                      all platforms, default scenarios
#   VUS="1 32" DURATION=60s ./run.sh
#   PLATFORMS="bifrost" ./run.sh  one platform only (results of earlier runs are kept; PLATFORMS=eneo for eneo)
#
# Needs: Docker, the .NET SDK (to seed the gateway database), python3, and the images described in README.md.
set -euo pipefail
cd "$(dirname "$0")"

PLATFORMS="${PLATFORMS:-fake ume bifrost}" # eneo still works: PLATFORMS=eneo
VUS="${VUS:-1 16 64}"
DURATION="${DURATION:-30s}"
WARMUP="${WARMUP:-10s}"
source ./lib.sh
mkdir -p results

for platform in $PLATFORMS; do
  log "Starting $platform"
  start_platform "$platform"
  log "Warm-up ($WARMUP)"
  warm_up "$platform"
  for stream in false true; do
    for vus in $VUS; do
      measure "$platform" "$stream" "$vus" "$DURATION" "$platform-stream_$stream-vus_$vus"
    done
  done
done

stop_all
log "Report"
python3 report.py results | tee results/report.md

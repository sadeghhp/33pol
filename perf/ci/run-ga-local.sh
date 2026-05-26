#!/usr/bin/env bash
# Shortened GA k6 scripts against local mock stack (not staging thresholds).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

export BASE_URL="${BASE_URL:-http://127.0.0.1:8080}"
export MODEL="${MODEL:-gpt-local}"
export GATEWAY_PORT="${GATEWAY_PORT:-8080}"

if ! curl -sf "${BASE_URL}/health/live" >/dev/null 2>&1; then
  echo "Gateway not reachable at ${BASE_URL}. Start with: bash perf/ci/run-smoke.sh (in another terminal) or deploy stack." >&2
  exit 1
fi

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 is required" >&2
  exit 1
fi

echo "GA local — inference RPS (short)..."
k6 run perf/k6/scripts/inference-rps.js \
  -e "BASE_URL=${BASE_URL}" \
  -e "MODEL=${MODEL}" \
  --duration 30s \
  --vus 5 \
  --max-vus 10

echo "GA local — streaming concurrent (short)..."
k6 run perf/k6/scripts/streaming-concurrent.js \
  -e "BASE_URL=${BASE_URL}" \
  -e "MODEL=${MODEL}" \
  -e "STREAM_VUS=5" \
  --duration 30s

echo "GA local — rate limit storm (short)..."
k6 run perf/k6/scripts/rate-limit-storm.js \
  -e "BASE_URL=${BASE_URL}" \
  -e "MODEL=${MODEL}" \
  -e "STORM_VUS=5" \
  --duration 20s

echo "All shortened GA scripts completed."

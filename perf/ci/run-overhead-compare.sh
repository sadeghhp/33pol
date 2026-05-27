#!/usr/bin/env bash
# Compare mock upstream vs gateway path (local-perf). Requires mock + gateway running.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

MOCK_PORT="${MOCK_PORT:-18080}"
GATEWAY_PORT="${GATEWAY_PORT:-8080}"
OVERHEAD_DURATION="${OVERHEAD_DURATION:-30s}"

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 is required" >&2
  exit 1
fi

if ! curl -sf "http://127.0.0.1:${MOCK_PORT}/health" >/dev/null 2>&1; then
  echo "Mock upstream not reachable on :${MOCK_PORT}. Run: python3 perf/scripts/mock-upstream.py" >&2
  exit 1
fi

if ! curl -sf "http://127.0.0.1:${GATEWAY_PORT}/health/live" >/dev/null 2>&1; then
  echo "Gateway not reachable on :${GATEWAY_PORT}. Run: bash perf/ci/run-smoke.sh (or start gateway manually)" >&2
  exit 1
fi

mkdir -p perf/reports

echo "Running overhead compare (duration=${OVERHEAD_DURATION})..."
k6 run perf/k6/scripts/overhead-compare.js \
  -e "DIRECT_URL=http://127.0.0.1:${MOCK_PORT}" \
  -e "GATEWAY_URL=http://127.0.0.1:${GATEWAY_PORT}" \
  -e "MODEL=gpt-local" \
  -e "OVERHEAD_DURATION=${OVERHEAD_DURATION}" \
  --summary-export "perf/reports/overhead-summary.json"

echo "Summary written to perf/reports/overhead-summary.json"
echo "Record p99 delta in perf/reports/ga-*.md (target: gateway overhead p99 < 5 ms vs direct on local-perf)."

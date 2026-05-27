#!/usr/bin/env bash
# G-01 local substitute when no staging URL: shortened k6 GA suite on Docker Compose.
# Production staging thresholds are NOT represented — see docs/ga-signoff-no-staging.md.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

export BASE_URL="${BASE_URL:-http://127.0.0.1:8080}"
# Always mock-gpt on Compose (WireMock); override only if explicitly set.
COMPOSE_MODEL="${MODEL:-mock-gpt}"
if [[ "${COMPOSE_MODEL}" == "gpt-local" ]]; then
  COMPOSE_MODEL="mock-gpt"
fi
export GATEWAY_PORT="${GATEWAY_PORT:-8080}"
ADMIN_KEY="${GATEWAY_ADMIN_API_KEY:-sk-33pol-dev-admin-key}"

if ! curl -sf "${BASE_URL}/health/live" >/dev/null 2>&1; then
  echo "Gateway not reachable at ${BASE_URL}. Start: docker compose up -d --build" >&2
  exit 1
fi

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 is required" >&2
  exit 1
fi

if [[ -z "${API_KEY:-}" ]]; then
  resp="$(curl -sf -X POST "${BASE_URL}/admin/api/keys" \
    -H "Authorization: Bearer ${ADMIN_KEY}" \
    -H "Content-Type: application/json" \
    -d '{"name":"k6-ga-compose","scopes":["inference"]}')"
  export API_KEY="$(python3 -c "import json,sys; print(json.load(sys.stdin)['secret'])" <<<"${resp}")"
fi

K6_EXTRA=(--no-thresholds)

echo "GA Compose k6 — inference-rps (short)..."
k6 run "${K6_EXTRA[@]}" perf/k6/scripts/inference-rps.js \
  -e "BASE_URL=${BASE_URL}" \
  -e "MODEL=${COMPOSE_MODEL}" \
  -e "API_KEY=${API_KEY}" \
  -e "K6_SLEEP_SEC=1" \
  --duration 45s \
  --vus 1

echo "GA Compose k6 — streaming-concurrent (short)..."
k6 run "${K6_EXTRA[@]}" perf/k6/scripts/streaming-concurrent.js \
  -e "BASE_URL=${BASE_URL}" \
  -e "MODEL=${COMPOSE_MODEL}" \
  -e "API_KEY=${API_KEY}" \
  -e "STREAM_VUS=1" \
  -e "STREAM_DURATION=30s" \
  -e "K6_SLEEP_SEC=2"

echo "GA Compose k6 — rate-limit-storm (short)..."
k6 run "${K6_EXTRA[@]}" perf/k6/scripts/rate-limit-storm.js \
  -e "BASE_URL=${BASE_URL}" \
  -e "MODEL=${COMPOSE_MODEL}" \
  -e "API_KEY=${API_KEY}" \
  -e "STORM_VUS=3" \
  --duration 20s \
  --vus 3

echo "GA Compose k6 suite passed (local shortened thresholds)."

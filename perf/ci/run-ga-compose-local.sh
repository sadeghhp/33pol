#!/usr/bin/env bash
# Shortened GA k6 against Docker Compose (mock-gpt + inference key). Not staging thresholds.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

export BASE_URL="${BASE_URL:-http://127.0.0.1:8080}"
export MODEL="${MODEL:-mock-gpt}"
export GATEWAY_PORT="${GATEWAY_PORT:-8080}"
ADMIN_KEY="${GATEWAY_ADMIN_API_KEY:-sk-33pol-dev-admin-key}"

if [[ -z "${API_KEY:-}" ]]; then
  resp="$(curl -sf -X POST "${BASE_URL}/admin/api/keys" \
    -H "Authorization: Bearer ${ADMIN_KEY}" \
    -H "Content-Type: application/json" \
    -d '{"name":"k6-ga-local","scopes":["inference"]}')"
  export API_KEY="$(python3 -c "import json,sys; print(json.load(sys.stdin)['secret'])" <<<"${resp}")"
fi

exec bash perf/ci/run-ga-local.sh

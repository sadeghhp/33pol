#!/usr/bin/env bash
# Shortened GA k6 against Docker Compose (mock-gpt + inference key). Not staging thresholds.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

export BASE_URL="${BASE_URL:-http://127.0.0.1:8080}"
export MODEL="${MODEL:-mock-gpt}"
export GATEWAY_PORT="${GATEWAY_PORT:-8080}"
# Default matches the compose dev sentinel (deploy/docker/docker-compose.yml Gateway__Bootstrap__AdminApiKey).
ADMIN_KEY="${GATEWAY_ADMIN_API_KEY:-sk-33pol-dev-local-unsafe}"

if [[ -z "${API_KEY:-}" ]]; then
  resp="$(curl -sf -X POST "${BASE_URL}/admin/api/keys" \
    -H "Authorization: Bearer ${ADMIN_KEY}" \
    -H "Content-Type: application/json" \
    -d '{"name":"k6-ga-local","scopes":["inference"]}')"
  KEY_ID="$(python3 -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"${resp}")"
  API_KEY="$(python3 -c "import json,sys; print(json.load(sys.stdin)['secret'])" <<<"${resp}")"
  export API_KEY

  # Grant the model to the new key (same as run-ga-compose-k6.sh); without a grant the k6 run 403s
  # unless the model is publicAccess.
  curl -sf -X PUT "${BASE_URL}/admin/api/keys/${KEY_ID}/model-grants" \
    -H "Authorization: Bearer ${ADMIN_KEY}" \
    -H "Content-Type: application/json" \
    -d "{\"modelIds\":[\"${MODEL}\"]}" >/dev/null
fi

exec bash perf/ci/run-ga-local.sh

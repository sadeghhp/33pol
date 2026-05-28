#!/usr/bin/env bash
# G-02 local substitute: shortened soak (default 10m). Full 4h requires staging per plan.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

export BASE_URL="${BASE_URL:-http://127.0.0.1:8080}"
COMPOSE_MODEL="${MODEL:-mock-gpt}"
[[ "${COMPOSE_MODEL}" == "gpt-local" ]] && COMPOSE_MODEL="mock-gpt"
ADMIN_KEY="${GATEWAY_ADMIN_API_KEY:-sk-33pol-dev-admin-key}"
export SOAK_DURATION="${SOAK_DURATION:-10m}"
export SOAK_VUS="${SOAK_VUS:-1}"
export SOAK_SLEEP_SEC="${SOAK_SLEEP_SEC:-5}"

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 is required" >&2
  exit 1
fi

if [[ -z "${API_KEY:-}" ]]; then
  resp="$(curl -sf -X POST "${BASE_URL}/admin/api/keys" \
    -H "Authorization: Bearer ${ADMIN_KEY}" \
    -H "Content-Type: application/json" \
    -d '{"name":"soak-local","scopes":["inference"]}')"
  KEY_ID="$(python3 -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"${resp}")"
  export API_KEY="$(python3 -c "import json,sys; print(json.load(sys.stdin)['secret'])" <<<"${resp}")"

  curl -sf -X PUT "${BASE_URL}/admin/api/keys/${KEY_ID}/model-grants" \
    -H "Authorization: Bearer ${ADMIN_KEY}" \
    -H "Content-Type: application/json" \
    -d "{\"modelIds\":[\"${COMPOSE_MODEL}\"]}" >/dev/null
fi

for _ in $(seq 1 10); do
  if curl -sf -X POST "${BASE_URL}/v1/chat/completions" \
    -H "Authorization: Bearer ${API_KEY}" \
    -H "Content-Type: application/json" \
    -d "{\"model\":\"${COMPOSE_MODEL}\",\"messages\":[{\"role\":\"user\",\"content\":\"warmup\"}],\"stream\":false}" >/dev/null; then
    break
  fi
  sleep 1
done

echo "Soak local — duration=${SOAK_DURATION} vus=${SOAK_VUS} model=${COMPOSE_MODEL}"
k6 run perf/k6/scripts/soak.js \
  -e "BASE_URL=${BASE_URL}" \
  -e "MODEL=${COMPOSE_MODEL}" \
  -e "API_KEY=${API_KEY}"

echo "Soak local finished."

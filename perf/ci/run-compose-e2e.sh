#!/usr/bin/env bash
# Formal Compose E2E sign-off (G-04): health probes + inference via gateway registry.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "${ROOT}"

GATEWAY_PORT="${GATEWAY_PORT:-8080}"
ADMIN_KEY="${GATEWAY_ADMIN_API_KEY:-sk-33pol-dev-admin-key}"
MODEL="${MODEL:-mock-gpt}"
BASE="http://127.0.0.1:${GATEWAY_PORT}"

echo "=== Compose E2E (G-04) ==="
bash perf/ci/verify-compose-health.sh

echo "Creating inference API key..."
key_response="$(curl -sf -X POST "${BASE}/admin/api/keys" \
  -H "Authorization: Bearer ${ADMIN_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"name":"compose-e2e","scopes":["inference"]}')"
INFERENCE_KEY="$(python3 -c "import json,sys; print(json.load(sys.stdin)['secret'])" <<<"${key_response}")"

echo "GET /v1/models"
models="$(curl -sf "${BASE}/v1/models" -H "Authorization: Bearer ${INFERENCE_KEY}")"
python3 -c "import json,sys; d=json.load(sys.stdin); ids=[m['id'] for m in d.get('data',[])]; assert ids, 'no models'; print('  models:', ', '.join(ids[:8]))" <<<"${models}"

if ! python3 -c "import json,sys; ids=[m['id'] for m in json.load(sys.stdin).get('data',[])]; sys.exit(0 if '${MODEL}' in ids else 1)" <<<"${models}"; then
  echo "Model '${MODEL}' not in registry. Set MODEL= to an id from the list above." >&2
  exit 1
fi

echo "POST /v1/chat/completions (non-stream, model=${MODEL})"
chat="$(curl -sf -X POST "${BASE}/v1/chat/completions" \
  -H "Authorization: Bearer ${INFERENCE_KEY}" \
  -H "Content-Type: application/json" \
  -d "{\"model\":\"${MODEL}\",\"messages\":[{\"role\":\"user\",\"content\":\"compose e2e\"}],\"stream\":false}")"
python3 -c "import json,sys; c=json.load(sys.stdin)['choices'][0]['message']['content']; assert c, 'empty reply'; print('  reply:', repr(c[:80]))" <<<"${chat}"

echo "POST /v1/chat/completions (stream, model=${MODEL})"
stream_headers="$(curl -sf -D - -o /dev/null -X POST "${BASE}/v1/chat/completions" \
  -H "Authorization: Bearer ${INFERENCE_KEY}" \
  -H "Content-Type: application/json" \
  -d "{\"model\":\"${MODEL}\",\"messages\":[{\"role\":\"user\",\"content\":\"stream e2e\"}],\"stream\":true}")"
if ! grep -qi 'text/event-stream' <<<"${stream_headers}"; then
  echo "Expected text/event-stream from gateway; headers:" >&2
  echo "${stream_headers}" >&2
  exit 1
fi
echo "  Content-Type: text/event-stream"

echo "GET /admin (static)"
curl -sf "${BASE}/admin/index.html" >/dev/null
echo "  admin UI OK"

echo "Compose E2E sign-off passed."

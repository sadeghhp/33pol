#!/usr/bin/env bash
# Health check for gpu-gateway profile (gateway with embedded SQLite; no mock/observability required).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

if [ -f "${ROOT}/docker-compose.yml" ]; then
  COMPOSE_DIR="${ROOT}"
else
  COMPOSE_DIR="${ROOT}/deploy/docker"
fi

cd "${COMPOSE_DIR}"

# shellcheck disable=SC1091
[ -f .env ] && set -a && source .env && set +a

running_services="$(docker compose ps --services --filter status=running 2>/dev/null || true)"
if [[ "${running_services}" != *gateway* ]]; then
  echo "Start stack first: cd ${COMPOSE_DIR} && docker compose up -d --build" >&2
  exit 1
fi

if [[ "${COMPOSE_PROFILES:-}" == *mock* || "${COMPOSE_PROFILES:-}" == *full* ]]; then
  echo "Note: COMPOSE_PROFILES includes mock/full; gpu-gateway verify only requires the gateway." >&2
fi

if [[ "${running_services}" != *gateway* ]]; then
  echo "gateway: not running (start with: docker compose up -d --build gateway)" >&2
  exit 1
fi

curl -sf "http://localhost:${GATEWAY_PORT:-8080}/health/live" >/dev/null
echo "gateway OK"

echo "GPU gateway stack healthy (gateway with embedded SQLite)."

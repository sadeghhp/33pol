#!/usr/bin/env bash
# Health check for the all-in-one Compose stack (gateway + observability + mock upstream).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

if [ -f "${ROOT}/docker-compose.yml" ]; then
  COMPOSE_DIR="${ROOT}"
else
  COMPOSE_DIR="${ROOT}/deploy/docker"
fi

cd "${COMPOSE_DIR}"

running_services="$(docker compose ps --services --filter status=running 2>/dev/null || true)"
if [[ "${running_services}" != *postgres* ]]; then
  echo "Start stack first: cd ${COMPOSE_DIR} && docker compose up -d --build" >&2
  exit 1
fi

curl -sf "http://localhost:${MOCK_UPSTREAM_PORT:-18080}/v1/models" >/dev/null
echo "mock-upstream OK"

curl -sf "http://localhost:${PROMETHEUS_PORT:-9090}/-/healthy" >/dev/null
echo "prometheus OK"

curl -sf "http://localhost:${GRAFANA_PORT:-3000}/api/health" >/dev/null
echo "grafana OK"

bash "$(dirname "$0")/verify-grafana-dashboards.sh"

if [[ "${running_services}" == *gateway* ]]; then
  curl -sf "http://localhost:${GATEWAY_PORT:-8080}/health/live" >/dev/null
  echo "gateway OK"
else
  echo "gateway: not running (start full stack with: docker compose up -d --build)" >&2
  exit 1
fi

echo "Compose stack healthy."

#!/usr/bin/env bash
# Quick health check for deploy/docker stack (no gateway profile required).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
COMPOSE_DIR="${ROOT}/deploy/docker"

cd "${COMPOSE_DIR}"

if ! docker compose ps --status running 2>/dev/null | grep -q postgres; then
  echo "Start stack first: cd deploy/docker && docker compose up -d" >&2
  exit 1
fi

curl -sf "http://localhost:${MOCK_UPSTREAM_PORT:-18080}/v1/models" >/dev/null
echo "mock-upstream OK"

curl -sf "http://localhost:${PROMETHEUS_PORT:-9090}/-/healthy" >/dev/null
echo "prometheus OK"

curl -sf "http://localhost:${GRAFANA_PORT:-3000}/api/health" >/dev/null
echo "grafana OK"

echo "Compose observability + mock stack healthy."

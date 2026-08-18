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

# Read KEY=value lines from .env without sourcing it: `source .env` would execute shell syntax inside
# values and export every secret (GATEWAY_ADMIN_API_KEY, GATEWAY_KEY_PEPPER) to each child process.
# Mirrors scripts/install/lib/common.sh install_read_env_var; strips one pair of surrounding quotes.
env_get() {
  local v
  [[ -f .env ]] || return 0
  v="$(grep -E "^$1=" .env 2>/dev/null | tail -1 | cut -d= -f2- | tr -d '\r' || true)"
  if [[ ${#v} -ge 2 && "${v}" == \"*\" ]]; then
    v="${v:1:${#v}-2}"; v="${v//\\\"/\"}"; v="${v//\\\\/\\}"
  elif [[ ${#v} -ge 2 && "${v}" == \'*\' ]]; then
    v="${v:1:${#v}-2}"
  fi
  printf '%s' "${v}"
}
# Explicit environment beats .env, which beats the compose default.
env_or_file() { local cur="${!1:-}"; if [[ -n "${cur}" ]]; then printf '%s' "${cur}"; else env_get "$1"; fi; }
COMPOSE_PROFILES="$(env_or_file COMPOSE_PROFILES)"
GATEWAY_PORT="$(env_or_file GATEWAY_PORT)"
MOCK_UPSTREAM_PORT="$(env_or_file MOCK_UPSTREAM_PORT)"
PROMETHEUS_PORT="$(env_or_file PROMETHEUS_PORT)"
GRAFANA_PORT="$(env_or_file GRAFANA_PORT)"

running_services="$(docker compose ps --services --filter status=running 2>/dev/null || true)"
if [[ "${running_services}" != *gateway* ]]; then
  echo "Start stack first: cd ${COMPOSE_DIR} && docker compose up -d --build" >&2
  exit 1
fi

mock_profile_active=false
observability_profile_active=false
if [[ "${COMPOSE_PROFILES:-}" == *mock* || "${COMPOSE_PROFILES:-}" == *full* ]]; then
  mock_profile_active=true
fi
if [[ "${COMPOSE_PROFILES:-}" == *observability* || "${COMPOSE_PROFILES:-}" == *full* ]]; then
  observability_profile_active=true
fi

if [[ "${mock_profile_active}" == true ]]; then
  curl -sf "http://localhost:${MOCK_UPSTREAM_PORT:-18080}/v1/models" >/dev/null
  echo "mock-upstream OK"
fi

if [[ "${observability_profile_active}" == true ]]; then
  curl -sf "http://localhost:${PROMETHEUS_PORT:-9090}/-/healthy" >/dev/null
  echo "prometheus OK"

  curl -sf "http://localhost:${GRAFANA_PORT:-3000}/api/health" >/dev/null
  echo "grafana OK"

  # Grafana credentials go only to the dashboard verifier's environment (not exported globally).
  GRAFANA_PORT="${GRAFANA_PORT}" \
  GRAFANA_ADMIN_USER="$(env_or_file GRAFANA_ADMIN_USER)" \
  GRAFANA_ADMIN_PASSWORD="$(env_or_file GRAFANA_ADMIN_PASSWORD)" \
    bash "$(dirname "$0")/verify-grafana-dashboards.sh"
elif [[ "${mock_profile_active}" != true ]]; then
  echo "COMPOSE_PROFILES does not include 'observability' or 'full'; skipping prometheus/grafana checks" >&2
  echo "For observability set COMPOSE_PROFILES=observability in .env" >&2
fi

if [[ "${running_services}" == *gateway* ]]; then
  curl -sf "http://localhost:${GATEWAY_PORT:-8080}/health/live" >/dev/null
  echo "gateway OK"
else
  echo "gateway: not running (start full stack with: docker compose up -d --build)" >&2
  exit 1
fi

echo "Compose stack healthy."

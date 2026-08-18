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

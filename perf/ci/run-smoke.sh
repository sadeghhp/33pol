#!/usr/bin/env bash
# CI/local: mock upstream + gateway + k6 smoke.js
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

MOCK_PORT="${MOCK_PORT:-18080}"
GATEWAY_PORT="${GATEWAY_PORT:-8080}"
SMOKE_DURATION="${SMOKE_DURATION:-60s}"
CONFIGURATION="${CONFIGURATION:-Release}"

MOCK_PID=""
GATEWAY_PID=""

cleanup() {
  if [[ -n "${GATEWAY_PID}" ]]; then
    kill "${GATEWAY_PID}" 2>/dev/null || true
    wait "${GATEWAY_PID}" 2>/dev/null || true
  fi
  if [[ -n "${MOCK_PID}" ]]; then
    kill "${MOCK_PID}" 2>/dev/null || true
    wait "${MOCK_PID}" 2>/dev/null || true
  fi
}
trap cleanup EXIT

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 is required (https://grafana.com/docs/k6/latest/set-up/install-k6/)" >&2
  exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required for perf/scripts/mock-upstream.py" >&2
  exit 1
fi

echo "Building gateway (${CONFIGURATION})..."
dotnet build src/33pol.App/33pol.App.csproj -c "${CONFIGURATION}" --no-restore 2>/dev/null \
  || dotnet build src/33pol.App/33pol.App.csproj -c "${CONFIGURATION}"

python3 perf/scripts/mock-upstream.py &
MOCK_PID=$!
sleep 1

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://127.0.0.1:${GATEWAY_PORT}"
export Gateway__ModelsConfigPath="config/models.ci.json"
export Gateway__OperatorConsole__Enabled=false
export Gateway__RegistryWatchEnabled=false
export ConnectionStrings__GatewayDb=""

APP_DLL="src/33pol.App/bin/${CONFIGURATION}/net10.0/33pol.App.dll"
if [[ ! -f "${APP_DLL}" ]]; then
  echo "Gateway binary not found at ${APP_DLL}" >&2
  exit 1
fi

dotnet exec "${APP_DLL}" &
GATEWAY_PID=$!

echo "Waiting for gateway on :${GATEWAY_PORT}..."
ready=0
for _ in $(seq 1 90); do
  if curl -sf "http://127.0.0.1:${GATEWAY_PORT}/health/live" >/dev/null; then
    ready=1
    break
  fi
  sleep 1
done

if [[ "${ready}" -ne 1 ]]; then
  echo "Gateway did not become healthy in time" >&2
  exit 1
fi

# Allow first health probe cycle against mock upstream.
sleep 2

echo "Running k6 smoke (duration=${SMOKE_DURATION})..."
k6 run perf/k6/scripts/smoke.js \
  -e "BASE_URL=http://127.0.0.1:${GATEWAY_PORT}" \
  -e "MODEL=gpt-local" \
  -e "SMOKE_DURATION=${SMOKE_DURATION}"

echo "k6 smoke passed."

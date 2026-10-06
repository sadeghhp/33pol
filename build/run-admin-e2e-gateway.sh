#!/usr/bin/env bash
# Starts 33pol.App for Playwright E2E with the same bootstrap pattern as integration tests.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${ADMIN_E2E_PORT:-5050}"
HOST="${ADMIN_E2E_HOST:-127.0.0.1}"
URL="http://${HOST}:${PORT}"
E2E_MODELS="${ROOT}/config/e2e-models.json"

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="${URL}"
export ConnectionStrings__GatewayDb="${ConnectionStrings__GatewayDb:-InMemory:e2e-admin}"
export Gateway__Bootstrap__Enabled="${Gateway__Bootstrap__Enabled:-true}"
export Gateway__Bootstrap__AdminApiKey="${Gateway__Bootstrap__AdminApiKey:-sk-33pol-integration-admin-key}"
export Gateway__Bootstrap__KeyPepper="${Gateway__Bootstrap__KeyPepper:-integration-test-pepper}"
export Gateway__Security__KeyPepper="${Gateway__Security__KeyPepper:-integration-test-pepper}"
export Gateway__ModelsConfigPath="${E2E_MODELS}"

cd "$ROOT"

if [[ ! -f src/33pol.App/wwwroot/admin/index.html ]]; then
  echo "Missing built admin assets. Run: cd src/33pol.Admin.Web && npm run build" >&2
  exit 1
fi

if [[ ! -f "${E2E_MODELS}" ]]; then
  echo '{"models":[]}' > "${E2E_MODELS}"
fi

dotnet build src/33pol.App/33pol.App.csproj -c Release -p:SkipFrontendBuild=true --nologo -v q

cleanup() {
  if [[ -n "${GATEWAY_PID:-}" ]] && kill -0 "$GATEWAY_PID" 2>/dev/null; then
    kill "$GATEWAY_PID" 2>/dev/null || true
    wait "$GATEWAY_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

dotnet run --project src/33pol.App/33pol.App.csproj -c Release --no-build --no-launch-profile --nologo &
GATEWAY_PID=$!

deadline=$((SECONDS + 90))
until curl -sf "${URL}/health/ready" >/dev/null 2>&1; do
  if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then
    echo "Gateway process exited before becoming ready" >&2
    exit 1
  fi
  if (( SECONDS >= deadline )); then
    echo "Timed out waiting for ${URL}/health/ready" >&2
    exit 1
  fi
  sleep 0.5
done

echo "Admin E2E gateway ready at ${URL}/admin/"
wait "$GATEWAY_PID"

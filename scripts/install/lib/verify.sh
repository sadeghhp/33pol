#!/usr/bin/env bash
# Post-install health verification.

# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
# shellcheck source=compose.sh
source "$(dirname "${BASH_SOURCE[0]}")/compose.sh"

install_wait_for_gateway() {
  local port="$1"
  local max_seconds="${2:-180}"
  local elapsed=0
  local url="http://127.0.0.1:${port}/health/live"
  log "Waiting for gateway at ${url} (up to ${max_seconds}s)..."
  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] skip health wait"
    return 0
  fi
  while (( elapsed < max_seconds )); do
    if curl -sf "${url}" >/dev/null 2>&1; then
      log "Gateway is healthy."
      return 0
    fi
    sleep 5
    elapsed=$((elapsed + 5))
  done
  die "Gateway did not become healthy within ${max_seconds}s"
}

install_run_verify_script() {
  local install_dir="$1"
  local profile="$2"
  local script_dir
  script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../perf/ci" && pwd)"
  install_load_env "${install_dir}"
  export GATEWAY_PORT="${GATEWAY_PORT:-8080}"
  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] skip verify scripts (profile=${profile})"
    return 0
  fi
  log "Running post-install verify (profile=${profile})"
  bash "${script_dir}/verify-compose-health.sh"
}

install_probe_host_upstream() {
  local port="$1"
  local url="http://127.0.0.1:${port}/v1/models"
  if curl -sf "${url}" >/dev/null 2>&1; then
    log "Host upstream responded at ${url}"
    return 0
  fi
  log "WARNING: No response from host upstream at ${url} (start vLLM/Ollama on the host when ready)"
  return 0
}

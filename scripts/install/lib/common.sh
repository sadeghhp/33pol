#!/usr/bin/env bash
# Shared helpers for 33pol install scripts.

INSTALL_STATE_DIR="${HOME}/.33pol"
export DEFAULT_GIT_URL="https://github.com/sadeghhp/33pol.git"
export DEFAULT_INSTALL_DIR="${HOME}/33pol"

log() {
  printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*" | tee -a "${INSTALL_LOG_FILE:-/dev/null}" 2>/dev/null || printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*"
}

die() {
  log "ERROR: $*"
  exit 1
}

confirm() {
  local prompt="$1"
  local default="${2:-n}"
  if [[ "${INSTALL_YES:-false}" == true ]]; then
    return 0
  fi
  local reply
  if [[ "${default}" == y ]]; then
    read -rp "${prompt} [Y/n]: " reply
    reply="${reply:-Y}"
  else
    read -rp "${prompt} [y/N]: " reply
    reply="${reply:-N}"
  fi
  [[ "${reply}" =~ ^[Yy] ]]
}

init_logging() {
  mkdir -p "${INSTALL_STATE_DIR}"
  INSTALL_LOG_FILE="${INSTALL_STATE_DIR}/install-$(date '+%Y%m%d').log"
  touch "${INSTALL_LOG_FILE}"
}

redact_env_preview() {
  sed -E \
    -e 's/^(GATEWAY_ADMIN_API_KEY=).*/\1***REDACTED***/' \
    -e 's/^(GATEWAY_KEY_PEPPER=).*/\1***REDACTED***/' \
    -e 's/^(GRAFANA_ADMIN_PASSWORD=).*/\1***REDACTED***/' \
    -e 's/^([A-Z_]*API_KEY=).*/\1***REDACTED***/'
}

# Read a single KEY=value from .env without shell-expanding secrets (unlike `source .env`).
install_read_env_var() {
  local env_file="$1"
  local key="$2"
  local line
  [[ -f "${env_file}" ]] || return 1
  line="$(grep -E "^${key}=" "${env_file}" | tail -1 || true)"
  [[ -n "${line}" ]] || return 1
  printf '%s' "${line#*=}" | tr -d '\r'
}

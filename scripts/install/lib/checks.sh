#!/usr/bin/env bash
# Prerequisite checks for 33pol install.

# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

install_validate_port() {
  local port="$1"
  [[ "${port}" =~ ^[0-9]+$ ]] || return 1
  (( port >= 1 && port <= 65535 ))
}

install_port_in_use() {
  local port="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -ltn 2>/dev/null | grep -q ":${port} "
    return $?
  fi
  if command -v lsof >/dev/null 2>&1; then
    lsof -iTCP:"${port}" -sTCP:LISTEN -Pn >/dev/null 2>&1
    return $?
  fi
  return 1
}

install_profile_to_compose_profiles() {
  local profile="$1"
  case "${profile}" in
    gpu-gateway) echo "" ;;
    gpu-observability) echo "observability" ;;
    full-stack) echo "full" ;;
    *) return 1 ;;
  esac
}

install_validate_profile() {
  install_profile_to_compose_profiles "$1" >/dev/null
}

install_validate_model_id() {
  local model_id="$1"
  [[ "${model_id}" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]*$ ]]
}

install_check_openssl() {
  install_check_command openssl "Install openssl for generated secrets (apt install openssl / brew install openssl)"
}

install_check_command() {
  local cmd="$1"
  local hint="$2"
  if ! command -v "${cmd}" >/dev/null 2>&1; then
    die "Missing required command: ${cmd}. ${hint}"
  fi
}

install_check_docker() {
  install_check_command docker "Install Docker Engine 24+ (https://docs.docker.com/engine/install/)"
  if ! docker info >/dev/null 2>&1; then
    die "Docker daemon is not running. Start docker and retry."
  fi
}

install_check_compose() {
  install_check_command docker "Install Docker with Compose v2 plugin"
  if ! docker compose version >/dev/null 2>&1; then
    die "docker compose v2 is required (Compose file optional depends_on needs v2.20+)."
  fi
  local version
  version="$(docker compose version --short 2>/dev/null || docker compose version 2>/dev/null || echo "unknown")"
  log "Docker Compose: ${version}"
}

install_check_disk() {
  local min_mb="${1:-5120}"
  local check_dir="${INSTALL_DIR:-.}"
  local avail_kb=""
  if [[ ! -d "${check_dir}" ]]; then
    check_dir="$(dirname "${check_dir}")"
  fi
  [[ -d "${check_dir}" ]] || check_dir="."
  # df may exit non-zero under set -e if the path is missing; ignore failure.
  avail_kb="$(df -k "${check_dir}" 2>/dev/null | awk 'NR==2 {print $4}' || true)"
  if [[ -n "${avail_kb}" ]] && (( avail_kb < min_mb * 1024 )); then
    log "WARNING: Less than ${min_mb} MB free disk near ${check_dir}"
  fi
}

install_run_doctor() {
  local ports=("$@")
  install_check_command git "Install git to clone the repository"
  install_check_openssl
  install_check_docker
  install_check_compose
  install_check_command curl "Install curl for health checks"
  install_check_disk
  if command -v dotnet >/dev/null 2>&1; then
    log "dotnet: $(dotnet --version) (optional — not required for Docker install)"
  else
    log "dotnet: not installed (optional — Docker build uses SDK inside image)"
  fi
  local p
  for p in "${ports[@]}"; do
    if ! install_validate_port "${p}"; then
      die "Invalid port: ${p}"
    fi
    if install_port_in_use "${p}"; then
      log "WARNING: port ${p} appears to be in use"
    fi
  done
  log "Prerequisite checks passed."
}

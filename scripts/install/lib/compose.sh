#!/usr/bin/env bash
# Docker Compose operations.

# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

install_compose_dir() {
  local install_dir="$1"
  if [[ -f "${install_dir}/docker-compose.yml" ]]; then
    printf '%s' "${install_dir}"
  else
    die "No docker-compose.yml in ${install_dir}"
  fi
}

install_compose_build() {
  local install_dir="$1"
  local compose_dir
  compose_dir="$(install_compose_dir "${install_dir}")"
  log "Building gateway image (first build may take several minutes)..."
  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] docker compose -f ${compose_dir}/docker-compose.yml build gateway"
    return 0
  fi
  (cd "${compose_dir}" && docker compose build gateway)
}

install_compose_up() {
  local install_dir="$1"
  local compose_dir
  compose_dir="$(install_compose_dir "${install_dir}")"
  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] docker compose up -d"
    return 0
  fi
  (cd "${compose_dir}" && docker compose up -d)
}

install_compose_down() {
  local install_dir="$1"
  local with_volumes="$2"
  local compose_dir
  compose_dir="$(install_compose_dir "${install_dir}")"
  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] docker compose down"
    return 0
  fi
  if [[ "${with_volumes}" == true ]]; then
    (cd "${compose_dir}" && docker compose down -v)
  else
    (cd "${compose_dir}" && docker compose down)
  fi
}

install_compose_ps() {
  local install_dir="$1"
  local compose_dir
  compose_dir="$(install_compose_dir "${install_dir}")"
  (cd "${compose_dir}" && docker compose ps)
}

install_compose_logs() {
  local install_dir="$1"
  local service="${2:-}"
  local compose_dir
  compose_dir="$(install_compose_dir "${install_dir}")"
  if [[ -n "${service}" ]]; then
    (cd "${compose_dir}" && docker compose logs -f "${service}")
  else
    (cd "${compose_dir}" && docker compose logs -f)
  fi
}

install_load_env() {
  local install_dir="$1"
  local env_file="${install_dir}/.env"
  local val
  GATEWAY_PORT=8080
  COMPOSE_PROFILES=""
  if [[ ! -f "${env_file}" ]]; then
    return 0
  fi
  if val="$(install_read_env_var "${env_file}" GATEWAY_PORT)"; then
    GATEWAY_PORT="${val}"
  fi
  if val="$(install_read_env_var "${env_file}" COMPOSE_PROFILES)"; then
    COMPOSE_PROFILES="${val}"
  fi
  export GATEWAY_PORT COMPOSE_PROFILES
}

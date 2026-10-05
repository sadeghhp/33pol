#!/usr/bin/env bash
# 33pol-stack.sh — day-to-day operations for the 33pol Compose stack.
#
# Compatibility: Bash 4.4+, Docker Engine, Docker Compose v2.
# curl is used for HTTP health probes when it is installed. Bash /dev/tcp is the fallback.
#
# The script locates this directory as the project root and calls:
#   docker compose --project-directory <root> --project-name 33pol -f <root>/docker-compose.yml
# It does not modify Compose files, .env, or deploy/docker/33pol-deploy.sh.
# That deploy script remains the versioned rollout and rollback tool.
#
#   ./33pol-stack.sh                         # interactive menu on a terminal
#   ./33pol-stack.sh status
#   ./33pol-stack.sh start 33pol
#   ./33pol-stack.sh --yes start all
#   ./33pol-stack.sh logs prometheus --follow --tail 100
#   ./33pol-stack.sh health grafana
#   ./33pol-stack.sh --dry-run rebuild 33pol
#
# Exit codes: 0 success, 1 Docker or health failure, 2 usage, validation, or refused confirmation.
set -Eeuo pipefail

script_dir() {
  local source="${BASH_SOURCE[0]}" dir
  while [[ -L "${source}" ]]; do
    dir="$(cd -P "$(dirname -- "${source}")" && pwd)"
    source="$(readlink -- "${source}")"
    [[ "${source}" != /* ]] && source="${dir}/${source}"
  done
  cd -P "$(dirname -- "${source}")" && pwd
}

ROOT="$(script_dir)"
COMPOSE_FILE="${ROOT}/docker-compose.yml"
INCLUDED_COMPOSE_FILE="${ROOT}/deploy/docker/docker-compose.yml"
ENV_FILE="${ROOT}/.env"
PROJECT_NAME="33pol"
DEFAULT_TAIL=200

ASSUME_YES=false
FLAG_NO_COLOR=false
DRY_RUN=false
LOG_FILE=""
FOLLOW=false
TAIL="${DEFAULT_TAIL}"
COMMAND=""
TARGET=""
TOKEN_EXPLAINED=false
PROFILE_EXPLAINED=false
CANCELLED=false
INTERRUPTED=false
SCOPE=""
SELECTED=()
HEALTH_WAIT_SECONDS=90

C_RESET=""
C_DIM=""
C_RED=""
C_GREEN=""
C_YELLOW=""
C_BLUE=""
C_BOLD=""

usage() {
  cat <<'EOF'
33pol-stack.sh — manage the 33pol gateway, Prometheus, and Grafana.

The user-facing service 33pol is the Compose service gateway.
Prometheus and Grafana run only when COMPOSE_PROFILES includes observability or full.
This script reads .env and never writes it.

Usage:
  ./33pol-stack.sh [flags]
  ./33pol-stack.sh [flags] <command> [service] [flags]

No command on a terminal opens the menu. No command without a terminal prints this help and exits 2.

Commands:
  status [service]     Show docker compose ps -a. Default service: all. Read-only.
  start <service>      docker compose up -d. Does not rebuild. Service is required.
  stop <service>       docker compose stop after confirmation. Service is required.
  restart <service>    docker compose restart after confirmation. Service is required.
  rebuild <service>    Recreate after confirmation. Service is required.
                       33pol is rebuilt from the Dockerfile. Prometheus and Grafana are pulled.
                       Named volumes are kept.
  logs <service>       Logs for one service or all. Use --follow and --tail.
  health [service]     HTTP probes and container health. Default service: all.
  diagnose [service]   State, resources, networks, ports, volumes, and Compose validation.
                       Default service: all.
  validate             Check tools, files, profile, scrape token, and compose config. No service.
  help                 Show this help.

Service names: 33pol, prometheus, grafana, all.
33pol and gateway both select the gateway service.

Flags:
  --yes, -y            Skip confirmations. Without a terminal, stop, restart, and rebuild
                       are refused unless this flag is set.
  --dry-run            Print Compose commands and do not change containers.
                       Health checks print URLs and do not connect.
  --no-color           Plain text. Also off when stdout is not a terminal or NO_COLOR is set.
  --log-file PATH      Append timestamped script messages. Secret values are not written.
  --tail N             Log lines to show. Default: 200. Only valid with logs.
  --follow, -f         Stream logs until Ctrl+C. Only valid with logs.
  --help, -h           Show this help and exit 0.

Behavior notes:
  Starting Grafana also starts Prometheus and the gateway.
  Starting Prometheus waits until the gateway is healthy.
  Stopping the gateway leaves Prometheus unable to scrape until the gateway returns.
  When the observability profile is enabled and GATEWAY_METRICS_SCRAPE_TOKEN is
  missing, Compose cannot load the project. start and restart then use docker
  only for an existing 33pol container. Prometheus and Grafana still need Compose.
  stop can use docker. rebuild still needs the token.

Examples:
  ./33pol-stack.sh
  ./33pol-stack.sh status
  ./33pol-stack.sh start 33pol
  ./33pol-stack.sh --yes start all
  ./33pol-stack.sh logs prometheus --follow --tail 100
  ./33pol-stack.sh health grafana
  ./33pol-stack.sh --dry-run rebuild 33pol

Exit codes:
  0  success
  1  Docker or health failure
  2  usage, validation, or a refused confirmation
EOF
}

setup_colors() {
  if [[ "${FLAG_NO_COLOR}" == true || -n "${NO_COLOR:-}" || ! -t 1 ]]; then
    C_RESET=""
    C_DIM=""
    C_RED=""
    C_GREEN=""
    C_YELLOW=""
    C_BLUE=""
    C_BOLD=""
    return 0
  fi
  C_RESET=$'\033[0m'
  C_DIM=$'\033[2m'
  C_RED=$'\033[31m'
  C_GREEN=$'\033[32m'
  C_YELLOW=$'\033[33m'
  C_BLUE=$'\033[34m'
  C_BOLD=$'\033[1m'
}

_file() {
  [[ -n "${LOG_FILE}" ]] || return 0
  printf '%s %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*" >>"${LOG_FILE}"
}

_msg() {
  local color="$1" tag="$2"
  shift 2
  printf '%s%s%s %s\n' "${color}" "${tag}" "${C_RESET}" "$*" >&2
  _file "${tag} $*"
}

log() { _msg "${C_BLUE}" "==>" "$*"; }
ok() { _msg "${C_GREEN}" "ok" "$*"; }
warn() { _msg "${C_YELLOW}" "!" "$*"; }
err() { _msg "${C_RED}" "x" "$*"; }
step() {
  printf '\n%s%s%s\n' "${C_BOLD}" "$*" "${C_RESET}" >&2
  _file "$*"
}

open_log() {
  [[ -n "${LOG_FILE}" ]] || return 0
  local dir
  dir="$(dirname -- "${LOG_FILE}")"
  if [[ ! -d "${dir}" ]]; then
    err "Log directory does not exist: ${dir}"
    exit 2
  fi
  if ! touch -- "${LOG_FILE}" 2>/dev/null; then
    err "Cannot write log file: ${LOG_FILE}"
    exit 2
  fi
}

env_get() {
  local key="$1" v=""
  [[ -f "${ENV_FILE}" ]] || return 0
  v="$(grep -E "^${key}=" "${ENV_FILE}" 2>/dev/null | tail -n 1 | cut -d= -f2- | tr -d '\r' || true)"
  if [[ ${#v} -ge 2 && "${v}" == \"*\" ]]; then
    v="${v:1:${#v}-2}"
    v="${v//\\\"/\"}"
    v="${v//\\\\/\\}"
  elif [[ ${#v} -ge 2 && "${v}" == \'*\' ]]; then
    v="${v:1:${#v}-2}"
  fi
  printf '%s' "${v}"
}

env_or_file() {
  local name="$1"
  # An exported empty value overrides .env, matching Docker Compose.
  if [[ -v ${name} ]]; then
    printf '%s' "${!name}"
    return 0
  fi
  env_get "${name}"
}

profile_has() {
  local profiles="$1" want="$2" part trimmed
  IFS=',' read -ra _profile_parts <<< "${profiles}"
  for part in "${_profile_parts[@]}"; do
    trimmed="${part#"${part%%[![:space:]]*}"}"
    trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
    [[ "${trimmed}" == "${want}" ]] && return 0
  done
  return 1
}

observability_enabled() {
  local profiles
  profiles="$(env_or_file COMPOSE_PROFILES)"
  profile_has "${profiles}" observability || profile_has "${profiles}" full
}

trim_space() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "${value}"
}

token_is_set() {
  local token
  token="$(trim_space "$(env_or_file GATEWAY_METRICS_SCRAPE_TOKEN)")"
  [[ -n "${token}" ]]
}

explain_missing_token() {
  [[ "${TOKEN_EXPLAINED}" == true ]] && return 0
  TOKEN_EXPLAINED=true
  err "GATEWAY_METRICS_SCRAPE_TOKEN is not set."
  err "Prometheus mounts it as secret gateway_metrics_scrape_token."
  err "With the observability profile enabled, Docker Compose rejects the project until the token is set."
  err "Add it to ${ENV_FILE} only when that file does not already set it. This script will not edit that file."
  err "Then recreate so the gateway and Prometheus share the token:"
  err "  printf 'GATEWAY_METRICS_SCRAPE_TOKEN=%s\\n' \"\$(openssl rand -hex 24)\" >> '${ENV_FILE}'"
  err "  ./33pol-stack.sh --yes rebuild all"
}

explain_missing_profile() {
  local profiles
  [[ "${PROFILE_EXPLAINED}" == true ]] && return 0
  PROFILE_EXPLAINED=true
  profiles="$(env_or_file COMPOSE_PROFILES)"
  err "Prometheus and Grafana are enabled only with Compose profile observability or full."
  err "COMPOSE_PROFILES is '${profiles:-empty}'."
  err "Set COMPOSE_PROFILES=observability in ${ENV_FILE}. This script will not edit that file."
}

compose_model_ready() {
  if observability_enabled && ! token_is_set; then
    return 1
  fi
  return 0
}

selection_has() {
  local want="$1" svc
  for svc in "${SELECTED[@]}"; do
    [[ "${svc}" == "${want}" ]] && return 0
  done
  return 1
}

selection_needs_observability() {
  selection_has prometheus || selection_has grafana
}

ensure_profile_for_selection() {
  if selection_needs_observability && ! observability_enabled; then
    explain_missing_profile
    return 2
  fi
  return 0
}

narrow_all_to_enabled() {
  [[ "${SCOPE:-}" == all ]] || return 0
  if observability_enabled; then
    return 0
  fi
  warn "Prometheus and Grafana are disabled. This command is limited to 33pol."
  SELECTED=(gateway)
}

display_name() {
  case "$1" in
    gateway) printf '33pol' ;;
    *) printf '%s' "$1" ;;
  esac
}

join_services() {
  local svc first="true"
  for svc in "${SELECTED[@]}"; do
    if [[ "${first}" == true ]]; then
      printf '%s' "$(display_name "${svc}")"
      first="false"
    else
      printf ', %s' "$(display_name "${svc}")"
    fi
  done
}

services_for() {
  local spec="${1:-all}"
  spec="${spec,,}"
  spec="${spec// /}"
  case "${spec}" in
    all) SELECTED=(gateway prometheus grafana); SCOPE=all ;;
    33pol|gateway) SELECTED=(gateway); SCOPE=one ;;
    prometheus) SELECTED=(prometheus); SCOPE=one ;;
    grafana) SELECTED=(grafana); SCOPE=one ;;
    *)
      err "Unknown service '${spec}'. Use 33pol, prometheus, grafana, or all."
      return 2
      ;;
  esac
}

dry_run_cmd() {
  {
    printf '%sdry-run%s' "${C_DIM}" "${C_RESET}"
    printf ' %q' "$@"
    printf '\n'
  } >&2
}

dc() {
  if [[ "${DRY_RUN}" == true ]]; then
    dry_run_cmd docker compose --project-directory "${ROOT}" --project-name "${PROJECT_NAME}" -f "${COMPOSE_FILE}" "$@"
  fi
  if ! compose_model_ready; then
    explain_missing_token
    return 2
  fi
  if [[ "${DRY_RUN}" == true ]]; then
    return 0
  fi
  docker compose --project-directory "${ROOT}" --project-name "${PROJECT_NAME}" -f "${COMPOSE_FILE}" "$@"
}

confirm() {
  local prompt="$1" ans
  if [[ "${ASSUME_YES}" == true ]]; then
    log "Confirmed via --yes: ${prompt}"
    return 0
  fi
  if [[ ! -t 0 ]]; then
    err "This action needs confirmation: ${prompt}"
    err "Re-run with --yes, or run the script from a terminal."
    return 1
  fi
  read -r -p "${prompt} [y/N] " ans || {
    CANCELLED=true
    err "No input. Cancelled."
    return 1
  }
  if [[ "${ans}" == [yY] || "${ans}" == [yY][eE][sS] ]]; then
    return 0
  fi
  CANCELLED=true
  err "Cancelled."
  return 1
}

preflight_runtime() {
  if ! command -v docker >/dev/null 2>&1; then
    err "Docker was not found. Install Docker Engine and retry."
    return 2
  fi
  if ! docker compose version >/dev/null 2>&1; then
    err "Docker Compose v2 is required (the 'docker compose' command)."
    return 2
  fi
  local docker_info_err=""
  if ! docker_info_err="$(docker info 2>&1 >/dev/null)"; then
    if [[ "${docker_info_err}" == *[Pp]ermission* || "${docker_info_err}" == *denied* ]]; then
      err "Docker is installed but this user cannot access the Docker socket."
    else
      err "Docker is installed but the daemon is not reachable. Start Docker and retry."
    fi
    return 2
  fi
  if [[ ! -f "${COMPOSE_FILE}" ]]; then
    err "Compose file not found: ${COMPOSE_FILE}"
    return 2
  fi
  if [[ ! -f "${INCLUDED_COMPOSE_FILE}" ]]; then
    err "Included Compose file not found: ${INCLUDED_COMPOSE_FILE}"
    return 2
  fi
  if [[ ! -f "${ENV_FILE}" ]]; then
    err "Missing ${ENV_FILE}. Copy .env.example to .env and set the required values."
    err "This script will not create that file."
    return 2
  fi
  return 0
}

container_ids_for() {
  local svc="$1" running_only="$2"
  if [[ "${running_only}" == true ]]; then
    docker ps -q \
      --filter "label=com.docker.compose.project=${PROJECT_NAME}" \
      --filter "label=com.docker.compose.service=${svc}" 2>/dev/null || true
    return 0
  fi
  docker ps -aq \
    --filter "label=com.docker.compose.project=${PROJECT_NAME}" \
    --filter "label=com.docker.compose.service=${svc}" 2>/dev/null || true
}

container_id() {
  local svc="$1" ids=""
  ids="$(container_ids_for "${svc}" true)"
  if [[ -z "${ids}" ]]; then
    ids="$(container_ids_for "${svc}" false)"
  fi
  printf '%s' "${ids%%$'\n'*}"
}

container_port() {
  case "$1" in
    gateway) printf '8080' ;;
    prometheus) printf '9090' ;;
    grafana) printf '3000' ;;
    *)
      err "No container port mapped for $1"
      return 2
      ;;
  esac
}

probe_host() {
  local bind
  bind="$(env_or_file "$1")"
  if [[ -z "${bind}" || "${bind}" == "0.0.0.0" || "${bind}" == "::" || "${bind}" == "[::]" ]]; then
    printf '127.0.0.1'
    return 0
  fi
  if [[ "${bind}" == *:* ]]; then
    warn "Bind address ${bind} is not an IPv4 address. Probing 127.0.0.1 instead."
    printf '127.0.0.1'
    return 0
  fi
  printf '%s' "${bind}"
}

port_value() {
  local name="$1" fallback="$2" port
  port="$(env_or_file "${name}")"
  if [[ -z "${port}" ]]; then
    port="${fallback}"
  fi
  if [[ ! "${port}" =~ ^[0-9]+$ ]] || (( 10#${port} < 1 || 10#${port} > 65535 )); then
    err "${name} must be a TCP port from 1 to 65535."
    return 1
  fi
  printf '%s' "${port}"
}

http_ok_devtcp_once() {
  local url="$1" rest host port path status_line=""
  rest="${url#http://}"
  host="${rest%%:*}"
  rest="${rest#*:}"
  port="${rest%%/*}"
  path="/${rest#*/}"
  if [[ -z "${host}" || -z "${port}" || "${port}" == "${rest}" ]]; then
    err "Could not parse health URL: ${url}"
    return 1
  fi
  exec 3<>"/dev/tcp/${host}/${port}" || return 1
  if ! printf 'GET %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n' "${path}" "${host}" >&3; then
    exec 3<&- || true
    exec 3>&- || true
    return 1
  fi
  IFS= read -r -t 5 status_line <&3 || true
  exec 3<&- || true
  exec 3>&- || true
  [[ "${status_line}" == *" 200 "* ]]
}

http_ok_devtcp() {
  local url="$1"
  if command -v timeout >/dev/null 2>&1; then
    export -f http_ok_devtcp_once err
    timeout 5 bash -c 'http_ok_devtcp_once "$1"' bash "${url}"
    return $?
  fi
  http_ok_devtcp_once "${url}"
}

http_ok() {
  local url="$1"
  if command -v curl >/dev/null 2>&1; then
    curl -sf --noproxy '*' --max-time 5 -o /dev/null -- "${url}"
    return $?
  fi
  http_ok_devtcp "${url}"
}

check_http() {
  local name="$1" url="$2"
  if [[ "${DRY_RUN}" == true ]]; then
    log "dry-run health ${name}: ${url}"
    return 0
  fi
  if http_ok "${url}"; then
    ok "${name} ${url}"
    return 0
  fi
  err "${name} failed: ${url}"
  return 1
}

container_summary() {
  local id="$1"
  docker inspect --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} exit={{.State.ExitCode}} error={{.State.Error}}' "${id}"
}

show_status_fallback() {
  local svc rc=0
  printf 'NAMES\tSTATUS\tPORTS\n'
  for svc in "${SELECTED[@]}"; do
    if [[ "${DRY_RUN}" == true ]]; then
      dry_run_cmd docker ps -a \
        --filter "label=com.docker.compose.project=${PROJECT_NAME}" \
        --filter "label=com.docker.compose.service=${svc}"
      continue
    fi
    docker ps -a \
      --filter "label=com.docker.compose.project=${PROJECT_NAME}" \
      --filter "label=com.docker.compose.service=${svc}" \
      --format '{{.Names}}\t{{.Status}}\t{{.Ports}}' || rc=1
  done
  return "${rc}"
}

docker_lifecycle() {
  local action="$1" svc id running_id rc=0
  for svc in "${SELECTED[@]}"; do
    running_id="$(container_ids_for "${svc}" true)"
    running_id="${running_id%%$'\n'*}"
    if [[ "${action}" == stop ]]; then
      if [[ -z "${running_id}" ]]; then
        if [[ -n "$(container_id "${svc}")" ]]; then
          warn "$(display_name "${svc}") is not running. Left it unchanged."
          continue
        fi
        err "No container for $(display_name "${svc}"). Compose cannot create it until the project loads."
        rc=2
        continue
      fi
      id="${running_id}"
    else
      id="$(container_id "${svc}")"
      if [[ -z "${id}" ]]; then
        err "No container for $(display_name "${svc}"). Compose cannot create it until the project loads."
        rc=2
        continue
      fi
    fi
    if [[ "${DRY_RUN}" == true ]]; then
      dry_run_cmd docker "${action}" "${id}"
      continue
    fi
    docker "${action}" "${id}" || rc=1
  done
  return "${rc}"
}

classify_gateway_wait() {
  local live_ok="$1" ready_ok="$2"
  if [[ "${live_ok}" != true ]]; then
    printf 'failed\n'
    return 1
  fi
  if [[ "${ready_ok}" != true ]]; then
    printf 'started-not-ready\n'
    return 0
  fi
  printf 'healthy\n'
  return 0
}

wait_for_selected() {
  local svc urls line name url live_url="" ready_url=""
  local deadline live_ok ready_ok service_ok result
  [[ "${DRY_RUN}" == true ]] && return 0
  for svc in "${SELECTED[@]}"; do
    log "Waiting up to ${HEALTH_WAIT_SECONDS}s for $(display_name "${svc}") to become healthy."
    urls="$(health_urls_for "${svc}")" || return 1
    live_url=""
    ready_url=""
    while IFS= read -r line; do
      [[ -n "${line}" ]] || continue
      name="${line%%|*}"
      url="${line#*|}"
      if [[ "${name}" == "33pol live" ]]; then
        live_url="${url}"
      elif [[ "${name}" == "33pol ready" ]]; then
        ready_url="${url}"
      else
        live_url="${url}"
      fi
    done <<< "${urls}"
    deadline=$((SECONDS + HEALTH_WAIT_SECONDS))
    live_ok=false
    ready_ok=false
    service_ok=false
    while (( SECONDS < deadline )); do
      if [[ "${svc}" == gateway ]]; then
        if [[ -n "${live_url}" ]] && http_ok "${live_url}"; then
          live_ok=true
        fi
        if [[ -n "${ready_url}" ]] && http_ok "${ready_url}"; then
          ready_ok=true
        fi
        if [[ "${live_ok}" == true && ( -z "${ready_url}" || "${ready_ok}" == true ) ]]; then
          break
        fi
      elif [[ -n "${live_url}" ]] && http_ok "${live_url}"; then
        service_ok=true
        break
      fi
      sleep 2
    done
    if [[ "${svc}" == gateway ]]; then
      result="$(classify_gateway_wait "${live_ok}" "${ready_ok}")" || true
      if [[ "${result}" == failed ]]; then
        err "33pol failed to start. ${live_url} did not return 200 within ${HEALTH_WAIT_SECONDS}s."
        err "Check ./33pol-stack.sh logs 33pol and ./33pol-stack.sh health 33pol."
        return 1
      fi
      if [[ "${result}" == started-not-ready ]]; then
        warn "33pol started but is not ready: ${ready_url}"
        warn "The container is running. Readiness can lag while dependencies settle."
        continue
      fi
      ok "33pol is healthy."
      continue
    fi
    if [[ "${service_ok}" != true ]]; then
      err "$(display_name "${svc}") failed to start. ${live_url} did not return 200 within ${HEALTH_WAIT_SECONDS}s."
      err "Check ./33pol-stack.sh logs $(display_name "${svc}") and ./33pol-stack.sh health $(display_name "${svc}")."
      return 1
    fi
    ok "$(display_name "${svc}") is healthy."
  done
}

print_rebuild_plan() {
  local -a obs=()
  local svc
  log "These Compose commands were not run because the project cannot load:"
  for svc in "${SELECTED[@]}"; do
    [[ "${svc}" == gateway ]] || obs+=("${svc}")
  done
  if selection_has gateway; then
    dry_run_cmd docker compose --project-directory "${ROOT}" --project-name "${PROJECT_NAME}" -f "${COMPOSE_FILE}" build gateway
  fi
  if [[ ${#obs[@]} -gt 0 ]]; then
    dry_run_cmd docker compose --project-directory "${ROOT}" --project-name "${PROJECT_NAME}" -f "${COMPOSE_FILE}" pull "${obs[@]}"
    dry_run_cmd docker compose --project-directory "${ROOT}" --project-name "${PROJECT_NAME}" -f "${COMPOSE_FILE}" up -d --force-recreate "${obs[@]}"
  fi
  if selection_has gateway; then
    dry_run_cmd docker compose --project-directory "${ROOT}" --project-name "${PROJECT_NAME}" -f "${COMPOSE_FILE}" up -d --force-recreate gateway
  fi
}

finish_action() {
  local done_word="$1"
  if [[ "${DRY_RUN}" == true ]]; then
    log "Dry run finished. No containers were changed."
    return 0
  fi
  ok "${done_word} $(join_services)."
}

run_follow() {
  local rc=0
  INTERRUPTED=false
  trap 'INTERRUPTED=true; log "Log follow stopped."' INT
  "$@" || rc=$?
  trap - INT
  if [[ "${INTERRUPTED}" == true ]]; then
    return 130
  fi
  return "${rc}"
}

cmd_status() {
  narrow_all_to_enabled
  if ! compose_model_ready; then
    explain_missing_token
    warn "Compose cannot load the project. Showing docker ps for the selected services."
    show_status_fallback || return 1
    return 0
  fi
  if selection_needs_observability && ! observability_enabled; then
    explain_missing_profile
    warn "Showing docker ps for the selected services."
    show_status_fallback || return 1
    return 0
  fi
  dc ps -a "${SELECTED[@]}"
}

cmd_start() {
  narrow_all_to_enabled
  ensure_profile_for_selection || return $?
  if selection_has grafana; then
    log "Starting Grafana also starts Prometheus and the gateway."
  elif selection_has prometheus; then
    log "Starting Prometheus waits until the gateway is healthy."
  fi
  if ! compose_model_ready; then
    if selection_needs_observability; then
      explain_missing_token
      err "Start and restart of Prometheus or Grafana stay on Compose so dependencies and the scrape secret are applied."
      err "Set GATEWAY_METRICS_SCRAPE_TOKEN in ${ENV_FILE}, then retry. 33pol alone can still start with: ./33pol-stack.sh start 33pol"
      return 2
    fi
    explain_missing_token
    warn "Compose cannot load this project. Starting the existing 33pol container with docker start."
    docker_lifecycle start || return $?
    wait_for_selected || return $?
    finish_action "Started"
    return 0
  fi
  dc up -d "${SELECTED[@]}" || {
    err "Start failed."
    err "If Prometheus stays Created, set GATEWAY_METRICS_SCRAPE_TOKEN in ${ENV_FILE} and recreate the stack."
    return 1
  }
  wait_for_selected || return $?
  finish_action "Started"
}

cmd_stop() {
  local label
  narrow_all_to_enabled
  ensure_profile_for_selection || return $?
  label="$(join_services)"
  confirm "Stop ${label}? Running requests will be interrupted." || return 2
  if selection_has gateway; then
    warn "Stopping the gateway leaves Prometheus unable to scrape until the gateway is started again."
  fi
  if ! compose_model_ready; then
    explain_missing_token
    warn "Compose cannot load this project. Stopping existing containers with docker stop."
    docker_lifecycle stop || return $?
    finish_action "Stopped"
    return 0
  fi
  dc stop "${SELECTED[@]}" || {
    err "Stop failed."
    return 1
  }
  finish_action "Stopped"
}

cmd_restart() {
  local label
  narrow_all_to_enabled
  ensure_profile_for_selection || return $?
  label="$(join_services)"
  confirm "Restart ${label}? Open connections will drop." || return 2
  if ! compose_model_ready; then
    if selection_needs_observability; then
      explain_missing_token
      err "Start and restart of Prometheus or Grafana stay on Compose so dependencies and the scrape secret are applied."
      err "Set GATEWAY_METRICS_SCRAPE_TOKEN in ${ENV_FILE}, then retry. 33pol alone can still restart with: ./33pol-stack.sh restart 33pol"
      return 2
    fi
    explain_missing_token
    warn "Compose cannot load this project. Restarting the existing 33pol container with docker restart."
    docker_lifecycle restart || return $?
    wait_for_selected || return $?
    finish_action "Restarted"
    return 0
  fi
  dc restart "${SELECTED[@]}" || {
    err "Restart failed. If a container was never started, run start instead of restart."
    return 1
  }
  wait_for_selected || return $?
  finish_action "Restarted"
}

cmd_rebuild() {
  local label svc gateway="false"
  local -a obs=()
  narrow_all_to_enabled
  ensure_profile_for_selection || return $?
  label="$(join_services)"
  confirm "Rebuild and recreate ${label}? Named volumes are kept. In-flight gateway requests will drop." || return 2
  if ! compose_model_ready; then
    explain_missing_token
    print_rebuild_plan
    return 2
  fi
  for svc in "${SELECTED[@]}"; do
    if [[ "${svc}" == gateway ]]; then
      gateway="true"
    else
      obs+=("${svc}")
    fi
  done
  if [[ "${gateway}" == true ]]; then
    log "Building the 33pol image before recreating containers."
    dc build gateway || {
      err "Image build failed. Running containers were left in place."
      return 1
    }
  fi
  if [[ ${#obs[@]} -gt 0 ]]; then
    log "Pulling ${obs[*]} before recreating containers."
    dc pull "${obs[@]}" || {
      err "Image pull failed. Running containers were left in place."
      return 1
    }
    log "Recreating ${obs[*]} before the gateway."
    dc up -d --force-recreate "${obs[@]}" || {
      err "Recreate failed. The gateway container was left in place."
      err "Check ./33pol-stack.sh health 33pol and ./33pol-stack.sh logs 33pol."
      return 1
    }
  fi
  if [[ "${gateway}" == true ]]; then
    log "Recreating 33pol (gateway)."
    dc up -d --force-recreate gateway || {
      err "Gateway recreate failed. Volumes were not removed."
      if [[ ${#obs[@]} -gt 0 ]]; then
        err "Prometheus and Grafana were already recreated."
      fi
      err "Check ./33pol-stack.sh logs 33pol and ./33pol-stack.sh health 33pol."
      return 1
    }
  fi
  wait_for_selected || return $?
  finish_action "Rebuilt"
}

cmd_logs() {
  local rc=0
  narrow_all_to_enabled
  if ! compose_model_ready || { selection_needs_observability && ! observability_enabled; }; then
    if ! compose_model_ready; then
      explain_missing_token
    else
      explain_missing_profile
    fi
    warn "Showing docker logs for the selected containers."
    log_containers_fallback || rc=$?
    if [[ "${INTERRUPTED}" == true ]]; then
      return 0
    fi
    return "${rc}"
  fi
  if [[ "${FOLLOW}" == true ]]; then
    run_follow dc logs -f --tail "${TAIL}" "${SELECTED[@]}" || rc=$?
    if [[ "${rc}" -eq 130 ]]; then
      return 0
    fi
    return "${rc}"
  fi
  dc logs --tail "${TAIL}" "${SELECTED[@]}"
}

log_containers_fallback() {
  local svc id rc=0
  for svc in "${SELECTED[@]}"; do
    if [[ "${DRY_RUN}" == true ]]; then
      id="$(container_id "${svc}")"
      if [[ -z "${id}" ]]; then
        log "No container for $(display_name "${svc}"). docker logs was not run."
      else
        dry_run_cmd docker logs --tail "${TAIL}" "${id}"
      fi
      continue
    fi
    id="$(container_id "${svc}")"
    if [[ -z "${id}" ]]; then
      warn "No container for $(display_name "${svc}")."
      rc=1
      continue
    fi
    step "Logs: $(display_name "${svc}")"
    if [[ "${FOLLOW}" == true ]]; then
      run_follow docker logs -f --tail "${TAIL}" "${id}" || rc=$?
      if [[ "${rc}" -eq 130 ]]; then
        return 0
      fi
    else
      docker logs --tail "${TAIL}" "${id}" || rc=$?
    fi
  done
  return "${rc}"
}

health_urls_for() {
  local svc="$1" host port
  case "${svc}" in
    gateway)
      host="$(probe_host GATEWAY_BIND)"
      port="$(port_value GATEWAY_PORT 8080)" || return 1
      printf '%s|%s\n' "33pol live" "http://${host}:${port}/health/live"
      printf '%s|%s\n' "33pol ready" "http://${host}:${port}/health/ready"
      ;;
    prometheus)
      host="$(probe_host PROMETHEUS_BIND)"
      port="$(port_value PROMETHEUS_PORT 9090)" || return 1
      printf '%s|%s\n' "prometheus" "http://${host}:${port}/-/healthy"
      ;;
    grafana)
      host="$(probe_host GRAFANA_BIND)"
      port="$(port_value GRAFANA_PORT 3000)" || return 1
      printf '%s|%s\n' "grafana" "http://${host}:${port}/api/health"
      ;;
    *)
      err "No health URL for ${svc}"
      return 2
      ;;
  esac
}

cmd_health() {
  local svc id summary rc=0 live_ok="false" line name url urls=""
  narrow_all_to_enabled
  if selection_needs_observability && ! observability_enabled; then
    explain_missing_profile
    return 2
  fi
  for svc in "${SELECTED[@]}"; do
    step "Health: $(display_name "${svc}")"
    if [[ "${DRY_RUN}" != true ]]; then
      id="$(container_id "${svc}")"
      if [[ -z "${id}" ]]; then
        err "$(display_name "${svc}") has no container."
        if observability_enabled && ! token_is_set && [[ "${svc}" != gateway ]]; then
          explain_missing_token
        else
          err "Start it with: ./33pol-stack.sh start $(display_name "${svc}")"
        fi
        rc=1
      else
        summary="$(container_summary "${id}")"
        log "$(display_name "${svc}") container: ${summary}"
        if [[ "${summary}" != running* ]]; then
          err "$(display_name "${svc}") is not running (${summary})."
          if observability_enabled && ! token_is_set && [[ "${svc}" != gateway ]]; then
            explain_missing_token
          else
            err "Start it with: ./33pol-stack.sh start $(display_name "${svc}")"
          fi
          rc=1
        fi
      fi
    else
      log "dry-run: skip container inspect for $(display_name "${svc}")"
    fi
    live_ok="false"
    if ! urls="$(health_urls_for "${svc}")"; then
      rc=1
      continue
    fi
    while IFS= read -r line; do
      [[ -n "${line}" ]] || continue
      name="${line%%|*}"
      url="${line#*|}"
      if [[ "${name}" == "33pol live" ]]; then
        if check_http "${name}" "${url}"; then
          live_ok="true"
        else
          rc=1
        fi
      elif [[ "${name}" == "33pol ready" ]]; then
        if check_http "${name}" "${url}"; then
          :
        else
          rc=1
          if [[ "${live_ok}" == true ]]; then
            err "The gateway process is up but not ready. Prometheus waits until the gateway is healthy."
            err "Check ./33pol-stack.sh logs 33pol and ${url}."
          fi
        fi
      else
        check_http "${name}" "${url}" || rc=1
      fi
    done <<< "${urls}"
  done
  if [[ "${rc}" -ne 0 ]]; then
    return 1
  fi
  ok "Health checks passed for $(join_services)."
}

diagnose_one() {
  local svc="$1" id cport rc=0
  step "State: $(display_name "${svc}")"
  cport="$(container_port "${svc}")"
  if [[ "${DRY_RUN}" == true ]]; then
    id="$(container_id "${svc}")"
    if [[ -z "${id}" ]]; then
      log "No container for $(display_name "${svc}"). Inspect, stats, and port were not run."
      return 0
    fi
    dry_run_cmd docker inspect "${id}"
    dry_run_cmd docker stats --no-stream "${id}"
    dry_run_cmd docker port "${id}" "${cport}"
    return 0
  fi
  id="$(container_id "${svc}")"
  if [[ -z "${id}" ]]; then
    warn "No container for $(display_name "${svc}"). Start it with: ./33pol-stack.sh start $(display_name "${svc}")"
    return 1
  fi
  container_summary "${id}"
  if docker inspect --format '{{.State.Status}}' "${id}" | grep -qx running; then
    step "Resources: $(display_name "${svc}")"
    docker stats --no-stream --format 'table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.NetIO}}\t{{.BlockIO}}\t{{.PIDs}}' "${id}"
  else
    warn "$(display_name "${svc}") is not running, so resource stats were skipped."
    rc=1
  fi
  step "Networks: $(display_name "${svc}")"
  docker inspect --format '{{range $name, $net := .NetworkSettings.Networks}}{{$name}} {{$net.IPAddress}}{{println}}{{end}}' "${id}"
  step "Ports: $(display_name "${svc}")"
  if compose_model_ready && [[ "${DRY_RUN}" != true ]]; then
    dc port "${svc}" "${cport}" || warn "Port ${cport} is not published. The container may be stopped."
  else
    docker port "${id}" "${cport}" || warn "Port ${cport} is not published. The container may be stopped."
  fi
  step "Mounts: $(display_name "${svc}")"
  docker inspect --format '{{range .Mounts}}{{.Type}} {{if .Name}}{{.Name}}{{else}}{{.Source}}{{end}} -> {{.Destination}}{{println}}{{end}}' "${id}"
  return "${rc}"
}

check_profile_and_token() {
  local profiles
  profiles="$(env_or_file COMPOSE_PROFILES)"
  log "COMPOSE_PROFILES=${profiles:-empty}"
  if observability_enabled; then
    if token_is_set; then
      ok "GATEWAY_METRICS_SCRAPE_TOKEN is set."
      return 0
    fi
    explain_missing_token
    return 2
  fi
  warn "COMPOSE_PROFILES does not include observability or full. Prometheus and Grafana will not start."
  warn "For those services, set COMPOSE_PROFILES=observability in ${ENV_FILE}."
  return 0
}

check_compose_config() {
  if observability_enabled && ! token_is_set; then
    warn "Skipped docker compose config because the scrape token is missing."
    return 2
  fi
  log "Checking Docker Compose configuration (output is hidden so secrets are not printed)."
  if [[ "${DRY_RUN}" == true ]]; then
    dry_run_cmd docker compose --project-directory "${ROOT}" --project-name "${PROJECT_NAME}" -f "${COMPOSE_FILE}" config --quiet
    log "Running the read-only config check anyway. --dry-run does not skip validation."
  fi
  if docker compose --project-directory "${ROOT}" --project-name "${PROJECT_NAME}" -f "${COMPOSE_FILE}" config --quiet >/dev/null; then
    ok "Docker Compose configuration is valid."
    return 0
  fi
  err "Docker Compose rejected the project configuration."
  err "If the message names GATEWAY_METRICS_SCRAPE_TOKEN, add that variable to ${ENV_FILE} and retry."
  return 2
}

cmd_validate() {
  check_profile_and_token || return $?
  check_compose_config
}

cmd_diagnose() {
  local svc rc=0
  narrow_all_to_enabled
  for svc in "${SELECTED[@]}"; do
    diagnose_one "${svc}" || rc=1
  done
  step "Project volumes"
  log "Named volumes used by this stack: gateway-data, prometheus-data, grafana-data."
  if [[ "${DRY_RUN}" == true ]]; then
    dry_run_cmd docker volume ls --filter "label=com.docker.compose.project=${PROJECT_NAME}"
    step "Project networks"
    dry_run_cmd docker network ls --filter "label=com.docker.compose.project=${PROJECT_NAME}"
  else
    docker volume ls --filter "label=com.docker.compose.project=${PROJECT_NAME}" || rc=1
    step "Project networks"
    docker network ls --filter "label=com.docker.compose.project=${PROJECT_NAME}" || rc=1
  fi
  step "Compose configuration"
  if ! cmd_validate; then
    return 2
  fi
  return "${rc}"
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --yes|-y) ASSUME_YES=true ;;
      --dry-run) DRY_RUN=true ;;
      --no-color) FLAG_NO_COLOR=true ;;
      --follow|-f) FOLLOW=true ;;
      --help|-h) COMMAND="help" ;;
      --log-file)
        shift
        if [[ $# -eq 0 || -z "${1:-}" ]]; then
          err "--log-file requires a path."
          exit 2
        fi
        LOG_FILE="$1"
        ;;
      --tail)
        shift
        if [[ $# -eq 0 || -z "${1:-}" ]]; then
          err "--tail requires a number."
          exit 2
        fi
        TAIL="$1"
        ;;
      help|status|start|stop|restart|rebuild|logs|health|diagnose|validate)
        if [[ -n "${COMMAND}" && "${COMMAND}" != "help" ]]; then
          err "Unexpected extra command: $1"
          usage >&2
          exit 2
        fi
        if [[ "${COMMAND}" != "help" ]]; then
          COMMAND="$1"
        fi
        ;;
      *)
        if [[ -n "${COMMAND}" && -z "${TARGET}" && "${COMMAND}" != "help" ]]; then
          TARGET="$1"
        else
          err "Unknown argument: $1"
          usage >&2
          exit 2
        fi
        ;;
    esac
    shift
  done
}

validate_cli() {
  if [[ -n "${TAIL}" && ! "${TAIL}" =~ ^[0-9]+$ ]]; then
    err "--tail expects a non-negative integer."
    exit 2
  fi
  if [[ "${FOLLOW}" == true && "${COMMAND}" != "logs" ]]; then
    err "--follow is only valid with logs."
    exit 2
  fi
  if [[ "${TAIL}" != "${DEFAULT_TAIL}" && "${COMMAND}" != "logs" && -n "${COMMAND}" && "${COMMAND}" != "help" ]]; then
    err "--tail is only valid with logs."
    exit 2
  fi
  case "${COMMAND}" in
    start|stop|restart|rebuild|logs)
      if [[ -z "${TARGET}" ]]; then
        err "Command '${COMMAND}' needs a service: 33pol, prometheus, grafana, or all."
        exit 2
      fi
      ;;
    validate)
      if [[ -n "${TARGET}" ]]; then
        err "validate does not take a service name."
        exit 2
      fi
      ;;
  esac
}

pause_menu() {
  local _unused
  read -r -p "Press Enter to continue... " _unused || true
}

prompt_log_options() {
  local ans tail saved_tail
  saved_tail="${TAIL}"
  FOLLOW=false
  read -r -p "Follow logs? [y/N] " ans || true
  if [[ "${ans}" == [yY] || "${ans}" == [yY][eE][sS] ]]; then
    FOLLOW=true
  fi
  read -r -p "Tail lines [${saved_tail}]: " tail || true
  if [[ -n "${tail}" ]]; then
    if [[ "${tail}" =~ ^[0-9]+$ ]]; then
      TAIL="${tail}"
    else
      warn "Invalid tail '${tail}'. Using ${saved_tail}."
      TAIL="${saved_tail}"
    fi
  fi
}

pick_service() {
  local choice
  while true; do
    printf '\n%sSelect a service%s\n' "${C_BOLD}" "${C_RESET}"
    printf '  1) 33pol (gateway only)\n'
    printf '  2) prometheus (start also waits for a healthy gateway)\n'
    printf '  3) grafana (start also starts prometheus and the gateway)\n'
    printf '  4) all three\n'
    printf '  b) back\n'
    read -r -p "> " choice || return 1
    case "${choice}" in
      1) services_for 33pol; return 0 ;;
      2) services_for prometheus; return 0 ;;
      3) services_for grafana; return 0 ;;
      4) services_for all; return 0 ;;
      b|B) return 1 ;;
      *) warn "Enter 1, 2, 3, 4, or b." ;;
    esac
  done
}

run_menu_action() {
  local fn="$1" rc=0
  TOKEN_EXPLAINED=false
  PROFILE_EXPLAINED=false
  CANCELLED=false
  INTERRUPTED=false
  if ! pick_service; then
    return 0
  fi
  if [[ "${fn}" == "cmd_logs" ]]; then
    prompt_log_options
  fi
  "${fn}" || rc=$?
  FOLLOW=false
  TAIL="${DEFAULT_TAIL}"
  if [[ "${CANCELLED}" == true || "${INTERRUPTED}" == true ]]; then
    :
  elif [[ "${rc}" -ne 0 ]]; then
    warn "Command failed (exit ${rc}). Use the message above to recover, or open Help."
  fi
  pause_menu
}

menu_banner() {
  local profiles token_state
  profiles="$(env_or_file COMPOSE_PROFILES)"
  if token_is_set; then
    token_state="set"
  else
    token_state="missing"
  fi
  printf '\n%s33pol stack%s\n' "${C_BOLD}" "${C_RESET}"
  printf 'Project: %s\n' "${ROOT}"
  printf 'Compose: docker-compose.yml (includes deploy/docker/docker-compose.yml)\n'
  printf 'Profiles: %s\n' "${profiles:-empty}"
  printf 'Scrape token: %s\n' "${token_state}"
  if [[ "${DRY_RUN}" == true ]]; then
    printf '%sDry run: Compose changes are printed and not applied.%s\n' "${C_YELLOW}" "${C_RESET}"
  fi
}

menu_loop() {
  local choice
  while true; do
    menu_banner
    printf '\n%sMain menu%s\n' "${C_BOLD}" "${C_RESET}"
    printf '  1) Status\n'
    printf '  2) Start\n'
    printf '  3) Stop\n'
    printf '  4) Restart\n'
    printf '  5) Rebuild\n'
    printf '  6) Logs\n'
    printf '  7) Health checks\n'
    printf '  8) Diagnostics\n'
    printf '  9) Validate configuration\n'
    printf '  h) Help\n'
    printf '  q) Quit\n'
    read -r -p "> " choice || {
      printf '\n'
      return 0
    }
    case "${choice}" in
      1) run_menu_action cmd_status ;;
      2) run_menu_action cmd_start ;;
      3) run_menu_action cmd_stop ;;
      4) run_menu_action cmd_restart ;;
      5) run_menu_action cmd_rebuild ;;
      6) run_menu_action cmd_logs ;;
      7) run_menu_action cmd_health ;;
      8) run_menu_action cmd_diagnose ;;
      9)
        TOKEN_EXPLAINED=false
        PROFILE_EXPLAINED=false
        cmd_validate || warn "Validation failed (exit $?). The message above is the next step."
        pause_menu
        ;;
      h|H)
        usage
        pause_menu
        ;;
      q|Q)
        log "Bye."
        return 0
        ;;
      "") ;;
      *) warn "Enter a menu number, h, or q." ;;
    esac
  done
}

dispatch() {
  case "${COMMAND}" in
    status)
      services_for "${TARGET:-all}" || return 2
      cmd_status
      ;;
    start)
      services_for "${TARGET}" || return 2
      cmd_start
      ;;
    stop)
      services_for "${TARGET}" || return 2
      cmd_stop
      ;;
    restart)
      services_for "${TARGET}" || return 2
      cmd_restart
      ;;
    rebuild)
      services_for "${TARGET}" || return 2
      cmd_rebuild
      ;;
    logs)
      services_for "${TARGET}" || return 2
      cmd_logs
      ;;
    health)
      services_for "${TARGET:-all}" || return 2
      cmd_health
      ;;
    diagnose)
      services_for "${TARGET:-all}" || return 2
      cmd_diagnose
      ;;
    validate)
      cmd_validate
      ;;
    *)
      err "Unknown command: ${COMMAND}"
      usage >&2
      return 2
      ;;
  esac
}

main() {
  parse_args "$@"
  setup_colors
  open_log
  if [[ -z "${COMMAND}" ]]; then
    if [[ -t 0 && -t 1 ]]; then
      preflight_runtime || exit $?
      menu_loop
      exit 0
    fi
    usage >&2
    exit 2
  fi
  if [[ "${COMMAND}" == "help" ]]; then
    usage
    exit 0
  fi
  validate_cli
  preflight_runtime || exit $?
  dispatch || exit $?
  exit 0
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  main "$@"
fi

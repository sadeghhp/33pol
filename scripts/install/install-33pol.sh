#!/usr/bin/env bash
# 33pol interactive deployment installer.
set -euo pipefail

INSTALL_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
source "${INSTALL_SCRIPT_DIR}/lib/common.sh"
# shellcheck source=lib/checks.sh
source "${INSTALL_SCRIPT_DIR}/lib/checks.sh"
# shellcheck source=lib/config.sh
source "${INSTALL_SCRIPT_DIR}/lib/config.sh"
# shellcheck source=lib/git.sh
source "${INSTALL_SCRIPT_DIR}/lib/git.sh"
# shellcheck source=lib/compose.sh
source "${INSTALL_SCRIPT_DIR}/lib/compose.sh"
# shellcheck source=lib/verify.sh
source "${INSTALL_SCRIPT_DIR}/lib/verify.sh"
# shellcheck source=lib/prompt.sh
source "${INSTALL_SCRIPT_DIR}/lib/prompt.sh"

INSTALL_YES=false
INSTALL_DRY_RUN=false
INSTALL_FORCE_CONFIG=false
INSTALL_ENV_OVERRIDE=""
INSTALL_SUBCMD="install"
INSTALL_VOLUMES=false
INSTALL_LOGS_SERVICE=""
INSTALL_REAPPLY_SERVICE=""
INSTALL_REAPPLY_FORCE=false
INSTALL_REAPPLY_BUILD=false
INSTALL_REAPPLY_NO_WAIT=false

usage() {
  cat <<'EOF'
Usage: install-33pol.sh <command> [options]

Commands:
  install     Clone/build/configure and start 33pol (default)
  upgrade     git pull, rebuild gateway, docker compose up -d
  reapply     Recreate containers to apply .env changes (quota, ports, profiles)
  status      docker compose ps and gateway health
  logs        docker compose logs -f [service]
  uninstall   docker compose down [--volumes]
  doctor      Run prerequisite checks

Install options:
  -y, --yes              Non-interactive with generated defaults
  --profile PROFILE      gpu-gateway | gpu-observability | full-stack
  --install-dir PATH     Install/clone directory (default: ~/33pol)
  --git-url URL          Git remote (default: https://github.com/sadeghhp/33pol.git)
  --git-ref REF          Branch or tag (default: main)
  --env-file PATH        Merge overrides into generated .env
  --bind-gateway ADDR    Gateway bind (default: 0.0.0.0)
  --force-config         Overwrite models.json for gpu-gateway
  --dry-run              Print actions without executing compose

Reapply options:
  --service NAME         Recreate only this service (e.g. gateway)
  --force-recreate       Always recreate containers (even if compose sees no diff)
  --build                Rebuild images before recreating
  --no-wait              Skip gateway health wait after reapply

Examples:
  ./scripts/install-33pol.sh install --yes --profile gpu-gateway
  ./scripts/install-33pol.sh doctor
  ./scripts/install-33pol.sh upgrade
  ./scripts/install-33pol.sh reapply --service gateway
  ./scripts/install-33pol.sh reapply --force-recreate
EOF
}

parse_global_flags() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      -y|--yes) INSTALL_YES=true; shift ;;
      --profile)
        INSTALL_PROFILE="$2"
        install_validate_profile "${INSTALL_PROFILE}" || die "Invalid --profile: ${INSTALL_PROFILE} (gpu-gateway | gpu-observability | full-stack)"
        shift 2
        ;;
      --install-dir) INSTALL_DIR="$2"; shift 2 ;;
      --git-url) INSTALL_GIT_URL="$2"; shift 2 ;;
      --git-ref) INSTALL_GIT_REF="$2"; shift 2 ;;
      --env-file) INSTALL_ENV_OVERRIDE="$2"; shift 2 ;;
      --bind-gateway) INSTALL_GATEWAY_BIND="$2"; shift 2 ;;
      --force-config) INSTALL_FORCE_CONFIG=true; shift ;;
      --dry-run) INSTALL_DRY_RUN=true; shift ;;
      --volumes) INSTALL_VOLUMES=true; shift ;;
      --service) INSTALL_REAPPLY_SERVICE="$2"; shift 2 ;;
      --force-recreate) INSTALL_REAPPLY_FORCE=true; shift ;;
      --build) INSTALL_REAPPLY_BUILD=true; shift ;;
      --no-wait) INSTALL_REAPPLY_NO_WAIT=true; shift ;;
      -h|--help) usage; exit 0 ;;
      *)
        if [[ "${INSTALL_SUBCMD}" == logs && -z "${INSTALL_LOGS_SERVICE}" && "${1}" != -* ]]; then
          INSTALL_LOGS_SERVICE="$1"
          shift
        else
          die "Unknown option or argument: $1"
        fi
        ;;
    esac
  done
}

parse_args() {
  if [[ $# -eq 0 ]]; then
    INSTALL_SUBCMD=install
    return
  fi

  case "$1" in
    install|upgrade|reapply|status|logs|uninstall|doctor)
      INSTALL_SUBCMD="$1"
      shift
      ;;
    -h|--help|help)
      usage
      exit 0
      ;;
    -*)
      INSTALL_SUBCMD=install
      ;;
    *)
      die "Unknown command: $1. Run with --help for usage."
      ;;
  esac

  parse_global_flags "$@"
}

cmd_doctor() {
  init_logging
  install_run_doctor "${INSTALL_GATEWAY_PORT:-8080}" "${INSTALL_POSTGRES_PORT:-5432}"
  if [[ -f "${INSTALL_STATE_DIR}/install.state.json" ]]; then
    log "State file: ${INSTALL_STATE_DIR}/install.state.json"
    if dir_state="$(install_read_state_install_dir 2>/dev/null)"; then
      log "Last install dir: ${dir_state}"
    fi
  fi
}

resolve_install_dir() {
  local detected
  if [[ -z "${INSTALL_DIR:-}" ]]; then
    if detected="$(install_find_repo_root "$(pwd)" 2>/dev/null)"; then
      INSTALL_DIR="${detected}"
      log "Using current repository: ${INSTALL_DIR}"
      return 0
    fi
    INSTALL_DIR="${DEFAULT_INSTALL_DIR}"
  fi
}

handle_existing_install() {
  if [[ ! -f "${INSTALL_DIR}/.env" ]]; then
    return 0
  fi
  if [[ ! -f "${INSTALL_STATE_DIR}/install.state.json" ]]; then
    return 0
  fi
  if [[ "${INSTALL_YES:-false}" == true ]]; then
    return 0
  fi
  echo "Existing installation detected at ${INSTALL_DIR}" >&2
  echo "  1) upgrade (keep data, pull and rebuild)" >&2
  echo "  2) reconfigure (rewrite .env)" >&2
  echo "  3) abort" >&2
  local choice
  read -rp "Choice [3]: " choice
  case "${choice:-3}" in
    1)
      INSTALL_SUBCMD=upgrade
      return 0
      ;;
    2) return 0 ;;
    *) die "Aborted." ;;
  esac
}

cmd_install() {
  init_logging
  resolve_install_dir
  handle_existing_install
  if [[ "${INSTALL_SUBCMD}" == upgrade ]]; then
    cmd_upgrade
    return
  fi

  install_resolve_install_config

  install_run_doctor "${INSTALL_GATEWAY_PORT}" "${INSTALL_POSTGRES_PORT}"

  if [[ ! -f "${INSTALL_DIR}/33pol.sln" ]]; then
    install_clone_or_update "${INSTALL_GIT_URL:-${DEFAULT_GIT_URL}}" "${INSTALL_DIR}" "${INSTALL_GIT_REF:-main}"
  elif [[ -d "${INSTALL_DIR}/.git" ]]; then
    log "Repository present at ${INSTALL_DIR}"
  fi

  if [[ "${INSTALL_DRY_RUN}" != true && ! -f "${INSTALL_DIR}/33pol.sln" ]]; then
    die "33pol.sln not found in ${INSTALL_DIR}"
  fi

  local env_content
  env_content="$(install_build_env_content \
    "${INSTALL_PROFILE}" \
    "${INSTALL_GATEWAY_PORT}" \
    "${INSTALL_GATEWAY_BIND}" \
    "${INSTALL_ADMIN_KEY}" \
    "${INSTALL_POSTGRES_USER}" \
    "${INSTALL_POSTGRES_PASSWORD}" \
    "${INSTALL_POSTGRES_BIND}" \
    "${INSTALL_POSTGRES_PORT}" \
    "${INSTALL_ASPNET_ENV}")"

  if [[ -n "${INSTALL_ENV_OVERRIDE}" ]]; then
    env_content="$(install_merge_env_file "${env_content}" "${INSTALL_ENV_OVERRIDE}")"
  fi

  if [[ "${INSTALL_DRY_RUN}" != true ]]; then
    echo "${env_content}" | redact_env_preview
    if [[ "${INSTALL_YES}" != true ]] && ! confirm "Write .env and continue?"; then
      die "Aborted."
    fi
  else
    log "[dry-run] .env preview:"
    echo "${env_content}" | redact_env_preview
  fi

  install_write_env_file "${INSTALL_DIR}" "${env_content}"

  if [[ "${INSTALL_PROFILE}" == gpu-gateway || "${INSTALL_PROFILE}" == gpu-observability ]]; then
    if ! install_validate_model_id "${INSTALL_GPU_MODEL_ID:-local-llm}"; then
      die "Invalid model id for ${INSTALL_PROFILE}"
    fi
    install_seed_models_gpu "${INSTALL_DIR}" \
      "${INSTALL_GPU_MODEL_ID:-local-llm}" \
      "${INSTALL_GPU_UPSTREAM_PORT:-8000}" \
      "${INSTALL_FORCE_CONFIG}"
    install_probe_host_upstream "${INSTALL_GPU_UPSTREAM_PORT:-8000}"
  fi

  if [[ "${INSTALL_DRY_RUN}" == true ]]; then
    log "[dry-run] Skipping docker compose build/up and health checks."
    log "[dry-run] Install plan complete for ${INSTALL_DIR}"
    return 0
  fi

  install_compose_build "${INSTALL_DIR}"
  install_compose_up "${INSTALL_DIR}"
  install_wait_for_gateway "${INSTALL_GATEWAY_PORT}"
  install_run_verify_script "${INSTALL_DIR}" "${INSTALL_PROFILE}"
  install_write_state_file "${INSTALL_DIR}" "${INSTALL_PROFILE}" "${INSTALL_GIT_REF:-main}" "${INSTALL_GATEWAY_PORT}"

  local base="http://127.0.0.1:${INSTALL_GATEWAY_PORT}"
  log "Install complete."
  log "  Gateway:  ${base}"
  log "  Admin UI: ${base}/admin"
  log "  State:    ${INSTALL_STATE_DIR}/install.state.json"
  log "  Log:      ${INSTALL_LOG_FILE}"
  log "Create inference API keys in Admin → API keys."
}

cmd_upgrade() {
  init_logging
  INSTALL_DIR="${INSTALL_DIR:-$(install_read_state_install_dir 2>/dev/null || echo "${DEFAULT_INSTALL_DIR}")}"
  if [[ ! -f "${INSTALL_DIR}/.env" ]]; then
    die "No .env in ${INSTALL_DIR}; run install first."
  fi
  if [[ ! -f "${INSTALL_DIR}/33pol.sln" ]]; then
    die "Install directory does not look like 33pol: ${INSTALL_DIR}"
  fi
  install_load_env "${INSTALL_DIR}"
  if [[ -d "${INSTALL_DIR}/.git" ]]; then
    install_git_pull "${INSTALL_DIR}" || die "git pull failed; resolve conflicts and retry"
  else
    log "Skipping git pull (not a git clone)"
  fi
  install_compose_build "${INSTALL_DIR}"
  install_compose_up "${INSTALL_DIR}"
  install_wait_for_gateway "${GATEWAY_PORT:-8080}"
  local profile="gpu-gateway"
  if [[ "${COMPOSE_PROFILES:-}" == *full* ]]; then
    profile="full-stack"
  elif [[ "${COMPOSE_PROFILES:-}" == *observability* ]]; then
    profile="gpu-observability"
  fi
  install_run_verify_script "${INSTALL_DIR}" "${profile}"
  log "Upgrade complete."
}

cmd_reapply() {
  init_logging
  INSTALL_DIR="${INSTALL_DIR:-$(install_find_repo_root "$(pwd)" 2>/dev/null || install_read_state_install_dir 2>/dev/null || echo "${DEFAULT_INSTALL_DIR}")}"
  if [[ ! -f "${INSTALL_DIR}/.env" ]]; then
    die "No .env in ${INSTALL_DIR}; run install first."
  fi
  if [[ ! -f "${INSTALL_DIR}/docker-compose.yml" ]]; then
    die "Install directory does not look like 33pol: ${INSTALL_DIR}"
  fi
  install_load_env "${INSTALL_DIR}"
  log "Reapplying configuration from ${INSTALL_DIR}/.env"
  install_compose_reapply \
    "${INSTALL_DIR}" \
    "${INSTALL_REAPPLY_SERVICE}" \
    "${INSTALL_REAPPLY_FORCE}" \
    "${INSTALL_REAPPLY_BUILD}"
  if [[ "${INSTALL_REAPPLY_NO_WAIT}" != true ]]; then
    install_wait_for_gateway "${GATEWAY_PORT:-8080}"
  fi
  log "Reapply complete."
  log "  Gateway: http://127.0.0.1:${GATEWAY_PORT:-8080}"
}

cmd_status() {
  INSTALL_DIR="${INSTALL_DIR:-$(install_read_state_install_dir 2>/dev/null || echo "${DEFAULT_INSTALL_DIR}")}"
  install_compose_ps "${INSTALL_DIR}"
  install_load_env "${INSTALL_DIR}"
  local port="${GATEWAY_PORT:-8080}"
  if curl -sf "http://127.0.0.1:${port}/health/live" >/dev/null; then
    log "Gateway health: OK (port ${port})"
  else
    log "Gateway health: not reachable on port ${port}"
    exit 1
  fi
}

cmd_logs() {
  INSTALL_DIR="${INSTALL_DIR:-$(install_read_state_install_dir 2>/dev/null || echo "${DEFAULT_INSTALL_DIR}")}"
  install_compose_logs "${INSTALL_DIR}" "${INSTALL_LOGS_SERVICE}"
}

cmd_uninstall() {
  init_logging
  INSTALL_DIR="${INSTALL_DIR:-$(install_read_state_install_dir 2>/dev/null || echo "${DEFAULT_INSTALL_DIR}")}"
  if [[ "${INSTALL_YES}" != true ]] && ! confirm "Stop 33pol containers in ${INSTALL_DIR}?"; then
    die "Aborted."
  fi
  install_compose_down "${INSTALL_DIR}" "${INSTALL_VOLUMES}"
  log "Containers stopped."
}

main() {
  parse_args "$@"

  case "${INSTALL_SUBCMD}" in
    install) cmd_install ;;
    upgrade) cmd_upgrade ;;
    reapply) cmd_reapply ;;
    status) cmd_status ;;
    logs) cmd_logs ;;
    uninstall) cmd_uninstall ;;
    doctor) cmd_doctor ;;
    *) usage; exit 1 ;;
  esac
}

main "$@"

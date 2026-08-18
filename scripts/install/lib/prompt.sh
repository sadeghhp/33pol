#!/usr/bin/env bash
# Interactive prompts for install wizard.

# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
# shellcheck source=checks.sh
source "$(dirname "${BASH_SOURCE[0]}")/checks.sh"
# shellcheck source=config.sh
source "$(dirname "${BASH_SOURCE[0]}")/config.sh"

install_prompt_profile() {
  if [[ -n "${INSTALL_PROFILE:-}" ]]; then
    echo "${INSTALL_PROFILE}"
    return
  fi
  if [[ "${INSTALL_YES:-false}" == true ]]; then
    echo "gpu-gateway"
    return
  fi
  local choice
  echo "Select deployment profile:" >&2
  echo "  1) gpu-gateway       — gateway + embedded SQLite (remote GPU server)" >&2
  echo "  2) gpu-observability — above + Prometheus + Grafana (no mock)" >&2
  echo "  3) full-stack        — above + WireMock mock upstream (local demo)" >&2
  read -rp "Choice [1]: " choice
  choice="${choice:-1}"
  case "${choice}" in
    1|gpu-gateway) echo "gpu-gateway" ;;
    2|gpu-observability) echo "gpu-observability" ;;
    3|full-stack) echo "full-stack" ;;
    *) die "Invalid profile choice" ;;
  esac
}

install_prompt_value() {
  local var_name="$1"
  local prompt="$2"
  local default="$3"
  local secret="${4:-false}"
  local env_val="${!var_name:-}"
  if [[ -n "${env_val}" ]]; then
    printf '%s' "${env_val}"
    return
  fi
  if [[ "${INSTALL_YES:-false}" == true ]]; then
    printf '%s' "${default}"
    return
  fi
  local reply
  if [[ "${secret}" == true ]]; then
    read -rsp "${prompt} [leave empty to auto-generate]: " reply
    echo >&2
  else
    read -rp "${prompt} [${default}]: " reply
  fi
  if [[ -z "${reply}" ]]; then
    printf '%s' "${default}"
  else
    printf '%s' "${reply}"
  fi
}

install_prompt_gpu_upstream() {
  INSTALL_GPU_MODEL_ID="${INSTALL_GPU_MODEL_ID:-local-llm}"
  INSTALL_GPU_UPSTREAM_PORT="${INSTALL_GPU_UPSTREAM_PORT:-8000}"
  if [[ "${INSTALL_YES:-false}" == true ]]; then
    return 0
  fi
  echo "GPU upstream preset (OpenAI-compatible server on this host):" >&2
  echo "  1) vLLM (port 8000)" >&2
  echo "  2) Ollama (port 11434)" >&2
  echo "  3) TGI (port 8080)" >&2
  echo "  4) Custom port" >&2
  local preset
  read -rp "Preset [1]: " preset
  preset="${preset:-1}"
  case "${preset}" in
    1) INSTALL_GPU_UPSTREAM_PORT=8000 ;;
    2) INSTALL_GPU_UPSTREAM_PORT=11434 ;;
    3) INSTALL_GPU_UPSTREAM_PORT=8080 ;;
    4)
      read -rp "Custom port: " INSTALL_GPU_UPSTREAM_PORT
      install_validate_port "${INSTALL_GPU_UPSTREAM_PORT}" || die "Invalid port"
      ;;
    *) die "Invalid preset" ;;
  esac
  read -rp "Model id in registry [local-llm]: " INSTALL_GPU_MODEL_ID
  INSTALL_GPU_MODEL_ID="${INSTALL_GPU_MODEL_ID:-local-llm}"
  install_validate_model_id "${INSTALL_GPU_MODEL_ID}" || die "Invalid model id"
  if [[ "${INSTALL_GPU_UPSTREAM_PORT}" == "${INSTALL_GATEWAY_PORT:-8080}" ]]; then
    log "WARNING: GPU upstream port ${INSTALL_GPU_UPSTREAM_PORT} matches GATEWAY_PORT — pick a different gateway or upstream port"
  fi
}

install_resolve_install_config() {
  INSTALL_PROFILE="$(install_prompt_profile)"
  install_validate_profile "${INSTALL_PROFILE}" || die "Invalid profile: ${INSTALL_PROFILE} (use gpu-gateway, gpu-observability, or full-stack)"

  # Preserve INSTALL_DIR from resolve_install_dir() or --install-dir; only default when unset.
  if [[ -z "${INSTALL_DIR:-}" ]]; then
    INSTALL_DIR="${DEFAULT_INSTALL_DIR}"
  fi
  INSTALL_GIT_URL="${INSTALL_GIT_URL:-${DEFAULT_GIT_URL}}"
  INSTALL_GIT_REF="${INSTALL_GIT_REF:-main}"

  INSTALL_GATEWAY_PORT="$(install_prompt_value INSTALL_GATEWAY_PORT "Gateway port" "8080")"
  install_validate_port "${INSTALL_GATEWAY_PORT}" || die "Invalid GATEWAY_PORT"

  if [[ -z "${INSTALL_GATEWAY_BIND:-}" ]]; then
    INSTALL_GATEWAY_BIND="0.0.0.0"
    if [[ "${INSTALL_YES:-false}" != true ]]; then
      read -rp "Gateway bind address [0.0.0.0]: " INSTALL_GATEWAY_BIND
      INSTALL_GATEWAY_BIND="${INSTALL_GATEWAY_BIND:-0.0.0.0}"
    fi
  fi

  # Admin key and pepper: an existing .env's values are pre-loaded into INSTALL_ADMIN_KEY /
  # INSTALL_KEY_PEPPER by install_seed_secrets_from_existing_env (unless --rotate-secrets), so a
  # re-run never silently rotates them. Fresh values are generated only when nothing is set.
  local admin_default
  admin_default="$(install_generate_admin_key)"
  INSTALL_ADMIN_KEY="$(install_prompt_value INSTALL_ADMIN_KEY "Admin API key" "${admin_default}" true)"
  if [[ -z "${INSTALL_ADMIN_KEY}" ]]; then
    INSTALL_ADMIN_KEY="${admin_default}"
  fi

  # Key pepper hashes every stored API key. It must be strong and non-default in Production, and must
  # stay STABLE across reinstalls (rotating it invalidates existing key hashes). An existing .env's
  # value is reused automatically; export INSTALL_KEY_PEPPER to supply one explicitly.
  local pepper_default
  pepper_default="$(install_generate_secret)"
  INSTALL_KEY_PEPPER="$(install_prompt_value INSTALL_KEY_PEPPER "Key pepper (keep stable across reinstalls)" "${pepper_default}" true)"
  if [[ -z "${INSTALL_KEY_PEPPER}" ]]; then
    INSTALL_KEY_PEPPER="${pepper_default}"
  fi

  # Non-interactive secrets: the Prometheus scrape token for /metrics and the Grafana admin password.
  # Both are reused from an existing .env by install_seed_secrets_from_existing_env; otherwise generated.
  INSTALL_METRICS_SCRAPE_TOKEN="${INSTALL_METRICS_SCRAPE_TOKEN:-$(install_generate_secret)}"
  INSTALL_GRAFANA_ADMIN_PASSWORD="${INSTALL_GRAFANA_ADMIN_PASSWORD:-$(install_generate_secret)}"

  if [[ "${INSTALL_PROFILE}" == gpu-gateway || "${INSTALL_PROFILE}" == gpu-observability ]]; then
    INSTALL_ASPNET_ENV="${INSTALL_ASPNET_ENV:-Production}"
    install_prompt_gpu_upstream
  else
    INSTALL_ASPNET_ENV="${INSTALL_ASPNET_ENV:-Development}"
  fi
}

# Pre-load GATEWAY_KEY_PEPPER / GATEWAY_ADMIN_API_KEY from an existing .env into the INSTALL_* variables
# (only when the caller has not exported them) so a reconfigure/re-run keeps every stored API key hash
# valid and the seeded admin key working. Rotation is opt-in via --rotate-secrets ($2 == true).
install_seed_secrets_from_existing_env() {
  local install_dir="$1"
  local rotate="${2:-false}"
  local env_file="${install_dir}/.env"
  local val
  [[ -f "${env_file}" ]] || return 0
  if [[ "${rotate}" == true ]]; then
    log "WARNING: --rotate-secrets: generating a new key pepper and admin key. Every existing API key hash becomes invalid; re-seed the admin key with scripts/reset-admin-key.py afterwards."
  fi
  if [[ "${rotate}" != true && -z "${INSTALL_KEY_PEPPER:-}" ]] && val="$(install_read_env_var "${env_file}" GATEWAY_KEY_PEPPER)"; then
    val="$(install_strip_env_quotes "${val}")"
    if [[ -n "${val}" ]]; then
      INSTALL_KEY_PEPPER="${val}"
      log "Reusing GATEWAY_KEY_PEPPER from ${env_file} (pass --rotate-secrets to generate a new one)."
    fi
  fi
  if [[ "${rotate}" != true && -z "${INSTALL_ADMIN_KEY:-}" ]] && val="$(install_read_env_var "${env_file}" GATEWAY_ADMIN_API_KEY)"; then
    val="$(install_strip_env_quotes "${val}")"
    if [[ -n "${val}" ]]; then
      INSTALL_ADMIN_KEY="${val}"
      log "Reusing GATEWAY_ADMIN_API_KEY from ${env_file}."
    fi
  fi
  # Grafana persists its admin password in grafana-data after first start; keep the .env value in sync.
  if [[ -z "${INSTALL_GRAFANA_ADMIN_PASSWORD:-}" ]] && val="$(install_read_env_var "${env_file}" GRAFANA_ADMIN_PASSWORD)"; then
    val="$(install_strip_env_quotes "${val}")"
    [[ -n "${val}" ]] && INSTALL_GRAFANA_ADMIN_PASSWORD="${val}"
  fi
  if [[ -z "${INSTALL_METRICS_SCRAPE_TOKEN:-}" ]] && val="$(install_read_env_var "${env_file}" GATEWAY_METRICS_SCRAPE_TOKEN)"; then
    val="$(install_strip_env_quotes "${val}")"
    [[ -n "${val}" ]] && INSTALL_METRICS_SCRAPE_TOKEN="${val}"
  fi
  return 0
}

# Strip one pair of matching surrounding quotes (the format install_env_line emits for values with
# special characters) so the value round-trips through install_build_env_content unchanged.
install_strip_env_quotes() {
  local v="$1"
  if [[ ${#v} -ge 2 && "${v}" == \"*\" ]]; then
    v="${v:1:${#v}-2}"
    # Undo the escaping install_env_line applies inside double quotes.
    v="${v//\\\"/\"}"
    v="${v//\\\\/\\}"
  elif [[ ${#v} -ge 2 && "${v}" == \'*\' ]]; then
    v="${v:1:${#v}-2}"
  fi
  printf '%s' "${v}"
}

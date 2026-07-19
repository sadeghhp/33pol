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

  local admin_default
  admin_default="$(install_generate_admin_key)"
  INSTALL_ADMIN_KEY="$(install_prompt_value INSTALL_ADMIN_KEY "Admin API key" "${admin_default}" true)"
  if [[ -z "${INSTALL_ADMIN_KEY}" ]]; then
    INSTALL_ADMIN_KEY="${admin_default}"
  fi

  # Key pepper hashes every stored API key. It must be strong and non-default in Production, and must
  # stay STABLE across reinstalls (rotating it invalidates existing key hashes). Reuse an existing
  # value by exporting INSTALL_KEY_PEPPER before running.
  local pepper_default
  pepper_default="$(install_generate_secret)"
  INSTALL_KEY_PEPPER="$(install_prompt_value INSTALL_KEY_PEPPER "Key pepper (keep stable across reinstalls)" "${pepper_default}" true)"
  if [[ -z "${INSTALL_KEY_PEPPER}" ]]; then
    INSTALL_KEY_PEPPER="${pepper_default}"
  fi

  if [[ "${INSTALL_PROFILE}" == gpu-gateway || "${INSTALL_PROFILE}" == gpu-observability ]]; then
    INSTALL_ASPNET_ENV="${INSTALL_ASPNET_ENV:-Production}"
    install_prompt_gpu_upstream
  else
    INSTALL_ASPNET_ENV="${INSTALL_ASPNET_ENV:-Development}"
  fi
}

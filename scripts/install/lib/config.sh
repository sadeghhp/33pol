#!/usr/bin/env bash
# .env and models.json generation for 33pol install.

# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
# shellcheck source=checks.sh
source "$(dirname "${BASH_SOURCE[0]}")/checks.sh"

install_generate_secret() {
  openssl rand -hex 24
}

install_generate_admin_key() {
  printf 'sk-33pol-%s' "$(install_generate_secret)"
}

install_generate_password() {
  openssl rand -base64 24 | tr -d '/+=' | head -c 32
}

install_models_gpu_path() {
  local install_dir="$1"
  printf '%s/deploy/docker/config/models.json' "${install_dir}"
}

install_models_gpu_example_path() {
  local install_dir="$1"
  printf '%s/deploy/docker/config/models.gpu.json.example' "${install_dir}"
}

install_models_compose_example_path() {
  local install_dir="$1"
  printf '%s/deploy/docker/config/models.json.example' "${install_dir}"
}

# Create deploy/docker/config/models.json from example when missing (non-destructive).
install_ensure_models_json() {
  local install_dir="$1"
  local dest example
  dest="$(install_models_gpu_path "${install_dir}")"
  example="$(install_models_compose_example_path "${install_dir}")"
  if [[ -f "${dest}" ]]; then
    return 0
  fi
  if [[ ! -f "${example}" ]]; then
    log "No models.json at ${dest} and no ${example}; copy models.json.example after clone"
    return 0
  fi
  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] would copy ${example} -> ${dest}"
    return 0
  fi
  cp "${example}" "${dest}"
  log "Created ${dest} from models.json.example"
}

install_upstream_secrets_path() {
  local install_dir="$1"
  printf '%s/deploy/docker/config/upstream-secrets.enc' "${install_dir}"
}

install_upstream_secrets_example_path() {
  local install_dir="$1"
  printf '%s/deploy/docker/config/upstream-secrets.enc.example' "${install_dir}"
}

# Create empty upstream-secrets.enc from example when missing (non-destructive).
install_ensure_upstream_secrets() {
  local install_dir="$1"
  local dest example
  dest="$(install_upstream_secrets_path "${install_dir}")"
  example="$(install_upstream_secrets_example_path "${install_dir}")"
  if [[ -f "${dest}" ]]; then
    return 0
  fi
  if [[ ! -f "${example}" ]]; then
    return 0
  fi
  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] would copy ${example} -> ${dest}"
    return 0
  fi
  cp "${example}" "${dest}"
  log "Created ${dest} from upstream-secrets.enc.example"
}

# Substitute model id and upstream port into GPU models template (safe JSON; no sed on user input).
install_render_models_gpu_json() {
  local example_file="$1"
  local model_id="$2"
  local upstream_port="$3"
  if ! install_validate_model_id "${model_id}"; then
    die "Invalid model id '${model_id}' (use letters, digits, ., _, /, -)"
  fi
  install_validate_port "${upstream_port}" || die "Invalid upstream port: ${upstream_port}"
  if command -v python3 >/dev/null 2>&1; then
    MODEL_ID="${model_id}" UPSTREAM_PORT="${upstream_port}" python3 - "${example_file}" <<'PY'
import json, os, sys
path = sys.argv[1]
with open(path, encoding="utf-8") as f:
    data = json.load(f)
model_id = os.environ["MODEL_ID"]
port = os.environ["UPSTREAM_PORT"]
data["models"] = [{
    "id": model_id,
    "url": f"http://host.docker.internal:{port}",
    "maxContextLength": 32768,
    "aliases": [],
}]
print(json.dumps(data, indent=2))
PY
    return
  fi
  die "python3 is required to generate models.json safely"
}

install_seed_models_gpu() {
  local install_dir="$1"
  local model_id="$2"
  local upstream_port="$3"
  local force="${4:-false}"
  local dest example
  dest="$(install_models_gpu_path "${install_dir}")"
  example="$(install_models_gpu_example_path "${install_dir}")"
  if [[ ! -f "${example}" ]]; then
    if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
      log "[dry-run] would seed models.json after clone (template not present yet)"
      return 0
    fi
    die "Missing template: ${example} (use a release that includes models.gpu.json.example)"
  fi
  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] would write ${dest} (model=${model_id}, port=${upstream_port})"
    return 0
  fi
  if [[ -f "${dest}" && "${force}" != true ]]; then
    log "Keeping existing models.json at ${dest}"
    return 0
  fi
  install_render_models_gpu_json "${example}" "${model_id}" "${upstream_port}" >"${dest}"
  log "Wrote ${dest} (GPU upstream host.docker.internal:${upstream_port})"
}

# Write KEY=value lines safe for Docker Compose .env (quote when value has metacharacters).
install_env_line() {
  local key="$1"
  local value="$2"
  if [[ "${value}" == *$'\n'* ]]; then
    die "Environment value for ${key} must not contain newlines"
  fi
  if [[ "${value}" =~ [^A-Za-z0-9._/:+-] ]]; then
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    printf '%s="%s"\n' "${key}" "${value}"
  else
    printf '%s=%s\n' "${key}" "${value}"
  fi
}

install_build_env_content() {
  local profile="$1"
  local gateway_port="$2"
  local gateway_bind="$3"
  local admin_key="$4"
  local postgres_user="$5"
  local postgres_password="$6"
  local postgres_bind="$7"
  local postgres_port="$8"
  local aspnet_env="$9"
  local compose_profiles
  compose_profiles="$(install_profile_to_compose_profiles "${profile}")"

  {
    printf '# Generated by install-33pol.sh — %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    install_env_line COMPOSE_PROFILES "${compose_profiles}"
    install_env_line POSTGRES_USER "${postgres_user}"
    install_env_line POSTGRES_PASSWORD "${postgres_password}"
    install_env_line POSTGRES_DB gateway
    install_env_line POSTGRES_BIND "${postgres_bind}"
    install_env_line POSTGRES_PORT "${postgres_port}"
    install_env_line GATEWAY_BIND "${gateway_bind}"
    install_env_line GATEWAY_PORT "${gateway_port}"
    install_env_line GATEWAY_ADMIN_API_KEY "${admin_key}"
    install_env_line MOCK_UPSTREAM_PORT 18080
    install_env_line PROMETHEUS_PORT 9090
    install_env_line GRAFANA_PORT 3000
    install_env_line GRAFANA_ADMIN_USER admin
    install_env_line GRAFANA_ADMIN_PASSWORD admin
    install_env_line ASPNETCORE_ENVIRONMENT "${aspnet_env}"
    install_env_line QUOTA_MONTHLY_TOKEN_LIMIT 10000000
  }
}

install_write_env_file() {
  local install_dir="$1"
  local content="$2"
  local env_file="${install_dir}/.env"
  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] would write ${env_file}"
    return 0
  fi
  umask 077
  if [[ -f "${env_file}" ]]; then
    cp "${env_file}" "${env_file}.bak.$(date '+%Y%m%d%H%M%S')"
  fi
  printf '%s\n' "${content}" >"${env_file}"
  chmod 600 "${env_file}"
  log "Wrote ${env_file}"
}

install_write_state_file() {
  local install_dir="$1"
  local profile="$2"
  local git_ref="$3"
  local gateway_port="$4"
  mkdir -p "${INSTALL_STATE_DIR}"
  cat >"${INSTALL_STATE_DIR}/install.state.json" <<EOF
{
  "installDir": "${install_dir}",
  "profile": "${profile}",
  "gitRef": "${git_ref}",
  "gatewayPort": ${gateway_port},
  "installedAt": "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
}
EOF
  chmod 600 "${INSTALL_STATE_DIR}/install.state.json"
}

install_read_state_install_dir() {
  local state_file="${INSTALL_STATE_DIR}/install.state.json"
  if [[ ! -f "${state_file}" ]]; then
    return 1
  fi
  if command -v python3 >/dev/null 2>&1; then
    python3 -c "import json,sys; print(json.load(open(sys.argv[1], encoding='utf-8'))['installDir'])" "${state_file}" 2>/dev/null
    return $?
  fi
  grep -o '"installDir"[[:space:]]*:[[:space:]]*"[^"]*"' "${state_file}" | head -1 | sed 's/.*"\([^"]*\)"$/\1/'
}

install_merge_env_file() {
  local generated="$1"
  local override_file="$2"
  if [[ ! -f "${override_file}" ]]; then
    printf '%s' "${generated}"
    return
  fi
  local tmp key line
  tmp="$(mktemp)"
  printf '%s\n' "${generated}" >"${tmp}"
  while IFS= read -r line || [[ -n "${line}" ]]; do
    [[ "${line}" =~ ^[[:space:]]*# ]] && continue
    [[ "${line}" =~ ^[[:space:]]*$ ]] && continue
    if [[ "${line}" =~ ^([A-Za-z_][A-Za-z0-9_]*)= ]]; then
      key="${BASH_REMATCH[1]}"
      grep -v "^${key}=" "${tmp}" >"${tmp}.new" || true
      mv "${tmp}.new" "${tmp}"
      echo "${line}" >>"${tmp}"
    fi
  done <"${override_file}"
  cat "${tmp}"
  rm -f "${tmp}"
}

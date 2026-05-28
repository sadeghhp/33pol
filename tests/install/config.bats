#!/usr/bin/env bats

setup() {
  INSTALL_SCRIPT_DIR="${BATS_TEST_DIRNAME}/../../scripts/install"
  # shellcheck source=../../scripts/install/lib/common.sh
  source "${INSTALL_SCRIPT_DIR}/lib/common.sh"
  # shellcheck source=../../scripts/install/lib/checks.sh
  source "${INSTALL_SCRIPT_DIR}/lib/checks.sh"
  # shellcheck source=../../scripts/install/lib/config.sh
  source "${INSTALL_SCRIPT_DIR}/lib/config.sh"
  REPO_ROOT="${BATS_TEST_DIRNAME}/../.."
}

@test "install_build_env_content includes COMPOSE_PROFILES full for full-stack" {
  local output
  output="$(install_build_env_content full-stack 8080 0.0.0.0 sk-admin gateway pass 0.0.0.0 5432 Development)"
  [[ "${output}" == *"COMPOSE_PROFILES=full"* ]]
  [[ "${output}" == *"GATEWAY_PORT=8080"* ]]
}

@test "install_build_env_content uses observability profile for gpu-observability" {
  local output
  output="$(install_build_env_content gpu-observability 8080 0.0.0.0 sk-admin gateway pass 0.0.0.0 5432 Production)"
  [[ "${output}" == *"COMPOSE_PROFILES=observability"* ]]
}

@test "install_build_env_content omits full profile for gpu-gateway" {
  local output
  output="$(install_build_env_content gpu-gateway 9090 127.0.0.1 sk-admin gateway pass 127.0.0.1 5432 Production)"
  [[ "${output}" == *"COMPOSE_PROFILES="* ]]
  [[ "${output}" != *"COMPOSE_PROFILES=full"* ]]
  [[ "${output}" != *"COMPOSE_PROFILES=observability"* ]]
  [[ "${output}" == *"ASPNETCORE_ENVIRONMENT=Production"* ]]
}

@test "install_render_models_gpu_json substitutes id and port" {
  if ! command -v python3 >/dev/null 2>&1; then
    skip "python3 required"
  fi
  local example="${REPO_ROOT}/deploy/docker/config/models.gpu.json.example"
  local output
  output="$(install_render_models_gpu_json "${example}" "my-model" 11434)"
  [[ "${output}" == *'"my-model"'* ]]
  [[ "${output}" == *"host.docker.internal:11434"* ]]
}

@test "install_validate_model_id rejects unsafe characters" {
  ! install_validate_model_id 'bad"id'
  ! install_validate_model_id ''
}

@test "install_validate_model_id accepts dotted and slashed ids" {
  install_validate_model_id "deepseek/deepseek-v4"
  install_validate_model_id "qwen3.5-0.8b"
}

@test "install_merge_env_file overrides keys" {
  local generated="FOO=1
BAR=2"
  local override output
  override="$(mktemp)"
  printf 'BAR=99\nBAZ=3\n' >"${override}"
  output="$(install_merge_env_file "${generated}" "${override}")"
  rm -f "${override}"
  [[ "${output}" == *"FOO=1"* ]]
  [[ "${output}" == *"BAR=99"* ]]
  [[ "${output}" == *"BAZ=3"* ]]
}

@test "install_generate_admin_key has sk-33pol prefix" {
  local output
  output="$(install_generate_admin_key)"
  [[ "${output}" == sk-33pol-* ]]
}

#!/usr/bin/env bats

setup() {
  HEALTH_SCRIPT_DIR="${BATS_TEST_DIRNAME}/../../scripts/host-health"
  # shellcheck source=../../scripts/host-health/lib/common.sh
  source "${HEALTH_SCRIPT_DIR}/lib/common.sh"
  health_init_report
}

@test "health_disk_status pass below warn threshold" {
  [ "$(health_disk_status 50 85 95)" = "pass" ]
}

@test "health_disk_status warn at warn threshold" {
  [ "$(health_disk_status 85 85 95)" = "warn" ]
}

@test "health_disk_status fail at fail threshold" {
  [ "$(health_disk_status 95 85 95)" = "fail" ]
}

@test "health_disk_status fail above fail threshold" {
  [ "$(health_disk_status 99 85 95)" = "fail" ]
}

@test "health_load_status pass under warn multiplier" {
  [ "$(health_load_status 1.0 4 2 4)" = "pass" ]
}

@test "health_load_status warn between warn and fail multiplier" {
  [ "$(health_load_status 3.0 2 2 4)" = "warn" ]
}

@test "health_load_status fail at fail multiplier" {
  [ "$(health_load_status 8.0 2 2 4)" = "fail" ]
}

@test "health_mem_avail_status pass when enough available" {
  [ "$(health_mem_avail_status 8000000 10000000 10)" = "pass" ]
}

@test "health_mem_avail_status warn when below threshold" {
  [ "$(health_mem_avail_status 500000 10000000 10)" = "warn" ]
}

@test "health_mem_avail_status pass when total is zero" {
  [ "$(health_mem_avail_status 0 0 10)" = "pass" ]
}

@test "health_final_exit_code returns 2 on fail count" {
  HEALTH_FAIL_COUNT=1
  HEALTH_WARN_COUNT=0
  HEALTH_STRICT=false
  run health_final_exit_code
  [ "$status" -eq 2 ]
}

@test "health_final_exit_code returns 1 on warn when strict" {
  HEALTH_FAIL_COUNT=0
  HEALTH_WARN_COUNT=1
  HEALTH_STRICT=true
  run health_final_exit_code
  [ "$status" -eq 1 ]
}

@test "health_final_exit_code returns 0 when clean" {
  HEALTH_FAIL_COUNT=0
  HEALTH_WARN_COUNT=0
  HEALTH_STRICT=false
  run health_final_exit_code
  [ "$status" -eq 0 ]
}

@test "health_load_config_file loads valid keys" {
  local tmp
  tmp="$(mktemp)"
  printf 'DISK_WARN_PCT=77\n' >"${tmp}"
  DISK_WARN_PCT=85
  health_load_config_file "${tmp}"
  [ "${DISK_WARN_PCT}" = "77" ]
  rm -f "${tmp}"
}

@test "health_load_config_file ignores invalid lines" {
  local tmp
  tmp="$(mktemp)"
  printf 'DISK_WARN_PCT=88\nfoo bar\n' >"${tmp}"
  DISK_WARN_PCT=85
  health_load_config_file "${tmp}"
  [ "${DISK_WARN_PCT}" = "88" ]
  rm -f "${tmp}"
}

@test "health_load_config_file rejects disallowed keys" {
  local tmp
  tmp="$(mktemp)"
  printf 'PATH=/tmp/evil\nDISK_WARN_PCT=70\n' >"${tmp}"
  DISK_WARN_PCT=85
  PATH="${PATH}"
  health_load_config_file "${tmp}"
  [ "${DISK_WARN_PCT}" = "70" ]
  rm -f "${tmp}"
}

@test "health_validate_host_token accepts hostname and ipv4" {
  health_validate_host_token example.com
  health_validate_host_token 1.1.1.1
}

@test "health_validate_host_token rejects injection" {
  ! health_validate_host_token 'foo;rm -rf /'
  ! health_validate_host_token ''
}

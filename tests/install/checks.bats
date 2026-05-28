#!/usr/bin/env bats

setup() {
  INSTALL_SCRIPT_DIR="${BATS_TEST_DIRNAME}/../../scripts/install"
  # shellcheck source=../../scripts/install/lib/common.sh
  source "${INSTALL_SCRIPT_DIR}/lib/common.sh"
  # shellcheck source=../../scripts/install/lib/checks.sh
  source "${INSTALL_SCRIPT_DIR}/lib/checks.sh"
}

@test "install_validate_port accepts valid ports" {
  install_validate_port 8080
  install_validate_port 1
  install_validate_port 65535
}

@test "install_validate_port rejects invalid ports" {
  ! install_validate_port 0
  ! install_validate_port 70000
  ! install_validate_port abc
}

@test "install_profile_to_compose_profiles gpu-gateway is empty" {
  [ -z "$(install_profile_to_compose_profiles gpu-gateway)" ]
}

@test "install_profile_to_compose_profiles full-stack is full" {
  [ "$(install_profile_to_compose_profiles full-stack)" = "full" ]
}

@test "install_profile_to_compose_profiles rejects unknown" {
  ! install_profile_to_compose_profiles unknown
}

@test "install_validate_profile accepts known profiles" {
  install_validate_profile gpu-gateway
  install_validate_profile full-stack
  ! install_validate_profile invalid
}

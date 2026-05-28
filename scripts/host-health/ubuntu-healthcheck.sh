#!/usr/bin/env bash
# Ubuntu host health check — read-only system scan.
set -uo pipefail

HEALTH_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# shellcheck source=lib/common.sh
source "${HEALTH_SCRIPT_DIR}/lib/common.sh"
# shellcheck source=lib/checks-system.sh
source "${HEALTH_SCRIPT_DIR}/lib/checks-system.sh"
# shellcheck source=lib/checks-systemd.sh
source "${HEALTH_SCRIPT_DIR}/lib/checks-systemd.sh"
# shellcheck source=lib/checks-packages.sh
source "${HEALTH_SCRIPT_DIR}/lib/checks-packages.sh"
# shellcheck source=lib/checks-network.sh
source "${HEALTH_SCRIPT_DIR}/lib/checks-network.sh"
# shellcheck source=lib/checks-logs.sh
source "${HEALTH_SCRIPT_DIR}/lib/checks-logs.sh"

health_usage() {
  cat <<'EOF'
Usage: ubuntu-healthcheck.sh [OPTIONS]

Read-only Ubuntu server health scan. Exit codes:
  0 — no FAIL (WARN allowed unless --strict)
  1 — WARN present (--strict only)
  2 — at least one FAIL

Options:
  --quick           System, systemd, packages, network (default)
  --full            All suites including log/kernel checks
  --config PATH     Additional config (KEY=value) after defaults
  --skip-external   Skip external connectivity probe
  --json            Emit JSON summary on stdout after human output
  --no-color        Disable ANSI colors
  --strict          Exit 1 when any WARN
  --log-file PATH   Append output to log file
  --help            Show this help

Config: config/defaults.conf, /etc/host-healthcheck.conf, --config
EOF
}

health_parse_args() {
  # Assignments consumed in health_main / common.sh after parse (SC2034 false positive).
  # shellcheck disable=SC2034
  HEALTH_MODE="quick"
  HEALTH_EXTRA_CONFIG=""
  HEALTH_NO_COLOR=false

  while [[ $# -gt 0 ]]; do
    case "$1" in
      --quick) HEALTH_MODE="quick" ;;
      --full) HEALTH_MODE="full" ;;
      --config)
        shift
        HEALTH_EXTRA_CONFIG="${1:?--config requires PATH}"
        ;;
      --skip-external) HEALTH_SKIP_EXTERNAL=true ;;
      --json) HEALTH_JSON=true ;;
      --no-color) HEALTH_NO_COLOR=true ;;
      --strict) HEALTH_STRICT=true ;;
      --log-file)
        shift
        HEALTH_LOG_FILE="${1:?--log-file requires PATH}"
        ;;
      --help|-h)
        health_usage
        exit 0
        ;;
      *)
        echo "Unknown option: $1" >&2
        health_usage >&2
        exit 2
        ;;
    esac
    shift
  done
}

health_main() {
  health_parse_args "$@"
  health_init_color
  health_load_config
  health_init_report

  health_log "Ubuntu host health check (mode=${HEALTH_MODE})"
  health_log "Host: $(hostname -f 2>/dev/null || hostname) user=$(id -un) euid=${EUID}"

  health_run_system_checks
  health_run_systemd_checks
  health_run_packages_checks
  health_run_network_checks

  if [[ "${HEALTH_MODE}" == full ]]; then
    health_run_logs_checks
  fi

  health_print_summary
  health_final_exit_code
}

health_main "$@"

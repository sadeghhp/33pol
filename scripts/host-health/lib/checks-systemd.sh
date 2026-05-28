#!/usr/bin/env bash
# systemd health checks.

health_check_systemd_failed() {
  if ! health_require_command systemctl; then
    health_skip "systemd-failed-units" "systemctl not found"
    return
  fi
  local failed
  failed="$(systemctl --failed --no-legend 2>/dev/null | sed '/^[[:space:]]*$/d')"
  if [[ -z "${failed}" ]]; then
    health_pass "systemd-failed-units" "none"
  else
    local count
    count="$(printf '%s\n' "${failed}" | wc -l | tr -d ' ')"
    health_fail "systemd-failed-units" "${count} failed unit(s): $(printf '%s' "${failed}" | head -3 | tr '\n' '; ')"
  fi
}

health_check_systemd_state() {
  if ! health_require_command systemctl; then
    health_skip "systemd-system-state" "systemctl not found"
    return
  fi
  local state
  state="$(systemctl is-system-running 2>/dev/null || true)"
  state="${state:-unknown}"
  case "${state}" in
    running) health_pass "systemd-system-state" "${state}" ;;
    degraded) health_warn "systemd-system-state" "${state}" ;;
    maintenance|stopping) health_warn "systemd-system-state" "${state}" ;;
    initializing|starting) health_pass "systemd-system-state" "${state} (transient)" ;;
    *) health_warn "systemd-system-state" "${state}" ;;
  esac
}

health_check_systemd_timers() {
  if ! health_require_command systemctl; then
    health_skip "systemd-failed-timers" "systemctl not found"
    return
  fi
  local failed
  failed="$(systemctl list-timers --failed --no-legend 2>/dev/null | sed '/^[[:space:]]*$/d')"
  if [[ -z "${failed}" ]]; then
    health_pass "systemd-failed-timers" "none"
  else
    local count
    count="$(printf '%s\n' "${failed}" | wc -l | tr -d ' ')"
    health_warn "systemd-failed-timers" "${count} failed timer(s)"
  fi
}

health_run_systemd_checks() {
  health_log "== systemd =="
  health_check_systemd_failed
  health_check_systemd_state
  health_check_systemd_timers
}

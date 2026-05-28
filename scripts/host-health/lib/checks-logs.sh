#!/usr/bin/env bash
# Log and kernel error checks (--full mode).

health_check_journal_errors() {
  if ! health_require_command journalctl; then
    health_skip "journal-errors-24h" "journalctl not found"
    return
  fi
  local count
  count="$(journalctl -p err --since "24 hours ago" --no-pager -q 2>/dev/null | wc -l | tr -d ' ')"
  [[ "${count}" =~ ^[0-9]+$ ]] || count=0
  if (( count >= JOURNAL_ERR_WARN_COUNT )); then
    health_warn "journal-errors-24h" "${count} error-level journal entries (last 24h)"
  else
    health_pass "journal-errors-24h" "${count} error-level entries"
  fi
}

health_check_oom() {
  if ! health_require_command journalctl; then
    health_skip "oom-killer" "journalctl not found"
    return
  fi
  local hits
  hits="$(journalctl -k --since "7 days ago" --no-pager 2>/dev/null | grep -ci 'out of memory' || echo 0)"
  hits="$(echo "${hits}" | tr -d ' ')"
  if [[ "${hits}" =~ ^[0-9]+$ ]] && (( hits > 0 )); then
    health_fail "oom-killer" "${hits} OOM event(s) in kernel log (7d)"
  else
    health_pass "oom-killer" "no OOM events in 7d"
  fi
}

health_check_dmesg_errors() {
  if ! health_require_command dmesg; then
    health_skip "dmesg-errors" "dmesg not found"
    return
  fi
  if [[ "${EUID}" -ne 0 ]] && ! dmesg >/dev/null 2>&1; then
    health_skip "dmesg-errors" "requires root or readable kernel ring buffer"
    return
  fi
  local lines
  lines="$(dmesg -T -l err,crit,alert,emerg 2>/dev/null | tail -20 | sed '/^[[:space:]]*$/d')"
  if [[ -z "${lines}" ]]; then
    health_pass "dmesg-errors" "no err/crit/alert/emerg in kernel ring (last 20)"
  else
    local count
    count="$(printf '%s\n' "${lines}" | wc -l | tr -d ' ')"
    health_warn "dmesg-errors" "${count} kernel err/crit+ in ring buffer (may include older boots; not time-filtered)"
  fi
}

health_run_logs_checks() {
  health_log "== Logs =="
  health_check_journal_errors
  health_check_oom
  health_check_dmesg_errors
}

#!/usr/bin/env bash
# Package and update checks (read-only).

health_check_dpkg_audit() {
  if ! health_require_command dpkg; then
    health_skip "dpkg-audit" "dpkg not found"
    return
  fi
  local audit
  audit="$(dpkg --audit 2>/dev/null | sed '/^[[:space:]]*$/d')"
  if [[ -z "${audit}" ]]; then
    health_pass "dpkg-audit" "no broken packages"
  else
    health_fail "dpkg-audit" "$(printf '%s' "${audit}" | head -5 | tr '\n' '; ')"
  fi
}

health_check_apt_lock() {
  if [[ -f /var/lib/dpkg/lock-frontend ]] && command -v fuser >/dev/null 2>&1; then
    if fuser /var/lib/dpkg/lock-frontend >/dev/null 2>&1; then
      health_warn "apt-lock" "dpkg lock held (another package operation may be running)"
      return
    fi
  fi
  if [[ -f /var/lib/apt/lists/lock ]] && command -v fuser >/dev/null 2>&1; then
    if fuser /var/lib/apt/lists/lock >/dev/null 2>&1; then
      health_warn "apt-lock" "apt lists lock held"
      return
    fi
  fi
  health_pass "apt-lock" "not held"
}

health_check_reboot_required() {
  if [[ -f /var/run/reboot-required ]]; then
    local reason=""
    if [[ -f /var/run/reboot-required.pkgs ]]; then
      reason=" ($(head -3 /var/run/reboot-required.pkgs | tr '\n' ' '))"
    fi
    health_warn "reboot-required" "kernel or libc update needs reboot${reason}"
  else
    health_pass "reboot-required" "not required"
  fi
}

health_check_security_updates() {
  if [[ -x /usr/lib/update-notifier/apt-check ]]; then
    local result pending security
    result="$(/usr/lib/update-notifier/apt-check 2>/dev/null || echo "0;0")"
    pending="${result%%;*}"
    security="${result#*;}"
    pending="${pending//[!0-9]/}"
    security="${security//[!0-9]/}"
    pending="${pending:-0}"
    security="${security:-0}"
    if (( security > 0 )); then
      health_warn "security-updates" "${security} security package(s) pending (${pending} total upgradable)"
      return
    fi
    if (( pending > 0 )); then
      health_pass "security-updates" "${pending} non-security upgrade(s) available"
      return
    fi
    health_pass "security-updates" "no pending upgrades"
    return
  fi
  if ! health_require_command apt-get; then
    health_skip "security-updates" "apt-get and apt-check unavailable"
    return
  fi
  local sim
  sim="$(apt-get -s upgrade 2>/dev/null | grep -E '^Inst.*security' || true)"
  if [[ -n "${sim}" ]]; then
    health_warn "security-updates" "security upgrades available (simulated apt-get)"
  else
    health_pass "security-updates" "no security upgrades detected (simulated apt-get)"
  fi
}

health_check_ssh_failures() {
  if ! health_require_command journalctl; then
    health_skip "ssh-failed-logins" "journalctl not found"
    return
  fi
  local count
  count="$(journalctl -u ssh -u sshd --since "24 hours ago" -q \
    --grep='Failed password|Failed publickey' 2>/dev/null | wc -l | tr -d ' ')"
  count="$(echo "${count}" | tr -d ' ')"
  [[ "${count}" =~ ^[0-9]+$ ]] || count=0
  if (( count >= SSH_FAILED_WARN_COUNT )); then
    health_warn "ssh-failed-logins" "${count} failed attempts in 24h (threshold ${SSH_FAILED_WARN_COUNT})"
  else
    health_pass "ssh-failed-logins" "${count} failed attempts in 24h"
  fi
}

health_run_packages_checks() {
  health_log "== Packages =="
  health_check_dpkg_audit
  health_check_apt_lock
  health_check_reboot_required
  health_check_security_updates
  health_check_ssh_failures
}

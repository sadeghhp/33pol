#!/usr/bin/env bash
# System resource checks (OS, load, memory, disk, processes).

health_check_os() {
  if health_is_ubuntu; then
  # shellcheck source=/dev/null
    source /etc/os-release
    health_pass "os-release" "${PRETTY_NAME:-Ubuntu}"
  else
    health_warn "os-release" "Not Ubuntu (ID=$(. /etc/os-release 2>/dev/null; echo "${ID:-unknown}"))"
  fi
}

health_check_uptime() {
  if [[ -r /proc/uptime ]]; then
    local up_secs up_human
    up_secs="$(awk '{print int($1)}' /proc/uptime)"
    up_human="$(uptime -p 2>/dev/null || echo "${up_secs}s")"
    health_pass "uptime" "${up_human} (boot: $(uptime -s 2>/dev/null || echo unknown))"
  else
    health_skip "uptime" "/proc/uptime not readable"
  fi
}

health_check_load() {
  if [[ ! -r /proc/loadavg ]]; then
    health_skip "load-average" "/proc/loadavg not readable"
    return
  fi
  local load1 nproc status
  load1="$(awk '{print $1}' /proc/loadavg)"
  nproc="$(nproc 2>/dev/null || echo 1)"
  status="$(health_load_status "${load1}" "${nproc}" "${LOAD_WARN_MULTIPLIER}" "${LOAD_FAIL_MULTIPLIER}")"
  local msg="load1=${load1} cpus=${nproc} (warn>=${LOAD_WARN_MULTIPLIER}x fail>=${LOAD_FAIL_MULTIPLIER}x)"
  case "${status}" in
    pass) health_pass "load-average" "${msg}" ;;
    warn) health_warn "load-average" "${msg}" ;;
    fail) health_fail "load-average" "${msg}" ;;
  esac
}

health_check_memory() {
  local avail_kb total_kb status
  avail_kb="$(health_read_meminfo_kb MemAvailable)"
  total_kb="$(health_read_meminfo_kb MemTotal)"
  if [[ -z "${avail_kb}" || -z "${total_kb}" ]]; then
    health_skip "memory-available" "cannot read /proc/meminfo"
    return
  fi
  status="$(health_mem_avail_status "${avail_kb}" "${total_kb}" "${MEM_AVAIL_WARN_PCT}")"
  local avail_mb=$((avail_kb / 1024))
  local total_mb=$((total_kb / 1024))
  local pct=$((avail_kb * 100 / total_kb))
  local msg="MemAvailable ${avail_mb}MB / ${total_mb}MB (${pct}%, warn if <${MEM_AVAIL_WARN_PCT}%)"
  case "${status}" in
    pass) health_pass "memory-available" "${msg}" ;;
    warn) health_warn "memory-available" "${msg}" ;;
  esac
}

health_check_swap() {
  local swap_total swap_free avail_kb total_kb used_pct=0 avail_pct=100
  swap_total="$(health_read_meminfo_kb SwapTotal)"
  swap_free="$(health_read_meminfo_kb SwapFree)"
  avail_kb="$(health_read_meminfo_kb MemAvailable)"
  total_kb="$(health_read_meminfo_kb MemTotal)"
  if [[ -z "${swap_total}" || "${swap_total}" -eq 0 ]]; then
    health_pass "swap-usage" "no swap configured"
    return
  fi
  if (( swap_total > 0 )); then
    used_pct=$(( (swap_total - swap_free) * 100 / swap_total ))
  fi
  if [[ -n "${avail_kb}" && -n "${total_kb}" ]] && (( total_kb > 0 )); then
    avail_pct=$((avail_kb * 100 / total_kb))
  fi
  if (( used_pct >= SWAP_USED_WARN_PCT )) && (( avail_pct < MEM_AVAIL_WARN_PCT * 2 )); then
    health_warn "swap-usage" "swap ${used_pct}% used and memory pressure (MemAvailable ${avail_pct}%)"
  else
    health_pass "swap-usage" "swap ${used_pct}% used"
  fi
}

health_check_disk_space() {
  if ! health_require_command df; then
    health_skip "disk-space" "df not found"
    return
  fi
  local line mount pct status fstype
  local any_mount=false
  while IFS= read -r line; do
    fstype="$(awk '{print $2}' <<<"${line}")"
    if health_df_skip_fstype "${fstype}"; then
      continue
    fi
    any_mount=true
    mount="$(awk '{print $NF}' <<<"${line}")"
    pct="$(awk '{gsub(/%/,"",$6); print $6}' <<<"${line}")"
    [[ "${pct}" =~ ^[0-9]+$ ]] || continue
    status="$(health_disk_status "${pct}" "${DISK_WARN_PCT}" "${DISK_FAIL_PCT}")"
    case "${status}" in
      fail) health_fail "disk-space:${mount}" "${pct}% used (${fstype})" ;;
      warn) health_warn "disk-space:${mount}" "${pct}% used (${fstype})" ;;
      *) health_pass "disk-space:${mount}" "${pct}% used" ;;
    esac
  done < <(df -PT -x tmpfs -x devtmpfs -x squashfs 2>/dev/null | awk 'NR>1 {print}')
  if [[ "${any_mount}" != true ]]; then
    health_skip "disk-space" "no mounts parsed from df"
  fi
}

health_check_inodes() {
  if ! health_require_command df; then
    health_skip "disk-inodes" "df not found"
    return
  fi
  local line mount pct status fstype
  while IFS= read -r line; do
    fstype="$(awk '{print $2}' <<<"${line}")"
    if health_df_skip_fstype "${fstype}"; then
      continue
    fi
    mount="$(awk '{print $NF}' <<<"${line}")"
    pct="$(awk '{gsub(/%/,"",$6); print $6}' <<<"${line}")"
    [[ "${pct}" =~ ^[0-9]+$ ]] || continue
    status="$(health_disk_status "${pct}" "${INODE_WARN_PCT}" "${INODE_FAIL_PCT}")"
    case "${status}" in
      fail) health_fail "disk-inodes:${mount}" "${pct}% inodes used" ;;
      warn) health_warn "disk-inodes:${mount}" "${pct}% inodes used" ;;
      *) health_pass "disk-inodes:${mount}" "${pct}% inodes used" ;;
    esac
  done < <(df -PTi -x tmpfs -x devtmpfs 2>/dev/null | awk 'NR>1 {print}')
}

health_check_zombies() {
  local count
  if ! health_require_command ps; then
    health_skip "zombie-processes" "ps not found"
    return
  fi
  count="$(ps axo stat= 2>/dev/null | awk '$1 ~ /Z/ { c++ } END { print c+0 }')"
  if (( count > 0 )); then
    health_warn "zombie-processes" "${count} zombie(s) detected"
  else
    health_pass "zombie-processes" "none"
  fi
}

health_run_system_checks() {
  health_log "== System =="
  health_check_os
  health_check_uptime
  health_check_load
  health_check_memory
  health_check_swap
  health_check_disk_space
  health_check_inodes
  health_check_zombies
}

#!/usr/bin/env bash
# Shared framework for Ubuntu host health checks (read-only).

HEALTH_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Counters
HEALTH_PASS_COUNT=0
HEALTH_WARN_COUNT=0
HEALTH_FAIL_COUNT=0
HEALTH_SKIP_COUNT=0

# Runtime options (set by entry script)
HEALTH_MODE="quick"
HEALTH_SKIP_EXTERNAL=false
HEALTH_STRICT=false
HEALTH_JSON=false
HEALTH_USE_COLOR=false
HEALTH_LOG_FILE=""
HEALTH_JSON_RESULTS=()

# Config thresholds (defaults; overridden by config files)
DISK_WARN_PCT=85
DISK_FAIL_PCT=95
INODE_WARN_PCT=85
INODE_FAIL_PCT=95
MEM_AVAIL_WARN_PCT=10
LOAD_WARN_MULTIPLIER=2
LOAD_FAIL_MULTIPLIER=4
SSH_FAILED_WARN_COUNT=50
SWAP_USED_WARN_PCT=80
JOURNAL_ERR_WARN_COUNT=1
DNS_TEST_HOST=example.com
EXTERNAL_PING_HOST=1.1.1.1

# Only these keys may be set from config files (prevents arbitrary variable overwrite).
HEALTH_CONFIG_KEYS=(
  DISK_WARN_PCT DISK_FAIL_PCT INODE_WARN_PCT INODE_FAIL_PCT
  MEM_AVAIL_WARN_PCT LOAD_WARN_MULTIPLIER LOAD_FAIL_MULTIPLIER
  SSH_FAILED_WARN_COUNT SWAP_USED_WARN_PCT JOURNAL_ERR_WARN_COUNT
  DNS_TEST_HOST EXTERNAL_PING_HOST
)

health_init_color() {
  if [[ "${HEALTH_NO_COLOR:-false}" == true ]] || [[ -n "${NO_COLOR:-}" ]]; then
    HEALTH_USE_COLOR=false
    return
  fi
  if [[ -t 1 ]]; then
    HEALTH_USE_COLOR=true
  else
    HEALTH_USE_COLOR=false
  fi
}

health_color() {
  local role="$1"
  if [[ "${HEALTH_USE_COLOR}" != true ]]; then
    return 0
  fi
  case "${role}" in
    pass) printf '\033[0;32m' ;;
    warn) printf '\033[0;33m' ;;
    fail) printf '\033[0;31m' ;;
    skip) printf '\033[0;36m' ;;
    reset) printf '\033[0m' ;;
  esac
}

health_log() {
  local line="[$(date '+%H:%M:%S')] $*"
  printf '%s\n' "${line}"
  if [[ -n "${HEALTH_LOG_FILE}" ]]; then
    printf '%s\n' "${line}" >>"${HEALTH_LOG_FILE}" 2>/dev/null || true
  fi
}

health_config_key_allowed() {
  local key="$1" k
  for k in "${HEALTH_CONFIG_KEYS[@]}"; do
    [[ "${k}" == "${key}" ]] && return 0
  done
  return 1
}

health_validate_config() {
  [[ "${DISK_WARN_PCT}" =~ ^[0-9]+$ ]] || DISK_WARN_PCT=85
  [[ "${DISK_FAIL_PCT}" =~ ^[0-9]+$ ]] || DISK_FAIL_PCT=95
  [[ "${INODE_WARN_PCT}" =~ ^[0-9]+$ ]] || INODE_WARN_PCT=85
  [[ "${INODE_FAIL_PCT}" =~ ^[0-9]+$ ]] || INODE_FAIL_PCT=95
  [[ "${MEM_AVAIL_WARN_PCT}" =~ ^[0-9]+$ ]] || MEM_AVAIL_WARN_PCT=10
  [[ "${LOAD_WARN_MULTIPLIER}" =~ ^[0-9]+$ ]] || LOAD_WARN_MULTIPLIER=2
  [[ "${LOAD_FAIL_MULTIPLIER}" =~ ^[0-9]+$ ]] || LOAD_FAIL_MULTIPLIER=4
  [[ "${SSH_FAILED_WARN_COUNT}" =~ ^[0-9]+$ ]] || SSH_FAILED_WARN_COUNT=50
  [[ "${SWAP_USED_WARN_PCT}" =~ ^[0-9]+$ ]] || SWAP_USED_WARN_PCT=80
  [[ "${JOURNAL_ERR_WARN_COUNT}" =~ ^[0-9]+$ ]] || JOURNAL_ERR_WARN_COUNT=1
  if ! health_validate_host_token "${DNS_TEST_HOST}"; then
    health_log "WARNING: invalid DNS_TEST_HOST; using example.com"
    DNS_TEST_HOST=example.com
  fi
  if ! health_validate_host_token "${EXTERNAL_PING_HOST}"; then
    health_log "WARNING: invalid EXTERNAL_PING_HOST; using 1.1.1.1"
    EXTERNAL_PING_HOST=1.1.1.1
  fi
}

# Hostname or IPv4 literal for DNS/ping targets (no shell metacharacters).
health_validate_host_token() {
  local token="$1"
  [[ -n "${token}" && "${#token}" -le 253 ]] || return 1
  [[ "${token}" != *[\;\|\&\`\$\(\)\\]* ]] || return 1
  if [[ "${token}" =~ ^[0-9]+(\.[0-9]+){3}$ ]]; then
    return 0
  fi
  [[ "${token}" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]]
}

health_load_config_file() {
  local file="$1"
  [[ -f "${file}" ]] || return 0
  local line key value
  while IFS= read -r line || [[ -n "${line}" ]]; do
    line="${line%%#*}"
    line="${line#"${line%%[![:space:]]*}"}"
    line="${line%"${line##*[![:space:]]}"}"
    [[ -z "${line}" ]] && continue
    [[ "${line}" == *"="* ]] || continue
    if [[ ! "${line}" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; then
      health_log "WARNING: ignoring invalid config line in ${file}: ${line}"
      continue
    fi
    key="${line%%=*}"
    value="${line#*=}"
    if ! health_config_key_allowed "${key}"; then
      health_log "WARNING: ignoring disallowed config key in ${file}: ${key}"
      continue
    fi
    # shellcheck disable=SC2163
    printf -v "${key}" '%s' "${value}"
  done <"${file}"
}

health_load_config() {
  health_load_config_file "${HEALTH_SCRIPT_DIR}/config/defaults.conf"
  health_load_config_file "/etc/host-healthcheck.conf"
  if [[ -n "${HEALTH_EXTRA_CONFIG:-}" ]]; then
    health_load_config_file "${HEALTH_EXTRA_CONFIG}"
  fi
  health_validate_config
}

health_init_report() {
  HEALTH_PASS_COUNT=0
  HEALTH_WARN_COUNT=0
  HEALTH_FAIL_COUNT=0
  HEALTH_SKIP_COUNT=0
  HEALTH_JSON_RESULTS=()
}

# Map numeric percent to pass|warn|fail (stdout).
health_disk_status() {
  local pct="$1" warn="$2" fail="$3"
  if (( pct >= fail )); then
    echo fail
  elif (( pct >= warn )); then
    echo warn
  else
    echo pass
  fi
}

# Map load1/nproc ratio to pass|warn|fail.
health_load_status() {
  local load1="$1" nproc="$2" warn_mult="$3" fail_mult="$4"
  if (( nproc < 1 )); then
    nproc=1
  fi
  local load_x100
  load_x100="$(awk -v l="${load1}" -v n="${nproc}" 'BEGIN { printf "%.0f", (l / n) * 100 }')"
  local warn_thresh fail_thresh
  warn_thresh=$((warn_mult * 100))
  fail_thresh=$((fail_mult * 100))
  if (( load_x100 >= fail_thresh )); then
    echo fail
  elif (( load_x100 >= warn_thresh )); then
    echo warn
  else
    echo pass
  fi
}

# Map MemAvailable % to pass|warn.
health_mem_avail_status() {
  local avail_kb="$1" total_kb="$2" warn_pct="$3"
  if (( total_kb <= 0 )); then
    echo pass
    return
  fi
  local pct=$((avail_kb * 100 / total_kb))
  if (( pct < warn_pct )); then
    echo warn
  else
    echo pass
  fi
}

health_json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g; s/	/\\t/g; s/\r//g' | tr '\n' ' '
}

health_record_json() {
  local name="$1" status="$2" detail="$3"
  if [[ "${HEALTH_JSON}" != true ]]; then
    return 0
  fi
  local escaped_name escaped_detail
  escaped_name="$(health_json_escape "${name}")"
  escaped_detail="$(health_json_escape "${detail}")"
  HEALTH_JSON_RESULTS+=("{\"name\":\"${escaped_name}\",\"status\":\"${status}\",\"detail\":\"${escaped_detail}\"}")
}

health_emit_result() {
  local level="$1"
  local name="$2"
  local msg="$3"
  local tag
  case "${level}" in
    pass) tag="PASS"; HEALTH_PASS_COUNT=$((HEALTH_PASS_COUNT + 1)) ;;
    warn) tag="WARN"; HEALTH_WARN_COUNT=$((HEALTH_WARN_COUNT + 1)) ;;
    fail) tag="FAIL"; HEALTH_FAIL_COUNT=$((HEALTH_FAIL_COUNT + 1)) ;;
    skip) tag="SKIP"; HEALTH_SKIP_COUNT=$((HEALTH_SKIP_COUNT + 1)) ;;
    *) tag="INFO"; HEALTH_PASS_COUNT=$((HEALTH_PASS_COUNT + 1)) ;;
  esac
  health_color "${level}"
  printf '[%s] %s' "${tag}" "${name}"
  health_color reset
  if [[ -n "${msg}" ]]; then
    printf ': %s' "${msg}"
  fi
  printf '\n'
  health_record_json "${name}" "${tag}" "${msg}"
  if [[ -n "${HEALTH_LOG_FILE}" ]]; then
    printf '[%s] %s: %s\n' "${tag}" "${name}" "${msg}" >>"${HEALTH_LOG_FILE}" 2>/dev/null || true
  fi
}

health_pass() { health_emit_result pass "$1" "${2:-}"; }
health_warn() { health_emit_result warn "$1" "${2:-}"; }
health_fail() { health_emit_result fail "$1" "${2:-}"; }
health_skip() { health_emit_result skip "$1" "${2:-}"; }

# run_check NAME SEVERITY COMMAND...
# SEVERITY: fail|warn — on command failure (non-zero exit), emit FAIL or WARN.
health_run_check() {
  local name="$1"
  local severity="$2"
  shift 2
  local detail="" rc
  set +e
  detail="$("$@" 2>&1)"
  rc=$?
  set +e
  if (( rc == 0 )); then
    if [[ -n "${detail}" ]]; then
      health_pass "${name}" "${detail}"
    else
      health_pass "${name}"
    fi
    return 0
  fi
  if [[ "${severity}" == fail ]]; then
    health_fail "${name}" "${detail:-check returned ${rc}}"
  else
    health_warn "${name}" "${detail:-check returned ${rc}}"
  fi
  return "${rc}"
}

health_is_ubuntu() {
  [[ -f /etc/os-release ]] || return 1
  # shellcheck source=/dev/null
  source /etc/os-release
  [[ "${ID:-}" == "ubuntu" ]]
}

health_print_summary() {
  health_log "---"
  health_log "Summary: ${HEALTH_PASS_COUNT} pass, ${HEALTH_WARN_COUNT} warn, ${HEALTH_FAIL_COUNT} fail, ${HEALTH_SKIP_COUNT} skip"
  if [[ "${HEALTH_JSON}" == true ]]; then
    local items
    items="$(IFS=,; echo "${HEALTH_JSON_RESULTS[*]}")"
    printf '{"summary":{"pass":%d,"warn":%d,"fail":%d,"skip":%d},"checks":[%s]}\n' \
      "${HEALTH_PASS_COUNT}" "${HEALTH_WARN_COUNT}" "${HEALTH_FAIL_COUNT}" "${HEALTH_SKIP_COUNT}" \
      "${items}"
  fi
}

health_final_exit_code() {
  if (( HEALTH_FAIL_COUNT > 0 )); then
    return 2
  fi
  if [[ "${HEALTH_STRICT}" == true ]] && (( HEALTH_WARN_COUNT > 0 )); then
    return 1
  fi
  return 0
}

health_require_command() {
  local cmd="$1"
  command -v "${cmd}" >/dev/null 2>&1
}

health_read_meminfo_kb() {
  local key="$1"
  awk -v k="${key}" '$1 == k ":" { print $2; exit }' /proc/meminfo 2>/dev/null
}

health_df_skip_fstype() {
  local fstype="$1"
  case "${fstype}" in
    tmpfs|devtmpfs|squashfs|overlay|proc|sysfs|devpts|cgroup2|cgroup|securityfs|pstore|bpf|tracefs|debugfs|fusectl|mqueue|hugetlbfs|configfs|fuse.lxcfs|efivarfs|autofs|binfmt_misc|rpc_pipefs|nsfs|fuse.portal|none) return 0 ;;
  esac
  return 1
}


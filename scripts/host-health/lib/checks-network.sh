#!/usr/bin/env bash
# Network connectivity checks (read-only probes).

health_check_dns() {
  local host="${DNS_TEST_HOST:-example.com}"
  if ! health_validate_host_token "${host}"; then
    health_skip "dns-resolution" "invalid DNS_TEST_HOST"
    return
  fi
  if health_require_command getent; then
    if getent hosts "${host}" >/dev/null 2>&1; then
      health_pass "dns-resolution" "${host} resolves"
      return
    fi
    health_fail "dns-resolution" "getent hosts ${host} failed"
    return
  fi
  if health_require_command host; then
    if host "${host}" >/dev/null 2>&1; then
      health_pass "dns-resolution" "${host} resolves"
      return
    fi
    health_fail "dns-resolution" "host ${host} failed"
    return
  fi
  health_skip "dns-resolution" "getent/host not available"
}

health_check_default_route() {
  if ! health_require_command ip; then
    health_skip "default-route" "ip not found"
    return
  fi
  local route
  route="$(ip route show default 2>/dev/null | head -1)"
  if [[ -n "${route}" ]]; then
    health_pass "default-route" "${route}"
  else
    health_fail "default-route" "no default route"
  fi
}

health_check_gateway_ping() {
  if ! health_require_command ip || ! health_require_command ping; then
    health_skip "gateway-reachable" "ip or ping not found"
    return
  fi
  local gw
  gw="$(ip route show default 2>/dev/null | awk '{print $3; exit}')"
  if [[ -z "${gw}" ]]; then
    health_skip "gateway-reachable" "no gateway from route table"
    return
  fi
  if ping -c1 -W2 "${gw}" >/dev/null 2>&1; then
    health_pass "gateway-reachable" "ping ${gw} ok"
  else
    health_warn "gateway-reachable" "ping ${gw} failed"
  fi
}

health_check_external() {
  if [[ "${HEALTH_SKIP_EXTERNAL}" == true ]]; then
    health_skip "external-connectivity" "--skip-external"
    return
  fi
  local host="${EXTERNAL_PING_HOST:-1.1.1.1}"
  if ! health_validate_host_token "${host}"; then
    health_skip "external-connectivity" "invalid EXTERNAL_PING_HOST"
    return
  fi
  if health_require_command ping; then
    if ping -c1 -W3 "${host}" >/dev/null 2>&1; then
      health_pass "external-connectivity" "ping ${host} ok"
      return
    fi
  fi
  if health_require_command curl && [[ "${host}" =~ ^[0-9]+(\.[0-9]+){3}$ ]]; then
    if curl -sf --max-time 3 "https://${host}" >/dev/null 2>&1; then
      health_pass "external-connectivity" "https://${host} reachable"
      return
    fi
  fi
  health_warn "external-connectivity" "cannot reach ${host} (ping preferred)"
}

health_check_listening_ports() {
  if ! health_require_command ss; then
    health_skip "listening-ports" "ss not found"
    return
  fi
  local count
  count="$(ss -tlnH 2>/dev/null | wc -l | tr -d ' ')"
  health_pass "listening-ports" "${count} TCP ports listening"
}

health_run_network_checks() {
  health_log "== Network =="
  health_check_dns
  health_check_default_route
  health_check_gateway_ping
  health_check_external
  health_check_listening_ports
}

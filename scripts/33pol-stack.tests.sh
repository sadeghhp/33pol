#!/usr/bin/env bash
# Behavior checks for 33pol-stack.sh that do not start or stop containers.
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=../33pol-stack.sh
source "${ROOT}/33pol-stack.sh"

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

profile_has "observability" observability || fail "observability profile was not recognized"
profile_has "mock,observability" observability || fail "comma-separated observability was not recognized"
profile_has "mock, full" full || fail "spaced full profile was not recognized"
if profile_has "fullstack" full; then
  fail "fullstack was treated as the full profile"
fi
if profile_has "observability-extra" observability; then
  fail "observability-extra was treated as the observability profile"
fi
if profile_has "" observability; then
  fail "an empty profile list matched observability"
fi

COMPOSE_PROFILES=mock
services_for all
narrow_all_to_enabled >/dev/null 2>&1
[[ "${SELECTED[*]}" == "gateway" ]] || fail "all with mock profile selected: ${SELECTED[*]}"

COMPOSE_PROFILES=observability
services_for all
narrow_all_to_enabled
[[ "${SELECTED[*]}" == "gateway prometheus grafana" ]] || fail "all with observability selected: ${SELECTED[*]}"

COMPOSE_PROFILES=mock
services_for prometheus
narrow_all_to_enabled
[[ "${SELECTED[*]}" == "prometheus" ]] || fail "explicit prometheus was changed to: ${SELECTED[*]}"

services_for 33pol
[[ "${SELECTED[*]}" == "gateway" ]] || fail "33pol did not map to gateway"

export COMPOSE_PROFILES=""
[[ -z "$(env_or_file COMPOSE_PROFILES)" ]] || fail "an empty shell profile overrode .env incorrectly"
unset COMPOSE_PROFILES
[[ "$(env_or_file COMPOSE_PROFILES)" == "observability" ]] || fail "an unset profile did not come from .env: $(env_or_file COMPOSE_PROFILES)"

export GATEWAY_METRICS_SCRAPE_TOKEN="   "
if token_is_set; then
  fail "a whitespace scrape token was accepted"
fi
unset GATEWAY_METRICS_SCRAPE_TOKEN

GATEWAY_PORT=0
if port_value GATEWAY_PORT 8080 >/dev/null 2>&1; then
  fail "port 0 was accepted"
fi
GATEWAY_PORT=65536
if port_value GATEWAY_PORT 8080 >/dev/null 2>&1; then
  fail "port 65536 was accepted"
fi
GATEWAY_PORT=11444
[[ "$(port_value GATEWAY_PORT 8080)" == "11444" ]] || fail "port 11444 was rejected"
unset GATEWAY_PORT

[[ "$(classify_gateway_wait true true)" == "healthy" ]] || fail "healthy gateway wait was misclassified"
[[ "$(classify_gateway_wait true false)" == "started-not-ready" ]] || fail "not-ready gateway wait was misclassified"
if classify_gateway_wait false false >/dev/null; then
  fail "a down gateway was classified as started"
fi

saved_dry="${DRY_RUN}"
saved_yes="${ASSUME_YES}"
DRY_RUN=true
ASSUME_YES=true
COMPOSE_PROFILES=observability
services_for prometheus
if cmd_start >/dev/null 2>/tmp/33pol-stack-start-obs.err; then
  fail "start prometheus was allowed without a scrape token"
fi
if grep -q "docker start" /tmp/33pol-stack-start-obs.err; then
  fail "start prometheus fell back to docker start"
fi
services_for grafana
if cmd_restart >/dev/null 2>/tmp/33pol-stack-restart-obs.err; then
  fail "restart grafana was allowed without a scrape token"
fi
if grep -q "docker restart" /tmp/33pol-stack-restart-obs.err; then
  fail "restart grafana fell back to docker restart"
fi
services_for 33pol
if ! cmd_start >/dev/null 2>/tmp/33pol-stack-start-gw.err; then
  fail "dry-run start 33pol failed without a scrape token"
fi
grep -q "docker start" /tmp/33pol-stack-start-gw.err || fail "dry-run start 33pol did not show docker start"
services_for 33pol
if ! cmd_status >/dev/null 2>/tmp/33pol-stack-status.err; then
  fail "status fallback returned failure after showing containers"
fi
DRY_RUN="${saved_dry}"
ASSUME_YES="${saved_yes}"
unset COMPOSE_PROFILES

printf 'ok\n'

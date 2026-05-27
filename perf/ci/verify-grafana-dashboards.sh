#!/usr/bin/env bash
# Assert Compose Grafana file-provisioned dashboards and Prometheus datasource exist.
set -euo pipefail

GRAFANA_PORT="${GRAFANA_PORT:-3000}"
GRAFANA_USER="${GRAFANA_ADMIN_USER:-admin}"
GRAFANA_PASS="${GRAFANA_ADMIN_PASSWORD:-admin}"
GRAFANA_BASE="http://127.0.0.1:${GRAFANA_PORT}"

search="$(curl -sf -u "${GRAFANA_USER}:${GRAFANA_PASS}" \
  "${GRAFANA_BASE}/api/search?type=dash-db")"

python3 -c "
import json, sys
data = json.load(sys.stdin)
uids = {d.get('uid') for d in data if d.get('uid')}
required = {'33pol-gateway', '33pol-gateway-traffic'}
missing = required - uids
if missing:
    raise SystemExit(
        'missing Grafana dashboards: '
        + ', '.join(sorted(missing))
        + f' (found uids: {sorted(uids)})'
    )
print('grafana dashboards OK:', ', '.join(sorted(required)))
" <<<"${search}"

datasources="$(curl -sf -u "${GRAFANA_USER}:${GRAFANA_PASS}" \
  "${GRAFANA_BASE}/api/datasources")"

python3 -c "
import json, sys
data = json.load(sys.stdin)
by_uid = {d.get('uid'): d for d in data}
prom = by_uid.get('prometheus')
if not prom or prom.get('type') != 'prometheus':
    raise SystemExit('missing Grafana Prometheus datasource (uid=prometheus)')
print('grafana datasource OK: prometheus')
" <<<"${datasources}"

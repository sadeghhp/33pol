#!/usr/bin/env bash
# Local metrics-path check (Prometheus + Grafana). Trace export is not verified here. See perf/README.md.
set -euo pipefail

GATEWAY_PORT="${GATEWAY_PORT:-8080}"
PROM_PORT="${PROMETHEUS_PORT:-9090}"
GRAFANA_PORT="${GRAFANA_PORT:-3000}"

metrics="$(curl -sf "http://127.0.0.1:${GATEWAY_PORT}/metrics")"
grep -q 'gateway_inference_requests_total' <<<"${metrics}"
echo "gateway /metrics exposes gateway_inference_requests_total"

curl -sf "http://127.0.0.1:${PROM_PORT}/-/healthy" >/dev/null
echo "prometheus healthy"

targets="$(curl -sf "http://127.0.0.1:${PROM_PORT}/api/v1/targets")"
python3 -c "import json,sys; d=json.load(sys.stdin); ups=[t for t in d.get('data',{}).get('activeTargets',[]) if t.get('health')=='up']; assert ups, 'no up targets'; print('prometheus targets up:', len(ups))" <<<"${targets}"

curl -sf "http://127.0.0.1:${GRAFANA_PORT}/api/health" >/dev/null
echo "grafana healthy"

bash "$(dirname "$0")/verify-grafana-dashboards.sh"

echo "Observability local (metrics) verification passed."

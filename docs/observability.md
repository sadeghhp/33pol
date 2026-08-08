# 33pol Gateway Observability

## Metrics

Prometheus scrape endpoint: `GET /metrics` (OpenTelemetry Prometheus exporter).

Canonical metric names are defined in [12-metrics-and-runtime-contracts.md](implementation-plan/12-metrics-and-runtime-contracts.md).

## Dashboards

Docker Compose auto-provisions dashboards under the Grafana folder **33pol**:

| Dashboard | URL | Source |
|-----------|-----|--------|
| **33pol Gateway** (SRE / RED) | http://localhost:3000/d/33pol-gateway/33pol-gateway | [33pol-gateway.json](../deploy/grafana/dashboards/33pol-gateway.json) |
| **33pol Gateway — Traffic & tokens** | http://localhost:3000/d/33pol-gateway-traffic/33pol-gateway-traffic | [33pol-gateway-traffic.json](../deploy/grafana/dashboards/33pol-gateway-traffic.json) |

**Ops dashboard:** overview (RPS, error rate, p99, streams, healthy backends), RED, streaming/policy, FinOps/usage writer, backend health.

**Traffic dashboard:** inference route rate by `route`/`stream`, forward outcomes by `outcome`, token rates (`direction=input|output`).

Use the **Model** variable to filter. Token metrics are recorded when upstream `usage` is parsed on the inference path (`IUsageRecorder`). Plan: [13-grafana-business-metrics.md](implementation-plan/13-grafana-business-metrics.md).

After changing the JSON or datasource provisioning, restart Grafana: `docker compose restart grafana`.

## Alerts

`GatewayBillingReconciliationDrift` fires when the billing rollups stop matching the ledger behind them; `GatewayBillingReconciliationStalled` fires when the sweep that checks this stops running. Both are documented in [finops.md](finops.md#reconciliation) — the drift alert is the only signal that billing numbers have gone wrong, because every other symptom of it looks like normal operation.

Validate rules:

```bash
promtool check rules deploy/prometheus/alerts/33pol.yml
```

## Traces

Sample OpenTelemetry Collector config: [deploy/otel-collector/config.yaml](../deploy/otel-collector/config.yaml).

## Admin APIs

| Endpoint | Purpose |
|----------|---------|
| `GET /admin/api/summary` | Operational snapshot |
| `GET /admin/api/backends` | Registry + health |
| `GET /admin/api/requests?limit=` | Recent requests ring buffer |

All require admin API key scope.

## Correlation

- `X-Request-Id` on every response (Phase 3)
- Structured Serilog request logging
- OTel traces when collector is configured

## Runbooks

| Scenario | Document |
|----------|----------|
| High error rate | [runbooks/high-error-rate.md](./runbooks/high-error-rate.md) |
| All backends down | [runbooks/all-backends-down.md](./runbooks/all-backends-down.md) |
| Usage writer backlog / drops | [runbooks/writer-backlog.md](./runbooks/writer-backlog.md) |

Prometheus alert annotations reference these paths under `deploy/prometheus/alerts/`.

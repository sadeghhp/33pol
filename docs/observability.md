# 33pol Gateway Observability

## Metrics

Prometheus scrape endpoint: `GET /metrics` (OpenTelemetry Prometheus exporter).

Canonical metric names are defined in [12-metrics-and-runtime-contracts.md](implementation-plan/12-metrics-and-runtime-contracts.md).

## Dashboards

Docker Compose auto-provisions **33pol Gateway** under the Grafana folder **33pol**:

- URL: http://localhost:3000/d/33pol-gateway/33pol-gateway
- Source: [deploy/grafana/dashboards/33pol-gateway.json](../deploy/grafana/dashboards/33pol-gateway.json)

Rows: overview (RPS, error rate, p99, streams, healthy backends), RED, streaming/policy, FinOps/usage writer, backend health. Use the **Model** variable to filter.

After changing the JSON or datasource provisioning, restart Grafana: `docker compose restart grafana`.

## Alerts

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

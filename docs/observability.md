# 33pol Gateway Observability

## Metrics

Prometheus scrape endpoint: `GET /metrics` (OpenTelemetry Prometheus exporter).

Canonical metric names are defined in [12-metrics-and-runtime-contracts.md](implementation-plan/12-metrics-and-runtime-contracts.md).

## Dashboards

Import [deploy/grafana/dashboards/33pol-gateway.json](../deploy/grafana/dashboards/33pol-gateway.json).

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

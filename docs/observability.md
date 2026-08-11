# 33pol Gateway Observability

## Metrics

Prometheus scrape endpoint: `GET /metrics` (OpenTelemetry Prometheus exporter). Meter name: `Pol33.Gateway`.

Canonical definitions live in `GatewayMeters` (`33pol.Observability`) — that file is the source of truth; this table mirrors it.

**Label rules:** never label a metric with a raw API key or a full request id. `model` is the canonical model id; prefer tenant *slug* over uuid where a tenant dimension is added.

### Inference (RED)

| Metric | Type | Labels |
|--------|------|--------|
| `gateway_inference_requests_total` | Counter | `model`, `status` (`success`/`error`) |
| `gateway_inference_errors_total` | Counter | `model`, `code` (error catalog code, or `unknown`) |
| `gateway_inference_duration_seconds` | Histogram | `model` |
| `gateway_time_to_first_token_seconds` | Histogram | `model` |
| `gateway_active_streams` | UpDownCounter | `model` |
| `gateway_active_requests` | UpDownCounter | `model` |
| `gateway_inference_route_total` | Counter | `model`, `route` (`chat`/`completions`/`embeddings`/`rerank`/`unknown`), `stream` |
| `gateway_model_resolve_total` | Counter | `result` (`resolved`/`alias`/`not_found`) |
| `gateway_forward_attempts_total` | Counter | `model`, `outcome` |

`outcome` distinguishes why a forward ended: `success`, `upstream_error`, `upstream_timeout`, `stream_idle_timeout`, `client_canceled`, `backend_unhealthy`, `circuit_open`, `bulkhead_full`, `budget_exceeded`. Only `upstream_error` and `upstream_timeout` count as backend ill-health against the circuit breaker — see `ModelRouterMiddleware`.

`upstream_timeout` means response headers never arrived, so the backend never answered at all. Its allowance is `Gateway:Resilience:ForwardTimeoutSeconds` widened by `ForwardTimeoutSecondsPerRequestMegabyte` for every megabyte of prompt forwarded (capped at `MaxForwardTimeoutSeconds`), because time to first byte scales with the context the backend has to pre-fill — a flat allowance made long-context requests look like a dead backend and opened the breaker on models that were working.

`stream_idle_timeout` covers both response modes despite its name: it means the upstream answered and then stopped sending, with the gap between chunks exceeding `StreamIdleTimeoutSeconds`. It is deliberately *not* counted against the breaker, because the backend has already proved it is reachable and producing.

### Policy and resilience

| Metric | Type | Labels |
|--------|------|--------|
| `gateway_rate_limit_rejections_total` | Counter | `reason` |
| `gateway_quota_rejections_total` | Counter | — |
| `gateway_backend_health` | ObservableGauge | `model` |
| `gateway_circuit_breaker_state` | ObservableGauge | `model` (0=closed, 1=half_open, 2=open) |
| `gateway_circuit_breaker_transitions_total` | Counter | `model`, `to_state` |
| `gateway_bulkhead_rejections_total` | Counter | `model` |
| `gateway_bulkhead_inflight` | UpDownCounter | `model` |

### Usage and billing pipeline

| Metric | Type | Labels |
|--------|------|--------|
| `gateway_tokens_total` | Counter | `model`, `direction` (`input`/`output`/`total`) |
| `gateway_usage_parse_failures_total` | Counter | `model` |
| `gateway_usage_unsplit_total` | Counter | `model` |
| `gateway_usage_estimated_total` | Counter | `model` |
| `gateway_usage_writer_queue_depth` | UpDownCounter | — |
| `gateway_usage_writer_dropped_total` | Counter | — |
| `gateway_billing_reconciliation_discrepancies` | ObservableGauge | — |
| `gateway_billing_reconciliation_cost_drift` | ObservableGauge | — |
| `gateway_billing_reconciliation_runs_total` | Counter | — |

`gateway_usage_unsplit_total` counts responses whose upstream reported only a combined token total — their cost is approximated at the dearer rate, so a persistently non-zero value for one model means that upstream's usage reporting needs checking. `gateway_usage_estimated_total` counts responses billed from a streamed-frame estimate rather than authoritative usage; a rise concentrated on one tenant can indicate deliberate disconnect-before-completion.

ASP.NET Core and Kestrel runtime metrics are exported alongside these by the OTel instrumentation — do not duplicate them with custom RED series.

## Dashboards

Docker Compose auto-provisions dashboards under the Grafana folder **33pol**:

| Dashboard | URL | Source |
|-----------|-----|--------|
| **33pol Gateway** (SRE / RED) | http://localhost:3000/d/33pol-gateway/33pol-gateway | [33pol-gateway.json](../deploy/grafana/dashboards/33pol-gateway.json) |
| **33pol Gateway — Traffic & tokens** | http://localhost:3000/d/33pol-gateway-traffic/33pol-gateway-traffic | [33pol-gateway-traffic.json](../deploy/grafana/dashboards/33pol-gateway-traffic.json) |

**Ops dashboard:** overview stats (RPS, error rate, duration p99, TTFT p95, in-flight requests, active streams, healthy backends, billing discrepancies), RED including time-to-first-token percentiles, streaming/policy with the timeout split, FinOps/usage writer/reconciliation, backend health and circuit state.

**Traffic dashboard:** inference route rate by `route`/`stream`, forward outcomes by `outcome` plus the same outcomes as a percentage mix, timeouts and cancellations, resilience policy, in-flight vs streaming, and token rates.

The two timeout series are kept apart on purpose: `upstream_timeout` is the only one the circuit breaker counts, so a rise in `stream_idle_timeout` alongside a closed breaker is the expected shape, not a contradiction. Watch TTFT p99 against `Gateway:Resilience:ForwardTimeoutSeconds` — the two panels sit side by side because a TTFT distribution creeping toward the allowance is what precedes `upstream_timeout` outcomes.

Use the **Model** variable to filter; the dashboards link to each other and carry the selection and time range across. Rate windows use `$__rate_interval`, so panels stay correct when zoomed. Token metrics are recorded when upstream `usage` is parsed on the inference path (`IUsageRecorder`).

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

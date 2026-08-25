# 33pol Gateway Observability

## Metrics

Prometheus scrape endpoint: `GET /metrics` (OpenTelemetry Prometheus exporter). Meter name: `Pol33.Gateway`.

### Scrape authentication

The exposition carries a `model` label on request, error, latency, stream and token series, so an anonymous scrape enumerates the model inventory and the traffic profile — the same data `/stats` is gated behind the Operator policy to protect. `/metrics` is therefore **not anonymous by default** once the gateway has API keys. A scrape is accepted when it presents any one of:

| Credential | How | Configure |
|---|---|---|
| Scrape token | `Authorization: Bearer <token>` | `Gateway:Metrics:ScrapeToken` — env `Gateway__Metrics__ScrapeToken`; the compose stack maps `GATEWAY_METRICS_SCRAPE_TOKEN` onto it |
| Operator API key | `X-API-Key: <key>` or `Authorization: Bearer <key>` | Any key satisfying the Operator policy (admin role in the operator tenant) |
| Nothing | — | `Gateway:Metrics:AllowAnonymous=true` (explicit opt-in; only when the port is reachable solely from the scraper's network) |

Anything else is answered `401` with the standard `invalid_api_key` error body. With no token configured and `AllowAnonymous=false` (the shipped default) only an Operator key works, and the gateway logs a startup warning saying so. A gateway with authentication disabled (no keys issued / no database) serves the scrape as it serves everything else.

Prometheus side (`deploy/docker/config/prometheus.yml`):

```yaml
  - job_name: gateway
    metrics_path: /metrics
    authorization:
      type: Bearer
      credentials: ${GATEWAY_METRICS_SCRAPE_TOKEN}   # or credentials_file: /run/secrets/gateway_metrics_token
    static_configs:
      - targets: ["gateway:8080"]
```

`/health`, `/health/live` and `/health/ready` stay anonymous for probes. Anonymous callers of `/health` get the summary shape (status, counts, per-backend up/down); the per-backend upstream `url` and probe `error` text are included only when the request carries an Operator key.

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

### .NET runtime

Exported by `OpenTelemetry.Instrumentation.Runtime` (see `GatewayOpenTelemetryExtensions`). Names follow the current OTel semantic conventions (`dotnet_*`), **not** the legacy `process_runtime_dotnet_*` ones — dashboards or alerts copied from older examples will silently match nothing.

| Metric | Type | Labels | Read it for |
|--------|------|--------|-------------|
| `dotnet_process_memory_working_set_bytes` | Gauge | — | Resident memory, against the container limit |
| `dotnet_gc_heap_total_allocated_bytes_total` | Counter | — | Allocation rate; divide by request rate to get bytes allocated per request |
| `dotnet_gc_last_collection_heap_size_bytes` | Gauge | `gc_heap_generation` (`gen0`/`gen1`/`gen2`/`loh`/`poh`) | Where memory actually sits |
| `dotnet_gc_last_collection_heap_fragmentation_size_bytes` | Gauge | `gc_heap_generation` | LOH fragmentation from large short-lived buffers |
| `dotnet_gc_last_collection_memory_committed_size_bytes` | Gauge | — | Committed vs resident divergence |
| `dotnet_gc_collections_total` | Counter | `gc_heap_generation` | Gen2 rate — the expensive collections |
| `dotnet_gc_pause_time_seconds_total` | Counter | — | The link between memory pressure and tail latency |
| `dotnet_thread_pool_queue_length_total`, `dotnet_thread_pool_thread_count_total` | Counter | — | Saturation before it becomes a stall |
| `dotnet_monitor_lock_contentions_total` | Counter | — | Lock contention under concurrency |

**Why this is not optional here.** The gateway buffers, scans and forwards whole request bodies, so heap pressure — not request rate — is what decides whether the process stays inside its memory limit. None of the RED series above move when that goes wrong; the first visible symptom is an OOMKill with no preceding signal.

Two of these carry most of the weight for long-context traffic:

- **`gc_heap_generation="loh"`** — every buffer above 85 KB lands on the Large Object Heap, which is the regime a multi-megabyte body operates in. Total heap size alone hides it.
- **`dotnet_gc_heap_total_allocated_bytes_total`** divided by request rate gives bytes allocated per request. Compare that against mean request body size: the ratio should stay near flat as bodies grow. A ratio that scales with body size means something on the request path is copying it.

`RuntimeMetricsIntegrationTests` pins these names, so an instrumentation removal or a package rename fails the build rather than blanking a dashboard.

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

### In-app Attention list

The admin Overview evaluates the same conditions in-process and lists them under **Attention**, so the console is useful without a monitoring stack (not instead of one). Thresholds live under `Gateway:Overview:Attention` and default to the rule values below.

| Prometheus rule | Attention code | Severity | Default |
|---|---|---|---|
| `GatewayHighErrorRate` | `error_rate_high` | warning | error rate > 5 % over 5 m, ≥ 20 requests, for 5 m |
| `GatewayNoHealthyBackends` | `no_healthy_backends` | critical | every registered model unhealthy, for 2 m |
| — | `backend_unhealthy` | warning | per model, for 2 m |
| `GatewayCircuitBreakerOpen` | `circuit_open` | warning | per model, for 5 m |
| — | `bulkhead_saturated` | warning | in-flight at the ceiling with a queue, for 1 m |
| `GatewayUsageParseFailures` | `usage_parse_failures` | warning | > 0.1/s over 5 m |
| `GatewayUsageWriterQueueHigh` | `usage_writer_backlog` | warning | queue depth > 5000, for 5 m |
| `GatewayUsageWriterDroppedEvents` | `usage_events_dropped` | critical | any drop in the last 5 m |
| `GatewayBillingReconciliationDrift` | `reconciliation_discrepancies` | warning | > 0 buckets, for 15 m |
| `GatewayBillingReconciliationStalled` | `reconciliation_stalled` | warning | last sweep older than 3 h |
| — | `budget_near_limit` / `budget_exceeded` / `budget_hard_stop` | warning / warning / critical | budget warning ratio, exhausted, exhausted with hard stop |
| — | `quota_near_limit` / `quota_exceeded` | info / warning | monthly token quota at the soft ratio / exhausted |
| — | `unpriced_models` | info | registered models with no rate card |
| — | `secrets_undecryptable` | critical | stored upstream credentials that no longer decrypt |
| — | `backup_stale` / `backup_failed` | info / warning | no verified backup in 7 d / last attempt failed |
| — | `key_expiring` / `key_idle` | info | keys expiring within 7 d / unused for 30 d |

## Traces

Sample OpenTelemetry Collector config: [deploy/otel-collector/config.yaml](../deploy/otel-collector/config.yaml).

## Admin APIs

| Endpoint | Purpose |
|----------|---------|
| `GET /admin/api/summary` | Operational snapshot |
| `GET /admin/api/backends` | Registry + health |
| `GET /admin/api/requests?limit=` | Recent requests ring buffer |
| `GET /admin/api/logs?limit=&level=&search=` | In-memory diagnostic tail (warning and above) |
| `DELETE /admin/api/logs` | Empty the diagnostic tail (audited) |
| `GET /admin/api/errors/groups` | Persisted failures grouped by fingerprint, with occurrence counts |
| `GET /admin/api/errors` | Individual occurrences; filter by `fingerprint` or `requestId` |
| `GET /admin/api/errors/{id}` | One occurrence in full, including its stack trace |
| `GET /admin/api/errors/facets` | Filter values present in the window, with counts |
| `GET /admin/api/errors/export?format=json\|csv` | Bulk export of the filtered set |
| `DELETE /admin/api/errors?confirm=true` | Clear records, error counters and the persisted snapshot (audited) |

All require admin API key scope.

**Error tracking** is configured under `Gateway:ErrorTracking` — hot-buffer capacity, tracked
fingerprints, batch-writer size and interval, and retention (`RetentionDays`, `MaxRows`). It
degrades to in-memory-only when no database is configured; the list responses report `persisted:
false` in that case. See `docs/admin-ui.md` for the Logs-versus-Errors split.

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

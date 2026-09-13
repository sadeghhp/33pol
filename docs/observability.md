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

`outcome` distinguishes why a forward ended: `success`, `upstream_error`, `upstream_timeout`, `upstream_first_byte_timeout`, `stream_idle_timeout`, `upstream_body_error`, `client_write_timeout`, `client_canceled`, `backend_unhealthy`, `circuit_open`, `bulkhead_full`, `budget_exceeded`. The outcomes that count as backend ill-health against the circuit breaker are `upstream_error`, `upstream_timeout`, `upstream_first_byte_timeout` and `upstream_body_error` — see `ModelRouterMiddleware`.

`upstream_timeout` means response headers never arrived, so the backend never answered at all. Its allowance is `Gateway:Resilience:ForwardTimeoutSeconds` widened by `ForwardTimeoutSecondsPerRequestMegabyte` for every megabyte of prompt forwarded (capped at `MaxForwardTimeoutSeconds`), because time to first byte scales with the context the backend has to pre-fill — a flat allowance made long-context requests look like a dead backend and opened the breaker on models that were working.

Once headers are in, three independent deadlines govern the rest of the forward, and each has its own outcome.

`upstream_first_byte_timeout` means the upstream answered with headers and then produced no response byte at all. Its allowance is whatever the prompt-scaled header allowance left unused, and never less than `StreamIdleTimeoutSeconds`. An SSE upstream such as vLLM writes its headers on admission — before scheduling, before prefill — so for a streaming request those headers are not evidence that anything works, and the wait for the first token is the wait the header allowance was sized for. Producing nothing for the whole of it is backend ill health (a hung worker, a deadlocked scheduler, a dead GPU process), so **this one counts against the breaker**. The breaker needs both an absolute failure count and a failure ratio over its window, so a backend that is merely busy and still answering other callers does not trip on these.

`stream_idle_timeout` means the upstream produced tokens and then stopped, with the gap between chunks reaching the client exceeding `StreamIdleTimeoutSeconds`. The backend demonstrably produced output, so this stays inconclusive and is *not* counted against the breaker.

`client_write_timeout` means the client held the connection open and stopped reading, so a single write to it exceeded `DownstreamWriteTimeoutSeconds` (default 120). This is the application's own bound, and it exists because backpressure from a stalled consumer reaches all the way to the upstream read: without it the client, not the backend, holds the upstream connection, the per-model bulkhead slot and the budget reservation for as long as it cares to. It says nothing about the backend and is never counted against the breaker, but unlike a clean `client_canceled` it *is* recorded in the Errors tab — a consumer that stalls mid-response is how one caller starves a model's concurrency.

Two response-side writes sit outside that per-write bound, and deliberately so. `Response.StartAsync`, which commits a streaming response's headers, and the small gateway error body the router writes under `!Response.HasStarted` both write well under Kestrel's response buffer threshold (`MaxResponseBufferSize`, 64 KB by default and not changed here) into an *empty* buffer. An ASP.NET Core write completes once the bytes are in that buffer and is deferred only once the buffer already holds more unsent bytes than the threshold, so neither can be stranded by a consumer that stops reading: `StartAsync` runs before the body phase, and nothing can be in the buffer while the response has not started, because the first byte to reach it is what starts the response. `KestrelResponseBackpressureTests` pins all of this against a real Kestrel host with the response data rate disabled, including a control proving backpressure does engage for a larger write. A disconnect ends `StartAsync` promptly because it is passed the client's own abort token.

Kestrel's `MinResponseDataRate` (240 B/s after a 5 s grace, by default) usually aborts such a connection first, and that abort arrives as `client_canceled`. It is a transport-level guard on a different measure — average throughput rather than per-write latency — and it is the host's to configure or remove. Gateway correctness does not depend on it: `DownstreamWriteTimeoutSeconds` bounds the write whether or not the host enforces any rate, so relaxing Kestrel's rate for long-lived SSE clients (a common change) does not reintroduce an unbounded write. There is deliberately no "off" value for it.

The error record says which deadline ran out without needing the outcome name: `responseBytesForwarded` is 0 for a first-byte failure and non-zero for a mid-stream stall, and `timeToFirstTokenMs` is null whenever no byte ever reached the client. Those, plus `streaming` and `upstreamBodySnippet`, are columns in the CSV export.

`request_incomplete` is a request-body failure, recorded before routing, so it carries no model. Its message includes how many bytes arrived against how many the client declared, and over how long: "Unexpected end of request content" with a shortfall is a client (or intermediate proxy) that closed early; "data arriving too slowly" is Kestrel's minimum body data rate, `Gateway:Resilience:MinRequestBodyBytesPerSecond` after `MinRequestBodyDataRateGraceSeconds` (defaults 240 B/s and 5 s, the framework's), which a client that pauses mid-upload trips even when its throughput while sending is fine. Set the rate to 0 to disable the check.

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

The timeout series are kept apart on purpose: `upstream_timeout` and `upstream_first_byte_timeout` are the ones the circuit breaker counts, so a rise in `stream_idle_timeout` alongside a closed breaker is the expected shape, not a contradiction. Watch TTFT p99 against `Gateway:Resilience:ForwardTimeoutSeconds` — the two panels sit side by side because a TTFT distribution creeping toward the allowance is what precedes `upstream_timeout` outcomes.

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
